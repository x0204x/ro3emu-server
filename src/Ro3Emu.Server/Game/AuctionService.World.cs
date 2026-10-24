using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>
/// World Auction (AuctionType 2) and Event Auction (AuctionType 3).
///
/// What the client proves:
/// - Tab tree: AuctionTabConfig rows with _iType 0 (Lua_CFG_AuctionTabConfig.GetTabTree) are 1 Guild, 2 World (no
///   children) and 3 Event (children 3001-3003, _iType 3). UI_Sys_AuctionWidgetCtrl.InitTab (lines 298-544) builds the
///   World sub-tabs from the item types of TypeConfig group Auction (AuctionConfig._iType) and shows them only when the
///   82019 count for WORLD has itemTypeCount entries (RefreshTabList, lines 548-678). Event sub-tabs are listed when
///   srcTypeCount names them AND CheckSubTab (lines 699-743) finds the tab's _KTypeParam activity in LC_ActManager.
/// - Queries: World asks QUERY_AUCTION/PreStack with srcType 0 and auctionItemTypes {type} (OnClickWorldSubTab,
///   lines 1072-1169; ShowPreOderView, 1500-1549 sends a nil sub-tab); Event asks with srcType = the sub-tab id
///   (OnClickSubTab, lines 996-1065). A record row labels itself "World Auction" when its srcType is
///   enum_AuctionTabType.World (UI_Sys_AuctionRecordCellListWidgetCtrl.lua:176-190), so World rows carry srcType 2
///   (the World tab, as Guild/Dungeon rows carry their leaf tab) and a World request's srcType 0 means "any".
/// - Empty tabs show 48102 "no World Auctions underway" / 48101 "no Event Auctions underway"
///   (UI_Sys_AuctionNullWidgetCtrl.SetNullText).
/// - Bidding is identical to Guild rows: start _iAuctionPrice, raise _iBaseRaisePrice, buy-out _iSellPrice, currency
///   _iCurrency (UI_Sys_AuctionListItemWidgetCtrl.lua:1010-1060, 1296-1395); only GUILD rows require a guild.
/// - Red dots: NewUpload for WORLD names itemTypes, other types srcTypes (LC_DAL_Auction.lua:1448-1498).
/// - World history is capped at PublicParam WorldAuctionNumber 300 (UI_Sys_AuctionRecordPanelCtrl.lua:418-425).
///
/// Server-internal and (inferred): the client never reads _iWorldAuctionCeiling, _iLimitCreate, _iBasicIncome,
/// _kGlobal (empty in all 529 rows), WorldAuctionExhibit or WorldAuctionEnd. The original server's DB shapes
/// (MG_Auction.proto DBAuctionGoodRecord: guildGenerateCount per guild, systemGenerateCount, generateTotalCount;
/// DBAuctionItem actId/actWeekDay/actDayStartTime/GuildActivityId; BatchUploadAuctionReq) show World goods come from
/// guilds and from the system, and Event lots from an activity occurrence. The concrete rules below are inferred:
/// - an unbid guild lot moves on to the World Auction while fewer than _iWorldAuctionCeiling lots of that item are open;
/// - World lots show for WorldAuctionExhibit minutes and end at the next WorldAuctionEnd o'clock (local time);
/// - Event lots come from a trusted event owner while the tab's activity window is open; exhibit/duration reuse the
///   guild auction minutes; dividends use the game-mode fraction.
/// No server producer exists yet for the three Event sources or for system World goods; the GM aids create them.
/// </summary>
public sealed partial class AuctionService
{
    private int WorldTab => data.Table("AuctionTabConfig").Values
        .Where(r => GameData.Int(r, "_iAuctionType") == (int)AuctionType.World && GameData.Int(r, "_iType") == 0)
        .Select(r => (int)GameData.Int(r, "_iID")).DefaultIfEmpty(2).First();

