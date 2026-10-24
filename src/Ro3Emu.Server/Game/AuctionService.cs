using System.Text.Json;
using System.Collections.Concurrent;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// MG_Auction (LC_AuctionServer/LC_DAL_Auction.lua). Server-owned rows arise only from committed
/// dungeon and guild event rewards, unbid guild lots moving on to the World Auction, and trusted Event Auction
/// owners (AuctionService.World.cs). No client upload route is registered. Price, currency, raise and duration come from AuctionConfig;
/// the proxy-bid tie, dividend split, UTC deadlines and unsold award ordering below are (inferred).
/// </summary>
public sealed partial class AuctionService(AuctionStore store, RoleStore roles, GuildStore guilds, CharacterService character,
    MailService mail, RewardService rewards, GameData data, SeasonService seasons, FunctionService functions,
    SessionRegistry sessions, GameLoop loop, TimeProvider clock, TimedEventService timedEvents,
    ILogger<AuctionService> log) : IHostedService
{
    public const uint Sync = 5063, Query = 82000, Bid = 82001, Changed = 82002, History = 82004, Self = 82005,
        QueryBonus = 82006, GetBonus = 82007, Get = 82009, Cancel = 82010, Quit = 82011, DungeonDone = 82012,
        DungeonChanged = 82013, BatchBid = 82014, HasAuto = 82015, Resume = 82016, PreStack = 82017,
        BatchCancel = 82018, Count = 82019, Reddot = 82020;

    private readonly ConcurrentDictionary<long, byte> _pendingReady = new();
    internal Func<Task>? BeforeNoticeForTest { get; set; }
    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();
    private int RecordLimit => (int)Math.Clamp(Param("AuctionRecordLimit", 50), 1, 500);
    // UI_Sys_AuctionRecordPanelCtrl.lua:418-425 caps the World record list at WorldAuctionNumber (300).
    private int WorldRecordLimit => (int)Math.Clamp(Param("WorldAuctionNumber", 300), 1, 1000);

    public int DungeonSrcType(int difficulty) => (int)data.Table("AuctionTabConfig").Values
        .Where(row => GameData.Int(row, "_iAuctionType") == (int)AuctionType.Dungeon
            && row.TryGetProperty("_KTypeParam", out var values) && values.ValueKind == JsonValueKind.Array
            && values.EnumerateArray().Any(v => GameData.Number(v) == difficulty))
        .Select(row => GameData.Int(row, "_iID")).FirstOrDefault();

    public IReadOnlyList<AuctionStore.Item> DungeonRows(long runId) => [.. store.RunItems(runId)];
    private long Param(string key, long fallback) => data.Table("PublicParamConfig").TryGetValue(key, out var v)
        ? (long)(GameData.Number(v) ?? fallback) : fallback;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Only boot-orphan runs can be completed without DungeonService's live Ready marker.
        foreach (var run in store.PendingRuns().Where(r => !r.Ready))
        {
            var current = store.GetRunSnapshot(run.Id);
            if (current.Run == null || current.Run.Finalized || current.Run.Ready) continue;
            current.Run.Ready = true;
            var builder = roles.NewTransaction();
            store.EnlistRunSave(current.Run, current.Raw, builder);
            builder.WithEscrowEconomy(AuctionIntent("auction/recover-run", sourceKey: $"auction/run/{run.Id}/recover")
                .Evidence(new { RunId = run.Id, Ready = true }));
            roles.Commit(builder, "auction/recover-run");
        }
        var prebuys = store.Prebuys();
        if (prebuys.Count > 0)
        {
            var builder = roles.NewTransaction();
            foreach (var order in prebuys) store.EnlistPrebuy(order with { Paused = true }, builder);
            builder.WithEscrowEconomy(AuctionIntent("auction/recover-prebuy")
                .Evidence(new { Orders = prebuys.Select(p => new { p.Id, p.Role, p.UnitPrice, p.Currency }).ToArray() }));
            roles.Commit(builder, "auction/recover-prebuy");
        }
        loop.ScheduleRuntime(TimeSpan.FromSeconds(1), SweepAsync);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SweepAsync()
    {
        try
        {
            foreach (long id in _pendingReady.Keys)
            {
                try { await MarkDungeonRunReadyAsync(id); }
                catch (Exception e) { log.LogWarning(e, "Dungeon auction ready marker retry deferred for {Run}", id); }
            }
            foreach (var run in store.PendingRuns().Where(r => r.Ready)) await FinalizeDungeonRunAsync(run.Id);
            await ProcessPrebuysAsync();
            foreach (var row in store.OpenItems().Where(r => r.End <= Now
                && r.SettlementRecoveryReason.Length == 0))
                await SettleAsync(row.Id);
        }
        catch (Exception e) { log.LogError(e, "Auction recovery sweep failed"); }
        finally { loop.ScheduleRuntime(TimeSpan.FromSeconds(1), SweepAsync); }
    }

    public async Task LoginAsync(GameSession s)
    {
        var pending = store.Prebuys().Where(p => p.Role == s.Role!.Id && !p.Paused).ToArray();
        if (pending.Length > 0)
        {
            var builder = roles.NewTransaction();
            foreach (var order in pending) store.EnlistPrebuy(order with { Paused = true }, builder);
            builder.WithEscrowEconomy(AuctionIntent("auction/login-prebuy", s.Role!.Id, s.Account ?? "")
                .Evidence(new { Orders = pending.Select(p => new { p.Id, p.UnitPrice, p.Currency }).ToArray() }));
            roles.Commit(builder, "auction/login-prebuy");
        }
        s.Auction = roles.LoadAuction(s.Role!.Id) is { } saved ? DBAuction.Parser.ParseFrom(saved) : new DBAuction();
        await CharacterService.SyncAsync(s, Sync, s.Auction.Clone());
        var auto = store.OpenItems().Where(r => r.Owner == s.Role.Id && r.Auto && r.State == AuctionState.Bidding)
            .Select(r => r.Id).Concat(store.Prebuys().Where(p => p.Role == s.Role.Id).Select(p => p.RequestedId))
            .Distinct().ToArray();
        if (auto.Length > 0) await s.SendGameAsync(HasAuto, new HasAutoBiddingAuctionNtf { AuctionIds = { auto } });
    }

    private GameSession? Online(long role) => sessions.All().FirstOrDefault(s => s.Role?.Id == role
        && s.Attachment?.Cancelled != true && ReferenceEquals(s.Owner?.Attachment, s.Attachment));
    private static DBAuction Personal(RoleStore roles, long role) =>
        roles.LoadAuction(role) is { } saved
            ? DBAuction.Parser.ParseFrom(saved) : new DBAuction();

    private async Task SyncPersonalAsync(long role)
    {
        if (Online(role) is not { } s) return;
        var next = Personal(roles, role);
        s.Auction = next;
        await CharacterService.SyncAsync(s, Sync, next.Clone());
    }

    /// <summary>
    /// Called only inside DungeonService's committed personal reward transaction. The run journal survives restart;
    /// a failed/rolled-back reward never adds an eligible role. (inferred) A role needs first pass or allowance to join.
    /// </summary>
    public void RecordDungeonClear(RoleItemPlanBuilder tx, long runId, long role, long guildId,
        int srcType, int playBook, IReadOnlyDictionary<int, long> tiers) =>
        store.EnlistRunClear(runId, role, guildId, srcType, playBook, tiers, tx);

    public void MarkDungeonRunReady(long runId)
    {
        // This in-memory handoff distinguishes a completed live producer from a still-arriving participant journal.
        // A failed DB Ready write retries in Sweep; boot promotes only orphaned journals after producers are gone.
        _pendingReady.TryAdd(runId, 0);
        var snapshot = store.GetRunSnapshot(runId);
        var run = snapshot.Run;
        if (run == null || run.Finalized || run.Ready)
        {
            _pendingReady.TryRemove(runId, out _);
            return;
        }
        run.Ready = true;
        var builder = roles.NewTransaction();
        store.EnlistRunSave(run, snapshot.Raw, builder);
        builder.WithEscrowEconomy(AuctionIntent("auction/run-ready")
            .Evidence(new { RunId = runId, Ready = true }));
        roles.Commit(builder, "auction/run-ready");
        _pendingReady.TryRemove(runId, out _);
    }

    public async Task MarkDungeonRunReadyAsync(long runId)
    {
        // Record the producer handoff before waiting for the durable-write reservation so a
        // later sweep can retry it if the original continuation is interrupted.
        _pendingReady.TryAdd(runId, 0);
        using var scope = await EnterAuctionAsync("auction/run-ready", () =>
            [Ownership.ReservationKey.Entity("auction_run", runId.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
        var snapshot = store.GetRunSnapshot(runId);
        var run = snapshot.Run;
        if (run == null || run.Finalized || run.Ready) { _pendingReady.TryRemove(runId, out _); return; }
        run.Ready = true;
        var builder = roles.NewTransaction();
        store.EnlistRunSave(run, snapshot.Raw, builder);
        builder.WithEscrowEconomy(AuctionIntent("auction/run-ready")
            .Evidence(new { RunId = runId, Ready = true }));
        RequireScope(scope, builder);
        await roles.CommitAsync(builder, "auction/run-ready");
        _pendingReady.TryRemove(runId, out _);
    }

    public async Task FinalizeDungeonRunAsync(long runId)
    {
        if (!loop.IsOnLoop) { await loop.InvokeAsync(() => FinalizeDungeonRunAsync(runId)); return; }
        long[] notify = [];
        bool created = false;
        while (true)
        {
            var snapshot = store.GetRunSnapshot(runId);
            var run = snapshot.Run;
            if (run == null || run.Finalized || !run.Ready) return;
            int count = run.Eligible.Count;
            long group = run.TierGroups.Where(t => t.Key <= count).OrderBy(t => t.Key).LastOrDefault().Value;
            var goods = new List<(int Entry, int Count, int Bind)>();
            long? rewardProgressRevision = null;
            int rewardOpenDay = Math.Max(1, (clock.GetLocalNow().Date - seasons.Start.Date).Days + 1);
            if (group > 0 && run.Eligible.Count > 0 && data.Row("MultiverseGroupConfig", group) is { } mg)
            {
                long award = GameData.Int(mg, $"_iValue_{run.PlayBook}");
                if (award == 0) award = GameData.Int(mg, "_iValue_default");
                if (award > 0 && data.Row("AwardConfig", award) != null)
                {
                    var progress = roles.LoadProgress(run.Eligible[0]);
                    rewardProgressRevision = progress.SourceRevision!.Revision(RevisionStore.Progress);
                    goods = rewards.RollAuctionAwardBound(award, progress, rewardOpenDay);
                }
            }
            if (goods.Any(g => g.Count <= 0 || data.Row("ItemConfig", g.Entry) == null
                || data.Row("AuctionConfig", g.Entry) == null || g.Entry is 100 or 101))
                throw new InvalidOperationException("Dungeon auction roll includes a non-priced or non-deliverable reward.");
            int itemCount = goods.Sum(g => g.Count);
            var keys = RoleKeys(run.Eligible).Concat([
                Ownership.ReservationKey.Entity("auction_run", runId.ToString(System.Globalization.CultureInfo.InvariantCulture))])
                .Concat(Enumerable.Range(0, itemCount).Select(index =>
                    Ownership.ReservationKey.Entity("auction_item", $"dungeon:{runId}:{index}"))).ToArray();
            using var scope = await EnterAuctionAsync("auction/finalize-run", () => keys);
            var current = store.GetRunSnapshot(runId);
            if (current.Run == null || current.Run.Finalized) return;
            if (current.Run.SourceRevision != run.SourceRevision || !current.Run.Eligible.SequenceEqual(run.Eligible)
                || rewardProgressRevision is { } captured && roles.ReadRevisionVector(run.Eligible[0])
                    .Revision(RevisionStore.Progress) != captured
                || rewardOpenDay != Math.Max(1, (clock.GetLocalNow().Date - seasons.Start.Date).Days + 1))
                continue;
            var transaction = roles.NewTransaction();
            var economy = AuctionIntent("auction/finalize-run", sourceKey: $"auction/run/{runId}/finalize")
                .Evidence(new
                {
                    RunId = runId, run.GuildId, run.SrcType, run.PlayBook,
                    Eligible = run.Eligible.ToArray(), Group = group, Goods = goods, OpenDay = rewardOpenDay
                });
            int index = 0;
            foreach (var (entry, amount, bind) in goods)
            {
                economy.Source(AuctionAsset(entry), amount, EconomyReason.ActivityReward,
                    EconomyCompartment.AuctionLot);
                for (int n = 0; n < amount; n++)
                {
                    var cfg = data.Row("AuctionConfig", entry)!.Value;
                    int basePrice = (int)GameData.Int(cfg, "_iDungeonPrice");
                    int max = (int)GameData.Int(cfg, "_iDungeonPriceMax");
                    long duration = Math.Max(1, GameData.Int(cfg, "_iTimeLast"));
                    long start = Now + Math.Max(0, Param("GamePlayAuctionAwaitTime", 30));
                    store.EnlistSave(new AuctionStore.Item
                    {
                        SourceKey = $"dungeon:{run.Id}:{index++}", Type = AuctionType.Dungeon,
                        SrcType = run.SrcType, GuildId = run.GuildId, RunId = run.Id, Entry = entry,
                        Bind = bind, Start = start, End = start + duration, State = AuctionState.Public,
                        Price = basePrice, Raise = (int)GameData.Int(cfg, "_iBaseRaisePrice"),
                        FixedPrice = max, MaxPrice = max, Currency = (int)GameData.Int(cfg, "_iCurrency"),
                        TypeAttr = (int)GameData.Int(cfg, "_iTypeAttr"), Eligible = new(run.Eligible),
                    }, transaction);
                }
            }
            run.Finalized = true;
            store.EnlistRunSave(run, snapshot.Raw, transaction);
            transaction.WithEscrowEconomy(economy);
            RequireScope(scope, transaction);
            await roles.CommitAsync(transaction, "auction/finalize-run");
            created = goods.Count > 0;
            notify = run.Eligible.ToArray();
            break;
        }
        if (created) await NotifyNewRowsAsync(AuctionType.Dungeon, notify);
    }

    /// <summary>LC_DAL_Auction.QueryAuctionReddotNtf (lines 1448-1498): a WORLD NewUpload lights the item-type
    /// sub-tabs named in itemTypes, every other type the source sub-tabs in srcTypes.</summary>
    private async Task NotifyNewRowsAsync(AuctionType type, IEnumerable<long> recipients,
        IEnumerable<long>? srcTypes = null, IEnumerable<long>? itemTypes = null)
    {
        foreach (long role in recipients.Distinct()) if (Online(role) is { } s)
            try
            {
                var notice = new AuctionReddotNtf { ReddotType = AuctionReddotType.NewUpload, AuctionType = (long)type };
                notice.SrcTypes.Add(srcTypes ?? []);
                notice.ItemTypes.Add(itemTypes ?? []);
                await s.SendGameAsync(Reddot, notice);
            }
            catch (Exception e) { log.LogWarning(e, "Auction red dot failed for {Role}", role); }
    }

    private bool Visible(GameSession s, AuctionStore.Item r)
    {
        // (inferred) FunctionConfig 402 "World Auction" (parent 4) gates World rows as 401 gates Dungeon rows.
        if (s.Role == null || !functions.IsOpen(s, 4) || r.Type == AuctionType.Dungeon && !functions.IsOpen(s, 401)
            || r.Type == AuctionType.World && !functions.IsOpen(s, 402))
            return false;
        if (r.Type == AuctionType.Dungeon) return r.Eligible.Contains(s.Role.Id);
        if (r.Type == AuctionType.Guild) return guilds.ForRole(s.Role.Id)?.Id == r.GuildId;
        return r.Type is AuctionType.World or AuctionType.Act;
    }

    /// <summary>The wire endTime is the end of the current phase: UI_Sys_AuctionListItemWidgetCtrl counts a PUBLIC
    /// row down to endTime as "Starts at" (AuctionItemPublic) and a BIDDING row as its bidding end.</summary>
    internal static long ClientEnd(AuctionStore.Item r) => r.State == AuctionState.Public ? r.Start : r.End;

    private BiddingAuctionItem View(GameSession s, AuctionStore.Item r) => new()
    {
        AuctionId = r.Id, ItemId = r.Entry, ItemPrice = r.Price, EndTime = ClientEnd(r),
        OwnerId = r.Owner, SrcType = r.SrcType, IsCanBidding = (r.State is AuctionState.Bidding or AuctionState.Public)
            && r.End > Now && Visible(s, r)
            && !r.GiveUp.Contains(s.Role!.Id),
        IsBidded = r.Joined.Contains(s.Role!.Id), IsBidding = r.Owner == s.Role.Id,
        State = r.State, GuildId = r.GuildId, BiddingPrice = r.Owner == s.Role.Id ? r.Reserve : 0,
        IsGiveup = r.GiveUp.Contains(s.Role.Id), TypeAttr = r.TypeAttr, LastBiddingTime = r.LastBid,
        CurMaxAutoBidding = r.Auto && r.Owner == s.Role.Id ? new DBAutoBidding
        {
            RoleId = r.Owner, MaxPrice = r.Reserve,
            BiddingTime = r.LastBid, IsPaused = r.AutoPaused
        } : null,
        DungeonAuctionData = r.Type == AuctionType.Dungeon ? new DungeonAuctionData
        { QuUniqueId = r.RunId, DungeonBeginTime = r.Start, GiveupUsers = { r.GiveUp } } : null,
    };

    public async Task QueryAsync(GameSession s, QueryAuctionReq req)
    {
        long requestRole = s.Role?.Id ?? 0;
        var requestLease = s.Attachment;
        QueryAuctionRes res;
        using (var scope = await EnterSessionAuctionAsync(s, "auction/query-read", role => ReadKeys(role,
            store.OpenItems().Where(r => r.Type == req.AuctionType && Visible(s, r)
                && (req.SrcType == 0 || r.SrcType == req.SrcType)
                && (req.GuildId == 0 || r.GuildId == req.GuildId)
                && (req.AuctionItemTypes.Count == 0 || req.AuctionItemTypes.Contains(GameData.Int(
                    data.Row("AuctionConfig", r.Entry)!.Value, "_iType")))))))
        {
            var rows = store.OpenItems().Where(r => r.Type == req.AuctionType && Visible(s, r)
                && (req.SrcType == 0 || r.SrcType == req.SrcType)
                && (req.GuildId == 0 || r.GuildId == req.GuildId)
                && (req.AuctionItemTypes.Count == 0 || req.AuctionItemTypes.Contains(GameData.Int(data.Row("AuctionConfig", r.Entry)!.Value, "_iType"))))
                .OrderBy(r => r.End).ThenBy(r => r.Id).ToArray();
            int from = Math.Max(0, req.StartIndex - 1), count = Math.Clamp(req.EndIndex - req.StartIndex + 1, 0, 500);
            res = new QueryAuctionRes
            {
                AuctionType = req.AuctionType, StartIndex = req.StartIndex,
                EndIndex = req.EndIndex, SrcType = req.SrcType, TotalNum = rows.Length
            };
            res.AuctionItemTypes.Add(req.AuctionItemTypes);
            res.SrvAuctionItemTypes.Add(rows.Select(r => GameData.Int(data.Row("AuctionConfig", r.Entry)!.Value, "_iType")).Distinct());
            res.Auctions.Add(rows.Skip(from).Take(count).Select(r => View(s, r)));
        }
        await SendSessionAuctionAsync(s, requestRole, requestLease, Query, res);
    }

    public async Task SelfAsync(GameSession s, QuerySelfAuctionReq req)
    {
        long requestRole = s.Role?.Id ?? 0;
        var requestLease = s.Attachment;
        QuerySelfAuctionRes res;
        using (var scope = await EnterSessionAuctionAsync(s, "auction/self-read", role => ReadKeys(role,
            store.ItemsOfType(req.AuctionType).Where(r => Visible(s, r)
                && (r.Type == AuctionType.Dungeon || r.Joined.Contains(role) || r.Owner == role)))))
        {
            res = new QuerySelfAuctionRes { AuctionType = req.AuctionType };
            res.SelfAuctions.Add(store.ItemsOfType(req.AuctionType).Where(r => Visible(s, r)
                && (r.Type == AuctionType.Dungeon || r.Joined.Contains(s.Role!.Id) || r.Owner == s.Role.Id))
                .OrderByDescending(r => r.LastBid).Select(r => View(s, r)));
        }
        // The recovered no_decode_default_array client Lua iterates nil for a truly empty repeated field.
        // No valid server-side encoding can force a zero-length repeated field to be present; never mint a dummy row.
        await SendSessionAuctionAsync(s, requestRole, requestLease, Self, res);
    }

    public async Task GetAsync(GameSession s, GetAuctionReq req)
    {
        long requestRole = s.Role?.Id ?? 0;
        var requestLease = s.Attachment;
        GetAuctionRes res;
        using (var scope = await EnterSessionAuctionAsync(s, "auction/get-read", role => ReadKeys(role,
            store.ItemsById(req.AuctionIds).Where(r => r.Type == req.AuctionType && Visible(s, r)
                && (req.GuildId == 0 || r.GuildId == req.GuildId)
                && (req.SrcType == 0 || r.SrcType == req.SrcType) && req.AuctionIds.Contains(r.Id)))))
        {
            var rows = store.ItemsById(req.AuctionIds).Where(r => r.Type == req.AuctionType && Visible(s, r)
                && (req.GuildId == 0 || r.GuildId == req.GuildId) && (req.SrcType == 0 || r.SrcType == req.SrcType)
                && req.AuctionIds.Contains(r.Id)).ToArray();
            res = new GetAuctionRes { AuctionType = req.AuctionType, SrcType = req.SrcType, TotalNum = rows.Length };
            res.AuctionIds.Add(req.AuctionIds);
            res.Auctions.Add(rows.Select(r => View(s, r)));
        }
        await SendSessionAuctionAsync(s, requestRole, requestLease, Get, res);
    }

    public async Task HistoryAsync(GameSession s, QueryHistoryAuctionReq req)
    {
        long requestRole = s.Role?.Id ?? 0;
        var requestLease = s.Attachment;
        QueryHistoryAuctionRes res;
        using (var scope = await EnterSessionAuctionAsync(s, "auction/history-read", role => ReadKeys(role,
            store.SettledItems(req.AuctionType).Where(r =>
                Visible(s, r) && (req.GuildId == 0 || r.GuildId == req.GuildId)))))
        {
            res = new QueryHistoryAuctionRes { AuctionType = req.AuctionType };
            res.HistoryAuctions.Add(store.SettledItems(req.AuctionType).Where(r =>
                Visible(s, r) && (req.GuildId == 0 || r.GuildId == req.GuildId))
                .OrderByDescending(r => r.End).Take(req.AuctionType == AuctionType.World ? WorldRecordLimit : RecordLimit)
                .Select(HistoryOf));
        }
        await SendSessionAuctionAsync(s, requestRole, requestLease, History, res);
    }

    private static HistoryAuctionItem HistoryOf(AuctionStore.Item r) => new()
    {
        AuctionId = r.Id, AuctionType = (long)r.Type, ItemId = r.Entry, ItemPrice = r.Price,
        EndTime = r.End, SrcType = r.SrcType, UserId = r.Winner
    };
}
