using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The common shop engine: NPC, point, card (class 220) and tower (240) shops
/// all buy through 82332 / 82340 against the client's ShopConfig + ShopCommodityConfig catalogue. The server resolves
/// payment and goods from the commodity (the request's price is only a quote), pays and grants in one bag transaction,
/// and keeps the personal purchase counters and favourites in 5024 DBShopInfo. Fashion-tab commodities (shop type 7)
/// carry _iAppearanceId instead of _iItemId: WardrobeService.BuyFromShopLockedAsync unlocks the look in the same commit.
/// </summary>
public sealed class ShopService : IGameTicker
{
    public const uint ShopSync = 5024;
    public const uint BuyCommodity = 82332, CommodityCollect = 82333, CommodityPresent = 82335, CommodityGetInfo = 82338,
        BatchBuyCommodity = 82340;

    private readonly GameData data;
    private readonly CharacterService character;
    private readonly FunctionService functions;
    private readonly RoleStore roles;
    private readonly SeasonService seasons;
    private readonly WardrobeService wardrobe;
    private readonly AwardService awards;
    private readonly TimeProvider clock;
    private readonly SessionRegistry sessions;
    private readonly GameLoop loop;
    private readonly ILogger<ShopService> log;

    public ShopService(GameData data, CharacterService character, FunctionService functions, RoleStore roles, SeasonService seasons,
        WardrobeService wardrobe, AwardService awards, TimeProvider clock, SessionRegistry sessions, GameLoop loop,
        ILogger<ShopService> log)
    {
        this.data = data;
        this.character = character;
        this.functions = functions;
        this.roles = roles;
        this.seasons = seasons;
        this.wardrobe = wardrobe;
        this.awards = awards;
        this.clock = clock;
        this.sessions = sessions;
        this.loop = loop;
        this.log = log;
    }

    private TimeSpan lastTick;
    private DateTime gameDay;

    /// <summary>
    /// At the 05:00 game-day rollover the online roles get 5024 again: their daily (and on Monday weekly, on the 1st
    /// monthly) counters now count as zero, and the client only learns that from the sync (LC_DAL_Shop keeps the map).
    /// </summary>
    public void Tick(GameTime time)
    {
        if (time.Now - lastTick < TimeSpan.FromSeconds(1)) return;
        lastTick = time.Now;
        var day = DailyActivityService.GameDay(clock.GetLocalNow().DateTime);
        if (day == gameDay) return;
        bool first = gameDay == default;
        gameDay = day;
        if (first) return;
        foreach (var session in sessions.All())
            if (session.Role != null && session.Shop is { Bought.Count: > 0 })
                loop.PostOwned(session, () => session.Shop != null && session.Role != null ? SyncAsync(session) : Task.CompletedTask);
    }

    /// <summary>Saved per role: purchase counters with the period they were counted in, and favourites.</summary>
    public sealed class ShopState
    {
        public Dictionary<int, Counter> Bought { get; set; } = new();
        public HashSet<int> Favorites { get; set; } = new();
    }

    public sealed class Counter
    {
        public int Count { get; set; }
        public string Period { get; set; } = "";
    }

    // ---- login / sync ----

    public async Task LoginAsync(GameSession session)
    {
        var saved = roles.LoadShop(session.Role!.Id);
        session.Shop = saved != null ? JsonSerializer.Deserialize<ShopState>(saved) ?? new ShopState() : new ShopState();
        // Before explicit rollover existed all seasonal counters used the empty lifetime key in season 1.
        // Preserve that purchase history in cycle 1; a later cycle naturally resets its seasonal allowance.
        bool migrated = false;
        foreach (var (id, counter) in session.Shop.Bought)
            if (counter.Period == "" && data.Row("ShopCommodityConfig", id) is { } row && Family(row) == 5)
            { counter.Period = "S1"; migrated = true; }
        if (migrated) Save(session);
        await SyncAsync(session);
    }

    private Task SyncAsync(GameSession session)
    {
        var info = new DBShopInfo { ShoppingCartProtocolVersion = 0 }; // 0: the client keeps its own appearance cart
        foreach (var (id, counter) in session.Shop!.Bought)
            if (Current(session, id, counter) is var n and > 0) info.CommodityBuyTimes[id] = n;
        foreach (var id in session.Shop.Favorites) info.CollectCommodities[id] = true; // removed favourites are deleted keys
        return CharacterService.SyncAsync(session, ShopSync, info);
    }