    /// <summary>Event sub-tabs (AuctionTabConfig _iAuctionType 3, _iType 3) and the ActivityConfig id that gates each.</summary>
    internal IReadOnlyDictionary<int, int> EventTabs() => data.Table("AuctionTabConfig").Values
        .Where(r => GameData.Int(r, "_iAuctionType") == (int)AuctionType.Act && GameData.Int(r, "_iType") == 3)
        .ToDictionary(r => (int)GameData.Int(r, "_iID"), r => r.TryGetProperty("_KTypeParam", out var p)
            && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() > 0 ? (int)(GameData.Number(p[0]) ?? 0) : 0);

    /// <summary>World requests name no tab (srcType 0); every World row and its pre-buy keys use the World tab.</summary>
    private int RowSrc(AuctionType type, int src) => type == AuctionType.World ? WorldTab : src;

    private int ItemType(int entry) => data.Row("AuctionConfig", entry) is { } cfg ? (int)GameData.Int(cfg, "_iType") : 0;

    /// <summary>The same pricing contract the guild publisher enforces before minting a lot.</summary>
    private bool AuctionPriced(int entry) => entry is not (100 or 101) && data.Row("ItemConfig", entry) != null
        && data.Row("AuctionConfig", entry) is { } cfg && GameData.Int(cfg, "_iAuction") == 1
        && GameData.Int(cfg, "_iAuctionPrice") > 0 && GameData.Int(cfg, "_iSellPrice") >= GameData.Int(cfg, "_iAuctionPrice")
        && GameData.Int(cfg, "_iBaseRaisePrice") > 0 && data.Row("ItemConfig", GameData.Int(cfg, "_iCurrency")) != null;

    private long WorldCeiling(int entry) => data.Row("AuctionConfig", entry) is { } cfg
        ? Math.Max(0, GameData.Int(cfg, "_iWorldAuctionCeiling")) : 0;

    private int OpenWorldLots(int entry) => store.OpenItems().Count(r => r.Type == AuctionType.World && r.Entry == entry);

