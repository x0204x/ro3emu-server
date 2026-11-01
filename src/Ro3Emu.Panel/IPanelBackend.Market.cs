namespace Ro3Emu.Panel;

/// <summary>
/// The market as the server keeps it: the exchange's listings and its sales (TradeService), the open stalls
/// (StallsService), the auction lots (AuctionStore) and the guild materials exchange's hourly prices (GuildStore
/// MarketState). Public and read only; sellers on the exchange stay anonymous as in the game, a stall shows its owner.
/// </summary>
public partial interface IPanelBackend
{
    /// <summary>Items on offer now, matching <paramref name="text"/> (part of the name or an id), with the market's figures.</summary>
    Task<PanelMarketOverview> MarketAsync(string text, int page, int pageSize);

    /// <summary>One item on the market: its offers, sales, price history and auction lots; null when the item is unknown.</summary>
    Task<PanelMarketItem?> MarketItemAsync(long id);
}

/// <param name="Lowest">The lowest unit price on offer, in <paramref name="Currency"/>.</param>
/// <param name="LastPrice">The unit price of the latest sale on the exchange; 0 when it never sold.</param>
public sealed record PanelMarketRow(PanelItemSummary Item, int Offers, long Quantity, long Lowest, string Currency, long LastPrice,
    DateTimeOffset? LastSold);

/// <param name="Sales">Sales on the exchange in the last 30 days.</param>
public sealed record PanelMarketTraded(PanelItemSummary Item, int Sales, long Quantity, long Average);

/// <param name="Average">The average unit price of the hour or day.</param>
public sealed record PanelPricePoint(DateTimeOffset Time, long Average, long Low, long High, long Quantity);

/// <param name="Listings">Listings on the exchange now.</param>
/// <param name="Stalls">Stalls open now.</param>
/// <param name="Sales">Sales on the exchange in the last 7 days.</param>
/// <param name="GuildMaterials">The guild materials exchange's hourly average price (its own history).</param>
public sealed record PanelMarketOverview(IReadOnlyList<PanelMarketRow> Offers, int Total, int Page, int PageSize,
    IReadOnlyList<PanelMarketTraded> MostTraded, int Listings, int Stalls, int Sales, IReadOnlyList<PanelPricePoint> GuildMaterials);

/// <param name="Where">"Exchange" or "Stall".</param>
/// <param name="Seller">A stall's owner (or its title for a system stall); empty on the exchange, where sellers are anonymous.</param>
/// <param name="OnShowUntil">An exchange listing on show (not yet for sale) until then; null when it is for sale.</param>
public sealed record PanelOffer(string Where, string Seller, string Map, long UnitPrice, string Currency, int Quantity,
    DateTimeOffset Listed, DateTimeOffset? Expires, DateTimeOffset? OnShowUntil);

public sealed record PanelSale(DateTimeOffset Time, int Quantity, long UnitPrice);

/// <param name="Kind">The auction (AuctionType).</param>
/// <param name="State">"Bidding", "Sold" or "Unsold".</param>
public sealed record PanelAuctionLot(string Kind, long Price, string Currency, string State, DateTimeOffset Start, DateTimeOffset End);

/// <param name="Daily">The exchange's sales by UTC day (the last 90 days with a sale).</param>
public sealed record PanelMarketItem(PanelItemSummary Item, IReadOnlyList<PanelOffer> Offers, IReadOnlyList<PanelSale> Sales,
    IReadOnlyList<PanelPricePoint> Daily, IReadOnlyList<PanelAuctionLot> Auctions);