    private void Save(GameSession session) => roles.SaveShop(session.Role!.Id, JsonSerializer.SerializeToUtf8Bytes(session.Shop));

    // ---- buying ----

    /// <summary>82332: one commodity, count units.</summary>
    public async Task BuyAsync(GameSession session, BuyCommodityReq req)
    {
        var (result, price, buyTimes) = await BuyOneAsync(session, req.CommodityId, req.Count, req.Price);
        if (result == 0) await SyncAsync(session);
        await session.SendGameAsync(BuyCommodity, new BuyCommodityResp
        {
            Result = result, CommodityId = req.CommodityId, Count = req.Count, Price = price, BuyTimes = buyTimes,
        });
    }

    /// <summary>82340: several commodities, each with its own outcome.</summary>
    public async Task BatchBuyAsync(GameSession session, BatchBuyCommodityReq req)
    {
        var resp = new BatchBuyCommodityResp();
        foreach (var item in req.Items)
        {
            var (result, price, buyTimes) = await BuyOneAsync(session, item.CommodityId, item.Count, item.Price);
            resp.Results.Add(new BatchBuyCommodityItemResult { CommodityId = item.CommodityId, Result = result, Count = item.Count, Price = price, BuyTimes = buyTimes });
        }
        // The aggregate code of a batch with failed items is not recovered: the first item's own code (inferred).
        resp.Result = resp.Results.Count == 0 ? ErrorCodes.Parameter : resp.Results.FirstOrDefault(r => r.Result != 0)?.Result ?? 0;
        if (resp.Results.Any(r => r.Result == 0)) await SyncAsync(session);
        await session.SendGameAsync(BatchBuyCommodity, resp);
    }

    private async Task<(int Result, int Price, int BuyTimes)> BuyOneAsync(GameSession session, int commodityId, int count, int quote)
    {
        using var guard = await CharacterService.Lock(session);
        var row = data.Row("ShopCommodityConfig", commodityId);
        Fail? error = row is not { } c ? new(ErrorCodes.ShopNoConfig, "unknown commodity")
            : count < 1 ? new(ErrorCodes.Parameter, "bad count")
            : Unavailable(session, c);
        int unit = row is { } r ? UnitPrice(session, r) : 0;
        int bought = row is { } r2 ? Current(session, commodityId, session.Shop!.Bought.GetValueOrDefault(commodityId)) : 0;
        if (error == null && GameData.Int(row!.Value, "_iPurchaseNum") is var limit and > 0 && (long)bought + count > limit) error = new(ErrorCodes.PurchaseLimit, $"limit {bought}/{limit}");
        if (error == null && (long)bought + count > int.MaxValue) error = new(ErrorCodes.Parameter, "counter overflow");
        int appearance = row is { } r3 ? (int)GameData.Int(r3, "_iAppearanceId") : 0;
        if (error == null) error = Malformed(row!.Value, appearance, count, unit);
        if (error == null && appearance > 0)
        {
            int currency = PaymentItem(row!.Value);
            var cost = currency > 0 && unit > 0 ? new[] { (currency, (long)unit) } : [];
            var next = Copy(session.Shop!);
            next.Bought[commodityId] = new Counter { Count = bought + 1, Period = Period(Family(row.Value), session) };
            long awardGroup = GameData.Int(row.Value, "_iAppearanceAwardId");
            AwardPlan? bonus = null;
            try { bonus = awards.Resolve(session, awardGroup, (int)(session.SeasonState?.PlayBookId ?? seasons.PlayBook)); }
            catch (Exception e) when (e is AwardResolutionException or OverflowException)
            { error = new(ErrorCodes.ShopRewardMissing, $"award {awardGroup}: {e.Message}"); }
            if (bonus != null)
            {
                int code = await wardrobe.BuyFromShopLockedAsync(session, appearance, Parts(row.Value), cost, bonus,
                    persistShop: tx => tx.RoleBlob(session.Role!.Id, "shop", JsonSerializer.SerializeToUtf8Bytes(next)),
                    committedShop: () => session.Shop = next,
                    shopOutcome: new
                    {
                        Commodity = commodityId, Count = 1, Unit = unit, Payment = currency, Appearance = appearance,
                        Before = bought, Counter = next.Bought[commodityId]
                    });
                if (code != 0) error = new(code, $"appearance {appearance}");
            }
        }
        else if (error == null)
        {
            int currency = PaymentItem(row!.Value), entry = (int)GameData.Int(row.Value, "_iItemId"), perUnit = (int)Math.Max(1, GameData.Int(row.Value, "_iItemPerMount"));
            var cost = currency > 0 ? new[] { (currency, (long)unit * count) } : [];
            if ((long)perUnit * count > int.MaxValue) error = new(ErrorCodes.Parameter, "quantity overflow");
            else
            {
                var next = Copy(session.Shop!);
                next.Bought[commodityId] = new Counter { Count = bought + count, Period = Period(Family(row.Value), session) };
                var economy = NpcPurchase(session, row.Value, commodityId, count, unit, currency, entry,
                    perUnit, bought, next.Bought[commodityId]) ?? character.FamilyEconomy(session, "cd13a/shop-buy",
                        cost, [(entry, perUnit * count)], new
                        {
                            Commodity = commodityId, Count = count, Unit = unit,
                            Payment = currency, Goods = entry, Before = bought, Counter = next.Bought[commodityId]
                        });
                // The new-cycle allowance and its payment must commit together; a failed save cannot grant a free retry.
                if (await character.ExchangeLockedAsync(session, cost, [(entry, perUnit * count)],
                    persist: tx =>
                    {
                        tx.RoleBlob(session.Role!.Id, "shop", JsonSerializer.SerializeToUtf8Bytes(next));
                        tx.WithEconomy(economy);
                    },
                    committed: () => session.Shop = next, asyncCommit: true) == null)
                    error = new(character.ShortageOf(session, cost), "payment short");
            }
        }
        log.LogInformation("{Name} buys {Count}x commodity {Id} at {Unit} (quote {Quote}): {Result}", session.Role!.Name, count, commodityId, unit, quote, error?.Why ?? "ok");
        if (error is { } fail) return (fail.Code, unit, bought);

        return (0, unit, session.Shop!.Bought[commodityId].Count);
    }

