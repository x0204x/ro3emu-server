using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Player trade house (LC_DAL_Trade.lua). The 326xx query envelopes are distinct from
/// direct mutation replies. Listing assets live in durable escrow; completed goods and proceeds use mail
/// templates 1064/1063 and enter a bag only through claim. MarketStore serializes each transaction.
/// </summary>
public sealed partial class TradeService(
    GameData data, CharacterService character, FunctionService functions, RoleStore roles,
    SeasonService seasons, MarketStore market, SessionRegistry sessions, TimeProvider clock, ILogger<TradeService> log)
{
    public const uint TradeSync = 5033;
    public const uint QueryProduct = 32602, QueryInventory = 32603, FindPrice = 32604, BuyHistory = 32605,
        SaleHistory = 32606, OwnBooth = 32607, Credit = 32608, RecentPrice = 32609, Search = 32610,
        BuyTop = 32612, PutOn = 32613, UnlockSlot = 32614, PutOff = 32615, Follow = 32616,
        Refresh = 32617, QuerySkus = 32618, BuyNormal = 32619, Reprice = 32620, QueryAllInventory = 32621,
        Penalties = 32623, MultiQuery = 32625, Preorder = 32626, QueryPreorders = 32628, RefundPreorder = 32629,
        InQueue = 32622;

    internal const string Listings = "trade_listing", History = "trade_history", Orders = "trade_preorder";
    private const int TaxBasisPoints = 900; // TradeConfigTax._kTaxZeny/_kTaxMiao; currency 1 rule is inferred.

    public sealed class Listing
    {
        public string Sku { get; set; } = "";
        public string Spu { get; set; } = "";
        public long SellerId { get; set; }
        public int Entry { get; set; }
        public int Currency { get; set; }
        public int UnitPrice { get; set; }
        public int Quantity { get; set; }
        public long SaleTimeMs { get; set; }
        public long PublicUntilMs { get; set; }
        public long ExpiresAtMs { get; set; }
        public int SpecialType { get; set; }
        public InvItem Item { get; set; } = new();
    }

    public sealed class Sale
    {
        public string Id { get; set; } = "";
        public string Sku { get; set; } = "";
        public string Spu { get; set; } = "";
        public long SellerId { get; set; }
        public long BuyerId { get; set; }
        public int Entry { get; set; }
        public int Quantity { get; set; }
        public int UnitPrice { get; set; }
        public long TimeMs { get; set; }
        public InvItem Item { get; set; } = new();
    }

    public sealed class PreorderRow
    {
        public string Id { get; set; } = "";
        public string Sku { get; set; } = "";
        public string Spu { get; set; } = "";
        public long BuyerId { get; set; }
        public int Entry { get; set; }
        public int Quantity { get; set; }
        public int Price { get; set; }
        public int Currency { get; set; }
        public long TimeMs { get; set; }
        public long Sequence { get; set; }
        public int Status { get; set; }
        public bool Hidden { get; set; }
        public InvItem Item { get; set; } = new();
    }

    private long NowMs => clock.GetUtcNow().ToUnixTimeMilliseconds();
    private static string NewId() => Guid.NewGuid().ToString("N"); // (inferred) opaque durable ID; original allocator is unknown.

    /// <summary>Localization_en 1063/1064 use sparse 1-based slots. UTC text formatting is (inferred).</summary>
    private string[] SaleMailParams(Sale sale, long amount, bool seller)
    {
        var values = new string[8];
        Array.Fill(values, "");
        values[0] = data.ItemName(sale.Entry); // ${1}
        values[4] = amount.ToString(CultureInfo.InvariantCulture); // ${5}
        if (seller) values[6] = sale.Id; // ${7}
        values[7] = DateTimeOffset.FromUnixTimeMilliseconds(sale.TimeMs).UtcDateTime
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); // ${8}
        return values;
    }

    /// <summary>5033 full trade preferences, sent after role/base/item state.</summary>
    public async Task LoginAsync(GameSession s)
    {
        // C/D 15: no market maintenance inside the login (it reserved the market collections and every participant).
        // The realm's own maintenance runs every second from startup (StallsService.MaintainAsync), so escrow that
        // matured during downtime settles independently of who logs in.
        var saved = roles.LoadTrade(s.Role!.Id);
        var state = saved is { Length: > 0 } ? DBUserTradeInfo.Parser.ParseFrom(saved) : new DBUserTradeInfo
        {
            LastRefreshTime = NowMs, LastClientRefreshTime = NowMs,
            CreditPoint = (int)Public("TradeCreditDefault", 1000),
            StallsNum = 6, CreditScoreRatio = 1, // (inferred) initial six free TradeConfigUnlockPlace places.
        };
        if (state.LastRefreshTime == 0) state.LastRefreshTime = NowMs; // LC_DAL_Trade full sync divides this field.
        s.Trade = state;
        if (saved == null) roles.SaveTrade(s.Role.Id, state.ToByteArray());
        await CharacterService.SyncAsync(s, TradeSync, state.Clone());
    }

    public async Task<bool> HandleAsync(GameSession s, uint id, ReadOnlyMemory<byte> body)
    {
        if (id is < 32601 or > 32630) return false;
        await TickAsync(s);
        var bytes = body.ToArray();
        switch (id)
        {
            case QueryProduct: await QueryProducts(s, SQueryProductByParamRequest.Parser.ParseFrom(bytes)); break;
            case QueryInventory: await Inventory(s, QueryItemInventoryInfoByItemIdRequest.Parser.ParseFrom(bytes)); break;
            case FindPrice: await Price(s, FindItemPriceRequest.Parser.ParseFrom(bytes)); break;
            case BuyHistory: await Buys(s, QueryBuyTradeInfoRequest.Parser.ParseFrom(bytes)); break;
            case SaleHistory: await Sales(s, CQuerySaleTradeInfoRequest.Parser.ParseFrom(bytes)); break;
            case OwnBooth: await Booth(s); break;
            case Credit: await CreditScore(s, CFindUserCreditScoreRequest.Parser.ParseFrom(bytes)); break;
            case RecentPrice: await Recent(s, CFindSpuRecentTransationPriceRequest.Parser.ParseFrom(bytes)); break;
            case Search: await SearchTop(s, CSearchTopPropsRequest.Parser.ParseFrom(bytes)); break;
            case BuyTop: await Buy(s, TradeBuyItemsReq.Parser.ParseFrom(bytes)); break;
            case PutOn: await List(s, TradePutOnItemReq.Parser.ParseFrom(bytes)); break;
            case UnlockSlot: await Unlock(s); break;
            case PutOff: await Delist(s, TradePutOffItemReq.Parser.ParseFrom(bytes)); break;
            case Follow: await Attention(s, TradeAttionReq.Parser.ParseFrom(bytes)); break;
            case Refresh: await RefreshMarket(s); break;
            case QuerySkus: await Skus(s, QueryProductBySkuIdReq.Parser.ParseFrom(bytes)); break;
            case BuyNormal: await Buy(s, TradeBuyNormalItemsReq.Parser.ParseFrom(bytes)); break;
            case Reprice: await ChangePrice(s, ChangeSkuPriceReq.Parser.ParseFrom(bytes)); break;
            case QueryAllInventory: await AllInventory(s, CQueryAllItemInventoryInfoByItemIdRequest.Parser.ParseFrom(bytes)); break;
            case Penalties: await PenaltyOrders(s, CQueryPenaltyOrdersRequest.Parser.ParseFrom(bytes)); break;
            case MultiQuery: await Multi(s, SQueryMultiProductByParamRequest.Parser.ParseFrom(bytes)); break;
            case Preorder: await Reserve(s, CPreTradeTopRequest.Parser.ParseFrom(bytes)); break;
            case QueryPreorders: await Preorders(s, CPreTradeTopQueryRequest.Parser.ParseFrom(bytes)); break;
            case RefundPreorder: await Refund(s, CRefundPreTradeTopRequest.Parser.ParseFrom(bytes)); break;
            default: return false;
        }
        return true;
    }

    private long Public(string key, long fallback) => data.Table("PublicParamConfig").TryGetValue(key, out var value)
        ? (long)(GameData.Number(value) ?? fallback) : fallback;

    private JsonElement? Eligible(int entry)
    {
        if (data.Row("TradeConfig", entry) is not { } row || GameData.Int(row, "_iTrade") != 1) return null;
        foreach (var key in new[] { "_kGameSeason", "_kMultiverseArray" })
            if (row.TryGetProperty(key, out var values) && values.ValueKind == JsonValueKind.Array
                && values.EnumerateArray().Select(v => (int)(GameData.Number(v) ?? 0)).ToArray() is { Length: > 0 } allowed
                && !allowed.Contains(0) && !allowed.Contains(seasons.PlayBook)) return null;
        return row;
    }

    private (int Min, int Max, int Base, int[] Ladder) PriceRule(JsonElement row)
    {
        int basis = (int)Math.Max(1, GameData.Int(row, "_iBasePrice"));
        int ceiling = (int)Math.Min(int.MaxValue, Public("TradeLimitPrice", 999_999_999));
        // (inferred) _kStableRange [5,15] are tenths of the basic price. The hidden backend's
        // market-sensitive computation is unavailable; this deterministic config range keeps repricing useful.
        var range = row.TryGetProperty("_kStableRange", out var r) && r.ValueKind == JsonValueKind.Array && r.GetArrayLength() >= 2
            ? r : default;
        double lo = range.ValueKind == JsonValueKind.Array ? GameData.Number(range[0]) ?? 10 : 10;
        double hi = range.ValueKind == JsonValueKind.Array ? GameData.Number(range[1]) ?? 10 : 10;
        int min = Math.Clamp((int)Math.Ceiling(basis * lo / 10), 1, ceiling);
        int max = Math.Clamp((int)Math.Floor(basis * hi / 10), min, ceiling);
        int step = Math.Max(1, basis / 10);
        var ladder = Enumerable.Range(0, Math.Min(21, (max - min) / step + 1)).Select(n => min + n * step).Where(p => p <= max).ToList();
        if (!ladder.Contains(basis) && basis >= min && basis <= max) ladder.Add(basis);
        if (!ladder.Contains(max)) ladder.Add(max);
        return (min, max, Math.Clamp(basis, min, max), ladder.Distinct().Order().ToArray());
    }

    private bool ValidPrice(JsonElement row, int price) => price >= PriceRule(row).Min && price <= PriceRule(row).Max;

    private int SpecialTypeOf(InvItem item)
    {
        long type = data.Row("ItemConfig", item.Entry) is { } row ? GameData.Int(row, "_iType") : 0;
        return type switch
        {
            5 or 7 => (item.Attrs?.GetValueOrDefault(3) ?? 0) == 1
                ? (int)ETradeItemSpecialType.Appraisal : (int)ETradeItemSpecialType.NotAppraisal,
            4 => (item.Attrs?.GetValueOrDefault(530) ?? 0) == 1
                ? (int)ETradeItemSpecialType.Collection : (int)ETradeItemSpecialType.NotCollection,
            _ => (int)ETradeItemSpecialType.None,
        };
    }

    /// <summary>
    /// Shared per-instance tradeability gate for market and world stalls. Mirrors
    /// LC_TradeHelper.CheckCanTransactionByItem (349-573) plus server ownership protection.
    /// Static _iTrade eligibility belongs to each caller's own market/stall rule.
    /// </summary>
    internal int TransactionItemError(GameSession s, InvItem item)
    {
        if (item.Grid != 0) return ErrorCodes.EquipNotInBag;
        if (item.IsLocked) return ErrorCodes.ItemLocked; // (inferred) decomposition lock also blocks escrow.
        long nowSeconds = clock.GetUtcNow().ToUnixTimeSeconds();
        if (item.BindType == 1 || item.BindType == 2 && item.BindExpire > nowSeconds)
            return ErrorCodes.TradeBound;
        if (item.Attrs?.GetValueOrDefault(10001) > 0) return ErrorCodes.TradeRequest;
        if (TotemService.IsReserved(s, item)) return ErrorCodes.TradeRequest;
        if (data.Row("ItemConfig", item.Entry) is not { } row) return ErrorCodes.ItemNoConfig;
        long type = GameData.Int(row, "_iType");
        if (type == 4 && ((item.Attrs?.GetValueOrDefault(501) ?? 0) > 0
            || (item.Attrs?.GetValueOrDefault(502) ?? 0) > 0)) return ErrorCodes.TradeCardDeveloped;
        if (type == 8 && ((item.Attrs?.GetValueOrDefault(1001) ?? 0) > 0
            || (item.Attrs?.GetValueOrDefault(1003) ?? 0) > 0)) return ErrorCodes.TradeEmblemDeveloped;
        if (type == 5 && (item.Attrs?.GetValueOrDefault(3) ?? 0) > 0)
        {
            int entries = item.Attrs!.Count(a => a.Key is >= 100 and <= 119 && a.Value > 0);
            int specials = item.Attrs!.Count(a => a.Key is >= 80 and <= 89 && a.Value > 0);
            // PublicParam.EquipIfGem = [[2,0],[0,1]], LC_EquipmentHelper.CheckEquipItemIsRare.
            if (entries < 2 && specials < 1) return ErrorCodes.TradeAppraised;
        }
        if (type == 7 && (item.Attrs?.GetValueOrDefault(3) ?? 0) > 0)
        {
            // (inferred) Server currently lacks a full totem talent-skill classifier. Require
            // an explicit skill roll rather than letting an ordinary appraised totem through.
            if (item.Attrs!.All(a => a.Key is < 240 or > 249 || a.Value == 0)) return ErrorCodes.TradeRequest;
        }
        return 0;
    }
    private bool Visible(Listing row, bool publicity) => (row.PublicUntilMs > NowMs) == publicity;
    private static string Spu(int entry) => "spu-" + entry.ToString(CultureInfo.InvariantCulture); // (inferred) opaque generated SPU.
    private static int Entry(string text) => int.TryParse(text, CultureInfo.InvariantCulture, out int n) ? n : 0;
    private static int Page(int n) => Math.Clamp(n <= 0 ? 1 : n, 1, 100000);
    private static int Size(int n) => Math.Clamp(n <= 0 ? 10 : n, 1, 100);

    private CCommodityInfo Commodity(Listing l)
    {
        var info = character.ItemInfoOf(l.Item);
        var config = Eligible(l.Entry);
        return new CCommodityInfo
        {
            ItemId = l.Entry.ToString(CultureInfo.InvariantCulture), SkuId = l.Sku, SpuId = l.Spu,
            SpuName = config is { } row && row.TryGetProperty("_kSpuName", out var name)
                && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "",
            // Commodity times are Unix seconds: LC_DAL_Trade._parseCommodity copies them unscaled and the trade cells compare
            // them with TimerManager:GetSystemTime() (seconds); suspension_time is the listing end ("Expired" when past).
            Quantity = l.Quantity, SaleTime = l.SaleTimeMs / 1000, PutOnSaleTime = l.SaleTimeMs / 1000,
            SuspensionTime = l.ExpiresAtMs / 1000, TakeDownTime = l.ExpiresAtMs / 1000,
            PublicityTime = l.PublicUntilMs / 1000, UserRoleId = l.SellerId.ToString(CultureInfo.InvariantCulture),
            CurrencyType = l.Currency, ItemPrice = l.UnitPrice, ItemBaseInfo = ByteString.CopyFrom(info.ToByteArray()),
            ItemType = config is { } c ? (int)GameData.Int(c, "_iType") : 0,
            ItemSpecialType = (ETradeItemSpecialType)l.SpecialType, Status = 1,
            RecommendedPrice = config is { } cfg ? PriceRule(cfg).Base : l.UnitPrice,
        };
    }

    private static List<Listing> Active(MarketTransaction tx) => [.. tx.All<Listing>(Listings).Where(l => l.Quantity > 0)];
}
