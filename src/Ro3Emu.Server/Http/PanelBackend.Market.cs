using System.Globalization;
using System.Text.Json;
using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;
using Romsg;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The public market: the exchange (TradeService's listings and sales), the stalls (StallsService), the auction lots
/// (AuctionStore) and the guild materials exchange (GuildStore.MarketState), read from the database on the read pool at
/// most once per <see cref="MarketTtl"/>. The exchange is anonymous in the game (its queries never name a seller), so the
/// panel names no seller there; a stall stands in the street with its owner's name over it, and the panel shows it.
/// </summary>
public sealed partial class PanelBackend
{
    /// <summary>(inferred) How stale the market pages may be: one read of the market tables per interval.</summary>
    private static readonly TimeSpan MarketTtl = TimeSpan.FromSeconds(30);
    private const int MostTradedRows = 10, RecentSales = 30, HistoryDays = 90;

    private sealed record MarketSnapshot(List<TradeService.Listing> Listings, List<TradeService.Sale> Sales, List<StallsService.StallRow> Stalls,
        List<AuctionStore.Item> Auctions, GuildStore.MarketState Guild, long NowMs);

    /// <summary>One offer on sale now: a listing or a stall's stock.</summary>
    private sealed record MarketOffer(long Entry, PanelOffer Offer);

    private Task<MarketSnapshot>? _market;
    private DateTimeOffset _marketAt;

    public async Task<PanelMarketOverview> MarketAsync(string text, int page, int pageSize)
    {
        var (market, catalog) = (await MarketSnapshotAsync(), await CatalogAsync());
        var offers = Offers(market);
        long week = market.NowMs - 7 * 86_400_000L, month = market.NowMs - 30 * 86_400_000L;
        var rows = offers.GroupBy(o => o.Entry)
            .Select(g => (Summary: catalog.ItemSummary(g.Key), Offers: g.Select(o => o.Offer).ToList(), Entry: g.Key))
            .Where(g => g.Summary != null && Matches(g.Summary, text))
            .Select(g =>
            {
                var cheapest = g.Offers.OrderBy(o => o.Currency != CurrencyName(ZenyItem)).ThenBy(o => o.UnitPrice).First();
                var last = market.Sales.Where(s => s.Entry == g.Entry).MaxBy(s => s.TimeMs);
                return new PanelMarketRow(g.Summary!, g.Offers.Count, g.Offers.Sum(o => (long)o.Quantity), cheapest.UnitPrice, cheapest.Currency,
                    last?.UnitPrice ?? 0, last == null ? null : DateTimeOffset.FromUnixTimeMilliseconds(last.TimeMs));
            })
            .OrderBy(r => r.Item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Item.Id).ToList();
        var traded = market.Sales.Where(s => s.TimeMs >= month).GroupBy(s => s.Entry)
            .Select(g => (Summary: catalog.ItemSummary(g.Key), Sales: g.ToList()))
            .Where(g => g.Summary != null)
            .Select(g => new PanelMarketTraded(g.Summary!, g.Sales.Count, g.Sales.Sum(s => (long)s.Quantity),
                (long)Math.Round(g.Sales.Sum(s => (decimal)s.UnitPrice * s.Quantity) / Math.Max(1, g.Sales.Sum(s => (decimal)s.Quantity)))))
            .OrderByDescending(t => t.Quantity).ThenByDescending(t => t.Sales).Take(MostTradedRows).ToList();
        var guild = market.Guild.PriceHistory.Select(p => new PanelPricePoint(DateTimeOffset.FromUnixTimeSeconds(p.DateHour * 3600L), p.Average, p.Average,
            p.Average, 0)).ToList();
        return new PanelMarketOverview([.. rows.Skip((page - 1) * pageSize).Take(pageSize)], rows.Count, page, pageSize, traded,
            market.Listings.Count(l => Open(l, market.NowMs)), market.Stalls.Count(s => !s.Ended && s.Timeout * 1000 > market.NowMs),
            market.Sales.Count(s => s.TimeMs >= week), guild);
    }

    private static bool Matches(PanelItemSummary item, string text) => text.Length == 0
        || item.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || item.Id.ToString(CultureInfo.InvariantCulture) == text;

    public async Task<PanelMarketItem?> MarketItemAsync(long id)
    {
        var (market, catalog) = (await MarketSnapshotAsync(), await CatalogAsync());
        if (catalog.ItemSummary(id) is not { } item) return null;
        var offers = Offers(market).Where(o => o.Entry == id).Select(o => o.Offer).OrderBy(o => o.Currency).ThenBy(o => o.UnitPrice).ToList();
        var sales = market.Sales.Where(s => s.Entry == id).OrderByDescending(s => s.TimeMs).ToList();
        long since = market.NowMs - HistoryDays * 86_400_000L;
        var daily = sales.Where(s => s.TimeMs >= since).GroupBy(s => s.TimeMs / 86_400_000L).OrderBy(g => g.Key)
            .Select(g => new PanelPricePoint(DateTimeOffset.FromUnixTimeMilliseconds(g.Key * 86_400_000L),
                (long)Math.Round(g.Sum(s => (decimal)s.UnitPrice * s.Quantity) / Math.Max(1, g.Sum(s => (decimal)s.Quantity))),
                g.Min(s => s.UnitPrice), g.Max(s => s.UnitPrice), g.Sum(s => (long)s.Quantity)))
            .ToList();
        var lots = market.Auctions.Where(a => a.Entry == id).OrderByDescending(a => a.End).Take(RecentSales)
            .Select(a => new PanelAuctionLot(AuctionKind(a.Type), a.Price, CurrencyName(a.Currency),
                a.State != AuctionState.Settled ? "Bidding" : a.Winner > 0 ? "Sold" : "Unsold",
                DateTimeOffset.FromUnixTimeSeconds(a.Start), DateTimeOffset.FromUnixTimeSeconds(a.End)))
            .ToList();
        return new PanelMarketItem(item, offers, [.. sales.Take(RecentSales).Select(s => new PanelSale(DateTimeOffset.FromUnixTimeMilliseconds(s.TimeMs),
            s.Quantity, s.UnitPrice))], daily, lots);
    }

    /// <summary>What is on sale now: exchange listings not yet expired, and the stock of stalls still open.</summary>
    private List<MarketOffer> Offers(MarketSnapshot market)
    {
        var offers = market.Listings.Where(l => Open(l, market.NowMs)).Select(l => new MarketOffer(l.Entry, new PanelOffer("Exchange", "", "",
            l.UnitPrice, CurrencyName(l.Currency), l.Quantity, DateTimeOffset.FromUnixTimeMilliseconds(l.SaleTimeMs),
            l.ExpiresAtMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(l.ExpiresAtMs) : null,
            l.PublicUntilMs > market.NowMs ? DateTimeOffset.FromUnixTimeMilliseconds(l.PublicUntilMs) : null))).ToList();
        foreach (var stall in market.Stalls.Where(s => !s.Ended && s.Timeout * 1000 > market.NowMs))
            offers.AddRange(stall.Stock.Where(i => i.Item.Stack > 0).Select(i => new MarketOffer(i.Item.Entry, new PanelOffer("Stall",
                stall.SystemId > 0 ? StallTitle(stall) : stall.OwnerName, MapName(stall.Scene), i.Price, CurrencyName(i.Currency), i.Item.Stack,
                DateTimeOffset.FromUnixTimeSeconds(stall.OpenTime), DateTimeOffset.FromUnixTimeSeconds(stall.Timeout), null))));
        return offers;
    }

    private static bool Open(TradeService.Listing l, long nowMs) => l.Quantity > 0 && (l.ExpiresAtMs == 0 || l.ExpiresAtMs > nowMs);

    private static string StallTitle(StallsService.StallRow stall) => stall.Title.Length > 0 ? stall.Title : "Merchant";

    private string CurrencyName(int currency) => currency <= 0 ? "Zeny" : ItemName(currency);

    /// <summary>MG_Auction AuctionType in words.</summary>
    private static string AuctionKind(AuctionType type) => type switch
    {
        AuctionType.Guild => "Guild auction",
        AuctionType.World => "World auction",
        AuctionType.Act => "Event auction",
        AuctionType.Dungeon => "Dungeon auction",
        AuctionType.Territory => "Territory auction",
        _ => "Auction",
    };

    private async Task<MarketSnapshot> MarketSnapshotAsync()
    {
        Task<MarketSnapshot> task;
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (_market == null || _market.IsFaulted || _market.IsCompleted && now - _marketAt >= MarketTtl)
            {
                _market = ReadMarketAsync(now.ToUnixTimeMilliseconds());
                _marketAt = now;
            }
            task = _market;
        }
        return await task;
    }

    private async Task<MarketSnapshot> ReadMarketAsync(long nowMs) => new(
        await MarketRecordsAsync<TradeService.Listing>(TradeService.Listings), await MarketRecordsAsync<TradeService.Sale>(TradeService.History),
        await MarketRecordsAsync<StallsService.StallRow>(StallsService.Kind),
        await accounts.ReadAsync<List<AuctionStore.Item>>(new AuctionReadPlan(AuctionReadKind.All)),
        await accounts.ReadAsync<GuildStore.MarketState?>(new GuildReadPlan(GuildReadKind.Market)) ?? new GuildStore.MarketState(), nowMs);

    /// <summary>A kind of market_record, decoded with its service's own type; a row that does not decode is left out.</summary>
    private async Task<List<T>> MarketRecordsAsync<T>(string kind) where T : class =>
        [.. (await accounts.ReadAsync<List<(string Id, string Data)>>(new MarketRecordReadPlan(kind))).Select(r => Decode<T>(r.Data)).OfType<T>()];

    private static T? Decode<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return null; }
    }
}