    /// <summary>
    /// 82338 COMMODITY_GET_INFO: the commodity sale windows of a shop type, map commodity id -> {beginTime, endTime}.
    /// The Fashion tab (UI_Sys_AppearanceShopWidgetCtrl.OnOpen, UI_Sys_AppearanceShopWidgetCtrl.lua:595-603) sends it on
    /// every open and builds its categories and items only when the answer lands: LC_DAL_Shop.CommodityGetInfoResp
    /// (LC_DAL_Shop.lua:1246-1263) replaces the "CommodityTimeMap" asset, whose binding (lua:194-196) calls
    /// _refreshLimitItemList -> OnRefreshByShopType (lua:1076-1350). No answer, no tabs and no items. The map only matters
    /// for commodities with both _kStartTime and _kEndTime (IsValidTime, lua:2868-2920); no ShopCommodityConfig row of
    /// this client has either (or a _kTimeLimit), so every shop type gets an empty map. Answered here, not by the generic
    /// empty reply, so the tab never depends on the message table being present (a fresh realm's DataDir).
    /// </summary>
    /// AppearanceShopTests pins that no row is dated.
    public Task CommodityInfoAsync(GameSession session, CommodityGetInfoReq req) =>
        session.SendGameAsync(CommodityGetInfo, new CommodityGetInfoResp());

    /// <summary>
    /// 82335 COMMODITY_PRESENT (gift a Fashion commodity to a friend) is refused until gifting exists. On any zero result
    /// LC_DAL_Shop.CommodityPresentResp (LC_DAL_Shop.lua:1178-1216) pops "gift sent" and posts a gift card in the
    /// friend's private chat, so the generic empty reply (result 0) faked a gift nobody paid for or received. A non-zero
    /// result takes LC_BaseDAL.DecodeError (LC_BaseDAL.lua:536-663) to the ErrorCodeConfig tip and skips that handler.
    /// 2317 "This shop does not allow gifts" is the inferred code. Nothing is charged, granted, counted or saved.
    /// </summary>
    public async Task RefusePresentAsync(GameSession session, CommodityPresentReq req)
    {
        log.LogInformation("{Name} tries to gift commodity {Id} to {Target}: refused, gifting is not implemented",
            session.Role!.Name, req.BuyReq?.CommodityId ?? 0, req.TargetId);
        await session.SendGameAsync(CommodityPresent, new CommodityPresentResp
        {
            Result = ErrorCodes.ShopGiftNotAllowed, CommodityId = req.BuyReq?.CommodityId ?? 0, TargetId = req.TargetId,
            GiftContent = req.GiftContent,
        });
    }