    /// <summary>Exhibit start and the next WorldAuctionEnd o'clock strictly after it, in the realm's civil time.</summary>
    private (long Start, long End) WorldWindow(bool startNow)
    {
        long start = startNow ? Now : checked(Now + Math.Max(0, Param("WorldAuctionExhibit", 5)) * 60);
        int hour = (int)Math.Clamp(Param("WorldAuctionEnd", 18), 0, 23);
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(start), clock.LocalTimeZone);
        var day = local.Date;
        for (int i = 0; i < 3; i++, day = day.AddDays(1))
        {
            var candidate = day.AddHours(hour);
            long end = new DateTimeOffset(candidate, clock.LocalTimeZone.GetUtcOffset(candidate)).ToUnixTimeSeconds();
            if (end > start) return (start, end);
        }
        return (start, start + 86400);
    }

    private bool WorldTransferAllowed(AuctionStore.Item row) => row.Type == AuctionType.Guild && row.Owner == 0
        && !row.RandomAward && row.SettlementRecoveryReason.Length == 0 && AuctionPriced(row.Entry)
        && !store.Prebuys().Any(p => p.RequestedId == row.Id)
        && OpenWorldLots(row.Entry) < WorldCeiling(row.Entry);

    /// <summary>Re-lists an unbid guild lot on the World Auction. The guild and its participants stay on the row for
    /// the dividend; bids, passes and the reserve are empty by construction (Owner is 0).</summary>
    private void MoveToWorld(AuctionStore.Item row)
    {
        var cfg = data.Row("AuctionConfig", row.Entry)!.Value;
        var (start, end) = WorldWindow(startNow: false);
        row.Type = AuctionType.World;
        row.SrcType = WorldTab;
        row.State = AuctionState.Public;
        row.Start = start;
        row.End = end;
        row.Price = (int)GameData.Int(cfg, "_iAuctionPrice");
        row.Raise = (int)GameData.Int(cfg, "_iBaseRaisePrice");
        row.FixedPrice = row.MaxPrice = (int)GameData.Int(cfg, "_iSellPrice");
        row.Currency = (int)GameData.Int(cfg, "_iCurrency");
        row.Reserve = 0;
        row.Auto = row.AutoPaused = false;
        row.LastBid = 0;
        row.Joined.Clear();
        row.GiveUp.Clear();
        row.Revision++;
    }

    private async Task WorldUploadNoticeAsync(IReadOnlyCollection<AuctionStore.Item> rows)
    {
        var viewers = sessions.All().Where(s => s.Role != null && rows.Any(r => Visible(s, r))).Select(s => s.Role!.Id).ToArray();
        await NotifyNewRowsAsync(AuctionType.World, viewers, itemTypes: rows.Select(r => (long)ItemType(r.Entry)).Distinct());
    }

    /// <summary>
    /// System World goods (GM aid until a producer exists): one lot per entry, skipping entries already at their
    /// ceiling. No participants, so the realized price is a fee sink. Idempotent on <paramref name="source"/>.
    /// </summary>
    internal async Task<(int Code, int Created)> PublishWorldSystemAsync(string source, IReadOnlyList<int> entries,
        bool startNow)
    {
        if (!loop.IsOnLoop) return await loop.InvokeAsync(() => PublishWorldSystemAsync(source, entries, startNow));
        var goods = new List<(int Entry, int Count, int Bind)>();
        foreach (int entry in entries.Distinct())
            if (AuctionPriced(entry) && OpenWorldLots(entry) < WorldCeiling(entry)) goods.Add((entry, 1, 0));
        if (goods.Count == 0) return (ErrorCodes.RewardMissing, 0);
        var (start, end) = WorldWindow(startNow);
        var (code, rows) = await PublishLotsAsync("auction/world-publish", "world:" + source, AuctionType.World, WorldTab, 0,
            goods, [], start, end);
        if (rows.Length > 0)
            try { await WorldUploadNoticeAsync(rows); }
            catch (Exception e) { log.LogWarning(e, "World auction {Source} committed; notification failed", source); }
        return (code, rows.Length);
    }

    /// <summary>
    /// Trusted Event Auction boundary for an event owner (no client route). The owner supplies its loot and the
    /// participants that share the dividend; the tab's activity window must be open (AuctionTabConfig _KTypeParam,
    /// the same gate the client applies). Repeating a completion key never publishes twice.
    /// </summary>
    internal async Task<int> PublishEventAuctionAsync(string completionKey, int srcType,
        IReadOnlyList<(int Entry, int Count, int Bind)> goods, IReadOnlyList<long> participants, bool startNow = false)
    {
        if (!loop.IsOnLoop) return await loop.InvokeAsync(() => PublishEventAuctionAsync(completionKey, srcType, goods,
            participants, startNow));
        if (string.IsNullOrWhiteSpace(completionKey) || goods.Count == 0 || !EventTabs().TryGetValue(srcType, out int activity))
            return ErrorCodes.Parameter;
        if (activity <= 0 || !timedEvents.IsActive(activity)) return ErrorCodes.ActivityClosed;
        long start = startNow ? Now : checked(Now + Math.Max(0, Param("GuildAuctionExhibit", 5)) * 60);
        long end = checked(start + Math.Max(1, Param("GuildAuctionTime", 30)) * 60);
        if (timedEvents.WindowEnd(activity) is { } windowEnd) end = Math.Min(end, windowEnd.ToUnixTimeSeconds());
        if (end <= start) return ErrorCodes.ActivityPeriod;
        long[] members = participants.Distinct().Order().ToArray();
        var (code, rows) = await PublishLotsAsync("auction/event-publish", $"act:{srcType}:{completionKey}",
            AuctionType.Act, srcType, 0, goods, members, start, end);
        if (rows.Length > 0)
            try
            {
                var viewers = sessions.All().Where(s => s.Role != null && Visible(s, rows[0])).Select(s => s.Role!.Id);
                await NotifyNewRowsAsync(AuctionType.Act, viewers, srcTypes: [srcType]);
            }
            catch (Exception e) { log.LogWarning(e, "Event auction {Tab} committed; notification failed", srcType); }
        return code;
    }

    /// <summary>Mints priced lots under one durable source claim, as the guild publisher does.</summary>
    private async Task<(int Code, AuctionStore.Item[] Rows)> PublishLotsAsync(string kind, string source,
        AuctionType type, int srcType, long guildId, IReadOnlyList<(int Entry, int Count, int Bind)> goods,
        long[] eligible, long start, long end)
    {
        if (source.Length > 240 || end <= start || goods.Any(g => g.Count <= 0 || !AuctionPriced(g.Entry)))
            return (ErrorCodes.RewardMissing, []);
        if (eligible.Any(role => roles.Get(role) == null)) return (ErrorCodes.NoPermission, []);
        int itemCount = goods.Sum(g => g.Count);
        var keys = RoleKeys(eligible).Append(ReservationKey.Entity("auction_source", source))
            .Concat(Enumerable.Range(0, itemCount).Select(index =>
                ReservationKey.Entity("auction_item", $"{source}:{index}"))).ToArray();
        var created = new List<AuctionStore.Item>();
        try
        {
            using var scope = await EnterAuctionAsync(kind, () => keys);
            if (store.HasSource(source)) return (0, []);
            var transaction = roles.NewTransaction();
            store.EnlistSource(source, transaction);
            var escrow = AuctionIntent(kind, sourceKey: "auction/source/" + source)
                .Evidence(new
                {
                    Source = source, Type = type, SrcType = srcType, GuildId = guildId,
                    Members = eligible, Goods = goods, Start = start, End = end
                });
            int index = 0;
            foreach (var (entry, amount, bind) in goods)
            {
                escrow.Source(AuctionAsset(entry), amount, EconomyReason.ActivityReward, EconomyCompartment.AuctionLot);
                var cfg = data.Row("AuctionConfig", entry)!.Value;
                for (int n = 0; n < amount; n++)
                {
                    var row = new AuctionStore.Item
                    {
                        SourceKey = $"{source}:{index++}", Type = type, SrcType = srcType, GuildId = guildId,
                        Entry = entry, Bind = bind, Eligible = eligible.ToList(), Start = start, End = end,
                        State = start > Now ? AuctionState.Public : AuctionState.Bidding,
                        Price = (int)GameData.Int(cfg, "_iAuctionPrice"),
                        Raise = (int)GameData.Int(cfg, "_iBaseRaisePrice"),
                        FixedPrice = (int)GameData.Int(cfg, "_iSellPrice"),
                        MaxPrice = (int)GameData.Int(cfg, "_iSellPrice"),
                        Currency = (int)GameData.Int(cfg, "_iCurrency"),
                        TypeAttr = (int)GameData.Int(cfg, "_iTypeAttr"),
                    };
                    store.EnlistSave(row, transaction);
                    created.Add(row);
                }
            }
            transaction.WithEscrowEconomy(escrow);
            RequireScope(scope, transaction);
            await roles.CommitAsync(transaction, kind);
        }
        catch (Ro3Emu.Server.Accounts.Persistence.AuctionSourceDuplicateException) { return (0, []); }
        catch (EconomyOperationConflictException) when (store.HasSource(source)) { return (0, []); }
        return (0, created.ToArray());
    }

    /// <summary>Ends every open lot the predicate selects now, then settles it through the normal path.</summary>
    internal async Task<int> EndOpenLotsAsync(Func<AuctionStore.Item, bool> select, string reason)
    {
        if (!loop.IsOnLoop) return await loop.InvokeAsync(() => EndOpenLotsAsync(select, reason));
        int ended = 0;
        foreach (long id in store.OpenItems().Where(select).Select(r => r.Id).ToArray())
        {
            using (var scope = await EnterAuctionAsync("auction/end-now", () => RowKeys(store.Get(id))))
            {
                var row = store.Get(id);
                if (row == null || row.State == AuctionState.Settled || !select(row)) continue;
                if (row.End > Now)
                {
                    object prior = LotEvidence(row);
                    row.End = Now;
                    row.Start = Math.Min(row.Start, Now);
                    row.Revision++;
                    var transaction = roles.NewTransaction();
                    store.EnlistSave(row, transaction);
                    transaction.WithEscrowEconomy(AuctionIntent("auction/end-now")
                        .Evidence(new { PriorLot = prior, NewLot = LotEvidence(row), Reason = reason }));
                    RequireScope(scope, transaction);
                    await roles.CommitAsync(transaction, "auction/end-now");
                }
            }
            await SettleAsync(id);
            ended++;
        }
        return ended;
    }

    /// <summary>One priced entry per AuctionConfig item type (the World sub-tabs), lowest id first.</summary>
    private int[] SampleEntries() => data.Table("AuctionConfig").Values
        .Select(r => (Id: (int)GameData.Int(r, "_iID"), Type: (int)GameData.Int(r, "_iType")))
        .Where(r => AuctionPriced(r.Id)).GroupBy(r => r.Type).OrderBy(g => g.Key)
        .Select(g => g.Min(r => r.Id)).ToArray();

    private static int[] EntryArgs(string[] args, int skip) => args.Skip(skip)
        .Select(a => int.Parse(a, CultureInfo.InvariantCulture)).ToArray();

    /// <summary>GM test aid "!worldauction open [itemId...]|close". Open lists system lots that bid at once.</summary>
    internal async Task<string> WorldGmAsync(string[] args)
    {
        if (args.Length >= 1 && args[0] == "open")
        {
            int[] entries = args.Length > 1 ? EntryArgs(args, 1) : SampleEntries();
            var (code, count) = await PublishWorldSystemAsync("gm:" + clock.GetUtcNow().ToUnixTimeMilliseconds(), entries, startNow: true);
            return code == 0 ? $"World Auction: {count} lot(s) listed until {WorldEndText()}" : $"World Auction publish failed: {code}";
        }
        if (args.Length == 1 && args[0] == "close")
            return $"World Auction: {await EndOpenLotsAsync(r => r.Type == AuctionType.World, "gm-close")} lot(s) ended and settled";
        throw new ArgumentException("Use !worldauction open [itemId...] | close");
    }

    private string WorldEndText() => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(WorldWindow(true).End),
        clock.LocalTimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>GM test aid "!actauction &lt;tab&gt; open [itemId...]|close". Open forces the tab's activity window for 90
    /// minutes in memory (the client lists the tab only while that activity runs) and lists lots with the GM as the
    /// participant; close ends and settles the tab's lots and drops the forced window.</summary>
    internal async Task<string> EventGmAsync(long gmRole, string[] args)
    {
        if (!loop.IsOnLoop) return await loop.InvokeAsync(() => EventGmAsync(gmRole, args));
        var tabs = EventTabs();
        if (args.Length < 2 || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int tab)
            || !tabs.TryGetValue(tab, out int activity) || args[1] is not ("open" or "close"))
            throw new ArgumentException($"Use !actauction <{string.Join('|', tabs.Keys.Order())}> open [itemId...] | close");
        if (args[1] == "close")
        {
            int ended = await EndOpenLotsAsync(r => r.Type == AuctionType.Act && r.SrcType == tab, "gm-close");
            timedEvents.SetTestWindow(activity, false);
            return $"Event Auction {tab}: {ended} lot(s) ended and settled; activity {activity} back on its schedule";
        }
        if (!timedEvents.IsActive(activity)) timedEvents.SetTestWindow(activity, true);
        int[] entries = args.Length > 2 ? EntryArgs(args, 2) : SampleEntries();
        int code = await PublishEventAuctionAsync("gm:" + clock.GetUtcNow().ToUnixTimeMilliseconds(), tab,
            entries.Select(e => (e, 1, 0)).ToArray(), [gmRole], startNow: true);
        return code == 0 ? $"Event Auction {tab}: {entries.Length} lot(s) listed (activity {activity} open)"
            : $"Event Auction {tab} publish failed: {code}";
    }
}