    /// <summary>
    /// E01: one server-admitted NPC purchase (ShopConfig type 3) authorizes its exact payment and
    /// goods. The existing wire has no retry token: equal client requests remain distinct purchases. The captured
    /// operation/source survives an internal retry of the detached plan; other shops use the E05a family envelope.
    /// </summary>
    private EconomyMutation? NpcPurchase(GameSession session, JsonElement row, int commodity, int count,
        int unit, int payment, int entry, int perUnit, int priorCount, Counter next)
    {
        long classId = GameData.Int(row, "_iClassID");
        var shop = data.Table("ShopConfig").Values.FirstOrDefault(s => GameData.Int(s, "_iClassID") == classId);
        if (shop.ValueKind != JsonValueKind.Object || GameData.Int(shop, "_iShopType") != 3) return null;
        EconomyAsset Asset(int id) => new(GameData.Int(data.Row("ItemConfig", id)!.Value, "_iType") == 1
            ? EconomyAssetKind.Currency : EconomyAssetKind.Item, id);
        string operation = Guid.NewGuid().ToString("N");
        string config = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.GetRawText() + "\n" + shop.GetRawText())));
        // Quote is diagnostic only in the original service; the resolved server price is the semantic input.
        string fingerprint = JsonSerializer.Serialize(new
        {
            Version = 1, Commodity = commodity, Count = count,
            UnitPrice = unit, Payment = payment, Goods = entry, PerUnit = perUnit, Config = config
        });
        byte[] outcome = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1, Result = 0, Commodity = commodity,
            Count = count, Price = unit, BuyTimes = next.Count, BeforeBuyTimes = priorCount, next.Period, Config = config
        });
        EconomyQuantity[] costs = payment > 0 && unit > 0 ? [new(Asset(payment), checked((long)unit * count))] : [];
        return new EconomyMutation(operation, session.Account!, session.Role!.Id, "npc-shop-buy", fingerprint,
            "npc-shop/" + session.Role.Id.ToString(CultureInfo.InvariantCulture) + "/" + operation,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), costs, [new(Asset(entry), checked((long)perUnit * count))], outcome);
    }

    /// <summary>Why a commodity cannot be bought now, or null: its shop's function, the buy/lock conditions, the variant.</summary>
    private Fail? Unavailable(GameSession session, JsonElement commodity)
    {
        if (session.SeasonState is { } state && state.SeasonId != seasons.SeasonId)
            return new(ErrorCodes.ShopClosed, "season changed; login required");
        long classId = GameData.Int(commodity, "_iClassID");
        var shop = data.Table("ShopConfig").Values.FirstOrDefault(s => GameData.Int(s, "_iClassID") == classId);
        if (shop.ValueKind != JsonValueKind.Object) return new(ErrorCodes.ShopMissing, "no shop");
        if (GameData.Int(shop, "_iFunctionId") is var function and > 0 && !functions.IsOpen(session, function)) return new(ErrorCodes.ShopClosed, $"function {function} closed");
        foreach (var gate in new[] { "_iIsBuy", "_iIsLock" })
            if (GameData.Int(commodity, gate) is var cond and > 0 && !functions.ConditionMet(session, cond)) return new(ErrorCodes.PurchaseRequirements, $"{gate} {cond}");
        // _kMultiverseArray: [0] in every season, else only in the listed playbooks (the current one: SeasonService).
        if (commodity.TryGetProperty("_kMultiverseArray", out var variants) && variants.ValueKind == JsonValueKind.Array
            && variants.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToList() is { Count: > 0 } books
            && !books.Contains(0) && !books.Contains(session.SeasonState?.PlayBookId ?? seasons.PlayBook))
            return new(ErrorCodes.NotForSale, "other season");
        return null;
    }

    /// <summary>
    /// The price the buy dialog quotes (GetCommodityPrice of UI_Sys_Shop_NpcListWidgetCtrl.lua:955-997 and
    /// UI_Sys_Shop_CommodityListWidgetCtrl.lua:960-997, which feed UI_Com_Shop_BuyTipsPanelCtrl.m_kPrice): the base
    /// _kItemPerPrice amount times _iDiscount/10000 when positive, times (1 - merchant discount/10000) when the commodity
    /// has _iMerchantDiscount, rounded up when either applied. Lua's double arithmetic is kept so the quote matches.
    /// </summary>
    private int UnitPrice(GameSession session, JsonElement commodity)
    {
        long amount = PricePart(commodity, 1);
        long discount = GameData.Int(commodity, "_iDiscount");
        long merchant = GameData.Int(commodity, "_iMerchantDiscount") > 0 ? MerchantDiscount(session) : 0;
        if (discount <= 0 && merchant <= 0) return (int)amount;
        double price = amount;
        if (discount > 0) price *= discount / 10000.0;
        if (merchant > 0) price *= 1 - merchant / 10000.0;
        return (int)Math.Ceiling(price);
    }

    /// <summary>
    /// LC_DAL_Shop.OnGetMerchantDiscount (lua:858-895): the learned level of the BuyLow profession skill
    /// (PublicParamConfig.DiscountMerchantSkill 16001), and the second _kSpSkillParam of that level's SkillConfig row,
    /// in basis points (1600101 [2,100] = 1% ... 1600110 [2,1000] = 10%). 0 when not learned.
    /// </summary>
    private long MerchantDiscount(GameSession session)
    {
        int skill = data.Table("PublicParamConfig").TryGetValue("DiscountMerchantSkill", out var p) ? (int)(GameData.Number(p) ?? 16001) : 16001;
        int level = session.Progress?.Skills.GetValueOrDefault(skill) ?? 0;
        if (level <= 0) return 0;
        var row = data.Table("SkillConfig").Values.FirstOrDefault(r => GameData.Int(r, "_iSkillID") == skill && GameData.Int(r, "_iLevel") == level);
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("_kSpSkillParam", out var param)
            || param.ValueKind != JsonValueKind.Array || param.GetArrayLength() < 2) return 0;
        return Math.Clamp((long)(GameData.Number(param[1]) ?? 0), 0, 10000);
    }

    /// <summary>_kItemPerPrice is one flat [payment item, amount] pair.</summary>
    private static int PaymentItem(JsonElement commodity) => (int)PricePart(commodity, 0);

    private static long PricePart(JsonElement commodity, int index) =>
        commodity.TryGetProperty("_kItemPerPrice", out var price) && price.ValueKind == JsonValueKind.Array && price.GetArrayLength() > index
            ? (long)(GameData.Number(price[index]) ?? 0) : 0;

    /// <summary>
    /// A commodity whose configured payment or goods the server cannot pay or grant is refused before any economy
    /// envelope is built (an envelope over an unknown ItemConfig entry used to throw and drop the gate connection).
    /// Survivor Battle shop rows (type 8) name match-only items that ItemConfig lacks. Appearance commodities carry
    /// _iItemId 0 and are bought one at a time: the cart sends count = m_iCount or 1
    /// (UI_Sys_AppearanceShopCart_PanelCtrl.lua:767-773), and an unlocked look cannot be bought again.
    /// </summary>
    private Fail? Malformed(JsonElement commodity, int appearance, int count, int unit)
    {
        int currency = PaymentItem(commodity), entry = (int)GameData.Int(commodity, "_iItemId");
        if (unit < 0 || currency < 0 || currency > 0 && data.Row("ItemConfig", currency) == null)
            return new(ErrorCodes.ShopNoConfig, $"payment {currency} x{unit} not configured");
        if (appearance > 0)
            return entry != 0 ? new(ErrorCodes.ShopNoConfig, $"appearance {appearance} with item {entry}")
                : count != 1 ? new(ErrorCodes.Parameter, "appearance count")
                : null;
        if (appearance < 0 || entry <= 0 || data.Row("ItemConfig", entry) == null)
            return new(ErrorCodes.ShopNoConfig, $"goods {entry} not configured");
        return null;
    }

    /// <summary>_kAppearanceParts: a JSON array of WardRobeConfig ids, or {} when empty.</summary>
    private static List<int> Parts(JsonElement commodity) =>
        commodity.TryGetProperty("_kAppearanceParts", out var parts) && parts.ValueKind == JsonValueKind.Array
            ? parts.EnumerateArray().Select(p => (long)(GameData.Number(p) ?? 0)).Where(id => id is > 0 and <= int.MaxValue)
                .Select(id => (int)id).ToList()
            : [];

    private static ShopState Copy(ShopState state) => new()
    {
        Favorites = new HashSet<int>(state.Favorites),
        Bought = state.Bought.ToDictionary(p => p.Key, p => new Counter { Count = p.Value.Count, Period = p.Value.Period }),
    };

    /// <summary>A refusal: the ErrorCodes result for the client and the reason for the log.</summary>
    private readonly record struct Fail(int Code, string Why);

    // ---- limits ----

    /// <summary>_iPurchaseTimesLimit: 1 daily, 2 weekly, 3 monthly, 4 lifetime, 5 season (enum_ShopLimitType).</summary>
    private static long Family(JsonElement commodity) => GameData.Int(commodity, "_iPurchaseTimesLimit");

    /// <summary>
    /// The period a counter belongs to; a counter of an older period counts as zero. The client's only refresh clock is
    /// the 05:00 game day (Lua_TimeHelper.GetDayRefreshTime) and Monday 05:00, shared with DailyActivityService, in the
    /// realm zone: the game day's date, its ISO week, and its month (month turning on the 1st at 05:00 is inferred).
    /// Family 5 follows the captured server season ID, as the enum_ShopLimitType/LC_ShopHelper seasonal label requires.
    /// </summary>
    private string Period(long family, GameSession session)
    {
        var day = DailyActivityService.GameDay(clock.GetLocalNow().DateTime);
        return family switch
        {
            1 => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            2 => $"{ISOWeek.GetYear(day)}W{ISOWeek.GetWeekOfYear(day)}",
            3 => day.ToString("yyyyMM", CultureInfo.InvariantCulture),
            5 => $"S{session.SeasonState?.SeasonId ?? 1}",
            _ => "",
        };
    }

    private int Current(GameSession session, int commodityId, Counter? counter) =>
        counter == null ? 0
        : data.Row("ShopCommodityConfig", commodityId) is { } row && counter.Period != Period(Family(row), session) ? 0
        : counter.Count;

    // ---- favourites ----

    /// <summary>82333: op 1 adds, 2 removes a favourite (a removed key is deleted, not set false).</summary>
    public async Task CollectAsync(GameSession session, CommodityCollectReq req)
    {
        using var guard = await CharacterService.Lock(session);
        session.Owners!.Current!.BindPresentation(session);
        int code = data.Row("ShopCommodityConfig", req.CommodityId) == null ? ErrorCodes.ShopNoConfig
            : req.OpType is CollectOperateType.Add or CollectOperateType.Remove ? 0 : ErrorCodes.Parameter;
        bool ok = code == 0;
        if (ok)
        {
            var next = new ShopState
            {
                Favorites = new HashSet<int>(session.Shop!.Favorites),
                Bought = session.Shop.Bought.ToDictionary(p => p.Key, p => new Counter { Count = p.Value.Count, Period = p.Value.Period }),
            };
            if (req.OpType == CollectOperateType.Add) next.Favorites.Add(req.CommodityId);
            else next.Favorites.Remove(req.CommodityId);
            var tx = roles.NewTransaction();
            tx.ItemDelta(session.Role!.Id, session.Items, session.Items);
            tx.RoleBlob(session.Role.Id, "shop", JsonSerializer.SerializeToUtf8Bytes(next));
            tx.WithEconomy(character.FamilyEconomy(session, "cd13a/shop-favorite", [], [], next));
            await roles.CommitAsync(tx, "shop/favorite", _ => session.Shop = next);
            await SyncAsync(session);
        }
        await session.SendGameAsync(CommodityCollect, new CommodityCollectResp { Result = code, CommodityId = req.CommodityId, NpcId = req.NpcId, OpType = req.OpType });
    }
}
