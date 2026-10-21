using System.Text.Json;
using System.Security.Cryptography;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// To-Do rows, Dodo's ring and type-7 liveness boxes, plus the permanent wardrobe, Codex and
/// Survivor Battle, Become Stronger and Season Treasure stage rewards (types 3, 4, 6, 1, 8) the client claims through the
/// same 31178/31175 and 5059 list (StageError).
/// Quest progress and its item-5 awards remain owned by TaskService. This service mirrors accepted deliveries in 5004;
/// it never pays _iPerTaskPoint.
/// Reset code 5 = server-local daily 05:00, 105 = Monday 05:00 (inferred, aligned with MVP reset).
/// Inferred integration: an accepted tracking quest delivery advances its row and reissues that quest until
/// _iCount; a box checks item-5 as a non-spending threshold. No global daily item-5 cap is inferred.
/// Login reconciles epochs and quests before exposing 5004/5059 and then functions 25/5902; every settlement
/// reconciles before applying a new event, so an event at the reset boundary belongs to the new epoch.
/// </summary>
public sealed partial class DailyActivityService : IGameTicker
{
    public const uint ActivitySync = 5004, StageSync = 5059, RingAccept = 43504, StageGain = 31178, StageBatchGain = 31175;
    private const int Liveness = 5, RingActivity = 3302, TodoFunction = 25, RingFunction = 5902;
    private const int FashionStage = 3, LibraryStage = 4, ChickenStage = 6, TodoStage = 7, ChickenScore = 7; // GenericStageRewardsConfig._iType; item 7
    private const int PowerStage = 1, TreasureStage = 8, PowerFunction = 48; // enum_StageRewardType BecomeStronger / SeasonTreasure
    private const int RingRuns = 1; // PublicParamConfig.RunRingTaskRefreshCount (inferred as one accept per reset).
    private static readonly int[] RingStarts = [3001, 3011, 3021];
    private readonly GameData _data;
    private readonly TimeProvider _clock;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly SeasonService _seasons;
    private readonly TaskService _tasks;
    private readonly RoleStore _roles;
    private readonly RewardService _rewards;
    private readonly SessionRegistry _sessions;
    private readonly GameLoop _loop;
    private readonly TimedTodoSchedule _timedSchedule;
    private readonly ILogger<DailyActivityService> _log;
    private Dictionary<int, ActivityDef>? _definitions;
    private Dictionary<long, List<int>>? _byTask;

    /// <summary>Elapsed-time owners settle the ending period under TaskLock before daily quests/currency reset.</summary>
    public Func<GameSession, long, Task<bool>>? BeforeReset { get; set; }

    public sealed class DailyState
    {
        public byte[] Rows { get; set; } = [];
        public HashSet<int> Claims { get; set; } = new(); // shared 5059 list, not only type 7
        public long DailyEpoch { get; set; }
        public long WeeklyEpoch { get; set; }
        public int ActiveRingStart { get; set; }
    }

    private sealed record ActivityDef(int Id, int Function, int Count, int Reset, int[] Books, long[] Tasks);
    public sealed record DeliveryPlan(byte[] Before, byte[] After, bool Repeat, bool MirrorChanged);

    public DailyActivityService(GameData data, CharacterService character, FunctionService functions, SeasonService seasons,
        TaskService tasks, RoleStore roles, RewardService rewards, SessionRegistry sessions, GameLoop loop,
        TimedTodoSchedule timedSchedule, TimeProvider clock, ILogger<DailyActivityService> log)
    {
        this._data = data;
        this._character = character;
        this._functions = functions;
        this._seasons = seasons;
        this._tasks = tasks;
        this._roles = roles;
        this._rewards = rewards;
        this._sessions = sessions;
        this._loop = loop;
        this._timedSchedule = timedSchedule;
        this._clock = clock;
        this._log = log;
        tasks.BeforeProgress = s => ReconcileCoreAsync(s);
        tasks.RefreshDailyTracking = s => tasks.EnsureDailyTrackingCoreAsync(s, TrackingTasks(s));
        tasks.PrepareDailyDelivery = PrepareDelivery;
        tasks.SyncDailyDelivery = SyncDeliveryAsync;
    }

    /// <summary>Restores the new day's AFK allowance into the reset transaction (WildAfkService, same role scope).</summary>
    internal Func<GameSession, (byte[] Bytes, Action Committed, Action Rollback)?>? PlanAfkRollover { get; set; }

    /// <summary>Before TaskService.Login: reconcile persisted epochs and quest rows, then 5009 will carry reset currency.</summary>
    public void Load(GameSession session, DateTime? localNow = null)
    {
        var saved = _roles.LoadDaily(session.Role!.Id);
        var state = saved == null ? new DailyState() : JsonSerializer.Deserialize<DailyState>(saved) ?? new DailyState();
        var taskBytes = _roles.LoadTasks(session.Role.Id);
        var taskInfo = taskBytes != null ? DBTaskInfo.Parser.ParseFrom(taskBytes) : new DBTaskInfo();
        // Selection owns the realm before the first packet; this branch never awaits a writer admission.
        RolloverAsync(session, state, taskInfo, localNow ?? _clock.GetLocalNow().DateTime,
            synchronous: true).GetAwaiter().GetResult();
    }

    /// <summary>
    /// The one daily/weekly period command (C/D 09): resets rows, quests, liveness and the AFK allowance together in
    /// one transaction, from the given copies. The session is changed only after the commit; a failed commit leaves
    /// the old period in place (and retryable). Login passes the stored state; the running reset passes clones of
    /// the session's own state, so nothing is re-read from SQL. Returns the committed quests when they were reset.
    /// </summary>
    private async Task<DBTaskInfo?> RolloverAsync(GameSession session, DailyState state, DBTaskInfo taskInfo,
        DateTime now, bool synchronous = false)
    {
        long previousDailyEpoch = state.DailyEpoch, previousWeeklyEpoch = state.WeeklyEpoch;
        state.Claims ??= new();
        var rows = state.Rows.Length > 0 ? DBDailyActivityInfo.Parser.ParseFrom(state.Rows) : new DBDailyActivityInfo();
        long daily = Boundary(now), weekly = WeekBoundary(now);
        bool resetDaily = state.DailyEpoch != 0 && state.DailyEpoch < daily;
        bool resetWeekly = state.WeeklyEpoch != 0 && state.WeeklyEpoch < weekly;
        // A completed AFK objective whose award was held must settle in its own period, also after reconnect.
        if (resetDaily && WildAfkService.PendingDailyAward(session.WildAfk, state, taskInfo, rows))
        {
            session.Daily = state;
            session.DailyRows = rows;
            return null;
        }
        if (resetDaily || resetWeekly)
        {
            foreach (var def in Defs().Values.Where(d => d.Reset == 5 && resetDaily || d.Reset == 105 && resetWeekly))
            {
                if (Available(session, def)) rows.ActivityData[def.Id] = NewRow(def.Id, daily);
                else rows.ActivityData.Remove(def.Id);
                foreach (long id in def.Tasks) { taskInfo.Infos.Remove(id); taskInfo.CompleteTaskNum.Remove(id); }
            }
            if (resetDaily)
            {
                foreach (var id in RingStarts.SelectMany(start => Enumerable.Range(start, 10)))
                { taskInfo.Infos.Remove(id); taskInfo.CompleteTaskNum.Remove(id); }
                state.Claims.RemoveWhere(IsDailyStage);
                state.ActiveRingStart = 0;
            }
        }
        state.DailyEpoch = daily;
        state.WeeklyEpoch = weekly;
        // A saved row from another playbook is not enabled in this one.
        foreach (var def in Defs().Values.Where(d => !Available(session, d))) rows.ActivityData.Remove(def.Id);
        foreach (var def in Defs().Values.Where(d => Available(session, d)))
            if (!rows.ActivityData.ContainsKey(def.Id)) rows.ActivityData[def.Id] = NewRow(def.Id, daily);
        ProjectWindows(rows, ToUnix(now));
        UpdateRingCount(rows, state);
        state.Rows = rows.ToByteArray();
        if (!resetDaily && !resetWeekly)
        {
            session.Daily = state;
            session.DailyRows = rows;
            Save(session);
            return null;
        }
        var currency = new Dictionary<int, long>(session.Progress!.Currency);
        long livenessSink = resetDaily ? currency.GetValueOrDefault(Liveness) : 0;
        if (resetDaily) currency[Liveness] = 0;
        var afk = resetDaily && PlanAfkRollover is { } plan ? plan(session) : null;
        byte[] dailyBytes = JsonSerializer.SerializeToUtf8Bytes(state), taskBytes = taskInfo.ToByteArray();
        byte[] outcome = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1, PreviousDailyEpoch = previousDailyEpoch, DailyEpoch = daily,
            PreviousWeeklyEpoch = previousWeeklyEpoch, WeeklyEpoch = weekly,
            DailyHash = Convert.ToHexString(SHA256.HashData(dailyBytes)),
            TasksHash = Convert.ToHexString(SHA256.HashData(taskBytes)),
            AfkHash = afk is { } prepared ? Convert.ToHexString(SHA256.HashData(prepared.Bytes)) : null
        });
        var economy = new EconomyMutation(Guid.NewGuid().ToString("N"), session.Account!, session.Role!.Id,
            "cd13e/daily-reset", Convert.ToHexString(SHA256.HashData(outcome)),
            $"daily-reset/{session.Role.Id}/{previousDailyEpoch}/{daily}/{previousWeeklyEpoch}/{weekly}",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            livenessSink > 0 ? [new EconomyQuantity(new(EconomyAssetKind.Currency, Liveness), livenessSink)] : [],
            [], outcome);
        string currencyBytes = string.Join(',', currency.Select(c => $"{c.Key}:{c.Value}"));
        try
        {
            if (synchronous)
                _roles.SaveDailyReset(session.Role.Id, dailyBytes, taskBytes, currencyBytes, session.Progress,
                    economy, afk?.Bytes);
            else
                await _roles.SaveDailyResetAsync(session.Role.Id, dailyBytes, taskBytes, currencyBytes,
                    session.Progress, economy, afk?.Bytes);
        }
        catch
        {
            afk?.Rollback();
            throw;
        }
        afk?.Committed();
        if (resetDaily) session.Progress.Currency[Liveness] = 0;
        session.Daily = state;
        session.DailyRows = rows;
        return taskInfo;
    }

    /// <summary>After 5027, before the final 5056: initialize eligible tracking quests and sync nonempty 5004/5059.</summary>
    public async Task LoginAsync(GameSession session)
    {
        using var gate = await TaskService.LockTasksAsync(session);
        await ReconcileCoreAsync(session); // an old AFK award may have held the load-time reset until quest login
        await _tasks.EnsureDailyTrackingCoreAsync(session, TrackingTasks(session));
        await SyncRowsAsync(session);
        await SyncClaimsAsync(session);
        session.DailyReady = true;
    }

    public IEnumerable<long> TrackingTasks(GameSession session) => Defs().Values
        .Where(d => d.Id != RingActivity && d.Count > 0 && Available(session, d) && ParticipationOpen(d.Id) && _functions.IsOpen(session, TodoFunction)
            && _functions.IsOpen(session, d.Function)
            && Rows(session).ActivityData.TryGetValue(d.Id, out var row) && row.TaskCompleteTimes < d.Count)
        .SelectMany(d => d.Tasks).Distinct();

    /// <summary>Preview an accepted daily tracking quest or any node of the active Dodo chain for atomic settlement.</summary>
    private DeliveryPlan? PrepareDelivery(GameSession session, long taskId)
    {
        if (session.Daily == null || session.DailyRows == null) return null;
        int start = session.Daily.ActiveRingStart;
        bool ringNode = start > 0 && taskId >= start && taskId < start + 10;
        if (!ringNode && !ByTask().ContainsKey(taskId)) return null;
        var before = JsonSerializer.SerializeToUtf8Bytes(session.Daily);
        var next = JsonSerializer.Deserialize<DailyState>(before)!;
        var rows = DBDailyActivityInfo.Parser.ParseFrom(next.Rows);
        bool repeat = false, changed = false;
        foreach (int id in ByTask().GetValueOrDefault(taskId) ?? [])
        {
            var def = Defs()[id];
            if (!Available(session, def) || !ParticipationOpen(id) || !_functions.IsOpen(session, TodoFunction)
                || !_functions.IsOpen(session, def.Function) || def.Count <= 0) continue;
            if (id == RingActivity && (!ringNode || taskId != start + 9)) continue;
            if (!rows.ActivityData.TryGetValue(id, out var row) || row.TaskCompleteTimes >= def.Count) continue;
            row.TaskCompleteTimes++;
            row.Tasks[taskId] = row.Tasks.GetValueOrDefault(taskId) + 1;
            row.ActivityState = row.TaskCompleteTimes >= def.Count ? (EDailyState)2 : (EDailyState)1;
            if (id == RingActivity) next.ActiveRingStart = 0;
            repeat |= id != RingActivity && row.TaskCompleteTimes < def.Count;
            changed = true;
        }
        if (changed) next.Rows = rows.ToByteArray();
        return new DeliveryPlan(before, JsonSerializer.SerializeToUtf8Bytes(next), repeat, changed);
    }

    private Task SyncDeliveryAsync(GameSession session) =>
        SyncRowsAsync(session);

    /// <summary>43504 branch 110 only. The deterministic chain rotation and consume-on-accept are inferred.</summary>
    public async Task AcceptRingAsync(GameSession session, CommonDailyActivityReq req)
    {
        using var gate = await TaskService.LockTasksAsync(session);
        if (session.Role == null || session.Progress == null || session.Daily == null || !session.DailyReady)
        {
            await session.SendGameAsync(RingAccept, new CommonDailyActivityRes
            { Result = ErrorCodes.FeatureUnavailable, ActivityId = req.ActivityId, RequestId = req.RequestId });
            return;
        }
        await ReconcileCoreAsync(session);
        int result = req.ActivityId != RingActivity || req.RunRingTaskAccept == null ? ErrorCodes.Parameter
            : !_functions.IsOpen(session, TodoFunction) || !_functions.IsOpen(session, RingFunction) ? ErrorCodes.FeatureUnavailable
            : session.Daily is { ActiveRingStart: not 0 } ? ErrorCodes.TaskExists
            : session.Daily == null || Remaining(session.Daily) <= 0 ? ErrorCodes.AttemptsExhausted
            : 0;
        int start = 0;
        if (result == 0)
        {
            // Local-day index rotates over the three configured chain roots, independent of requestId (often zero).
            int localDay = DateOnly.FromDateTime(SeasonService.Civil(session.Daily!.DailyEpoch, _clock.LocalTimeZone)).DayNumber;
            start = RingStarts[localDay % RingStarts.Length];
            var oldTasks = session.Tasks?.Clone();
            var oldDaily = JsonSerializer.SerializeToUtf8Bytes(session.Daily);
            result = _tasks.AssignRing(session, start) ? 0 : ErrorCodes.TaskStatus;
            if (result == 0)
            {
                session.Daily.ActiveRingStart = start;
                UpdateRingCount(Rows(session), session.Daily);
                session.Daily.Rows = Rows(session).ToByteArray();
                var nextDaily = JsonSerializer.SerializeToUtf8Bytes(session.Daily);
                bool committed;
                try { committed = _roles.CommitRingAccept(session.Role!.Id, oldDaily, nextDaily, session.Tasks!.ToByteArray()); }
                catch (Exception e)
                {
                    _log.LogError(e, "Ring acceptance transaction failed for {Role}", session.Role?.Id);
                    committed = false;
                }
                if (!committed)
                {
                    session.Tasks = oldTasks;
                    session.Daily = JsonSerializer.Deserialize<DailyState>(oldDaily)!;
                    session.DailyRows = DBDailyActivityInfo.Parser.ParseFrom(session.Daily.Rows);
                    result = ErrorCodes.Unknown;
                }
                else
                {
                    await _tasks.SyncCurrentAsync(session);
                    await SyncRowsAsync(session);
                }
            }
        }
        _log.LogInformation("Ring accept {Name} root {Root}: {Result}", session.Role?.Name, start, result);
        await session.SendGameAsync(RingAccept, new CommonDailyActivityRes
        {
            Result = result, ActivityId = req.ActivityId, RequestId = req.RequestId,
            RunRingTaskAccept = new RunRingTaskAcceptRes { Placeholder = req.RunRingTaskAccept?.Placeholder ?? 0 },
        });
    }

    /// <summary>31178: one stage under StageError's rule, paid and marked in the shared list in one commit.</summary>
    public async Task ClaimAsync(GameSession session, StageRewardGainReq req)
    {
        using var gate = await TaskService.LockTasksAsync(session);
        if (session.Role == null || session.Progress == null || session.Daily == null || !session.DailyReady)
        {
            await session.SendGameAsync(StageGain, new StageRewardGainResp
            { Result = ErrorCodes.FeatureUnavailable, StageId = req.StageId });
            return;
        }
        await ReconcileCoreAsync(session);
        int result = StageError(session, req.StageId);
        if (result == 0)
        {
            var grant = StageGrant(session, req.StageId);
            if (grant == null) result = ErrorCodes.RewardMissing;
            else
            {
                var before = JsonSerializer.SerializeToUtf8Bytes(session.Daily);
                var next = JsonSerializer.Deserialize<DailyState>(before)!;
                next.Claims.Add(req.StageId);
                var after = JsonSerializer.SerializeToUtf8Bytes(next);
                // The claim, currency/EXP and inventory are committed together by the character owner.
                result = await _character.GrantDailyStageAsync(session, grant, before, after) ? 0 : ErrorCodes.Unknown;
                if (result == 0) await SyncClaimsAsync(session);
            }
        }
        await session.SendGameAsync(StageGain, new StageRewardGainResp { Result = result, StageId = req.StageId });
    }

    /// <summary>
    /// 31175 (LC_StageRewardHelper.GetStageRewardByIdList): every listed stage under the 31178 rule, paid and marked in one
    /// commit. All or nothing (inferred: the client lists only stages it shows as claimable and reads no reply; 5059 is
    /// what updates it).
    /// </summary>
    public async Task BatchClaimAsync(GameSession session, StageRewardBatchGainReq req)
    {
        using var gate = await TaskService.LockTasksAsync(session);
        if (session.Role == null || session.Progress == null || session.Daily == null || !session.DailyReady)
        {
            await session.SendGameAsync(StageBatchGain, new StageRewardBatchGainResp { Result = ErrorCodes.FeatureUnavailable, Req = req });
            return;
        }
        await ReconcileCoreAsync(session);
        var ids = req.StageIds.Distinct().ToArray();
        int result = ids.Length == 0 ? ErrorCodes.Parameter : 0;
        foreach (int id in ids)
            if (result == 0) result = StageError(session, id);
        var grant = new List<(int Entry, int Count, int Bind)>();
        foreach (int id in ids)
        {
            if (result != 0) break;
            var award = StageGrant(session, id);
            if (award == null) result = ErrorCodes.RewardMissing;
            else grant.AddRange(award);
        }
        if (result == 0)
        {
            var before = JsonSerializer.SerializeToUtf8Bytes(session.Daily);
            var next = JsonSerializer.Deserialize<DailyState>(before)!;
            next.Claims.UnionWith(ids);
            var after = JsonSerializer.SerializeToUtf8Bytes(next);
            result = await _character.GrantDailyStageAsync(session, grant, before, after) ? 0 : ErrorCodes.Unknown;
            if (result == 0) await SyncClaimsAsync(session);
        }
        await session.SendGameAsync(StageBatchGain, new StageRewardBatchGainResp { Result = result, Req = req });
    }

    /// <summary>
    /// One stage's claim rule: a known row, not yet claimed, its type's progress reached, each as the client's claim button
    /// decides (the reply is ignored, LC_DAL_StageReward._processStageRewardGet is empty; 5059 alone updates the panels):
    /// <list type="bullet">
    /// <item>7 To-Do: function 25, item-5 liveness at least _iScore (a threshold, not spent).</item>
    /// <item>3 wardrobe: fashion score at least _iScore. LC_FashionHelper.UpdateFashionScore sums the permanent lit
    /// appearances' WardRobeConfig._iFashionScore; UI_Sys_WardrobeLevelPanelCtrl.OnClickRewardAll claims _iLevel up to
    /// that score's level, the same rows.</item>
    /// <item>4 Codex: the count of the row's _iItem at least _iScore (LC_Library_Helper.SendRewardByType / GetRewardIsOver;
    /// 118 is LibraryOverviewPointID, the Collection Points of the overview's 4001..4020).</item>
    /// <item>6 Survivor Battle: DBChickenInfo.rankId at least _iLevel (UI_Sys_ChickenContentWidgetBaseCtrl.OnItemClick),
    /// where rank 1 also needs points in it (its guard on GetCurRankInfo's surplus; at rank 1 that is item 7 itself).</item>
    /// <item>1 Become Stronger: the role's best power, max(maxHistoryPower, currentPower) of PlayerChangePower, at
    /// least _iScore (UI_Sys_BecomeStrongerPanelModel.lua:307-350 builds iMaxHistoryPower that way and lights the rows
    /// it reaches). (inferred) Gated by the panel's function 48 like To-Do's 25: power needs no feature to grow.</item>
    /// <item>8 Season Treasure: the count of the row's _iItem (130401, enum_CurrencyType.SeasonTreasure) at least
    /// _iScore (UI_Sys_Season_TreasureRewardListWidgetCtrl.lua:284 claims when iCurrentNum reaches iNum).</item>
    /// </list>
    /// (inferred) Types 3, 4, 6 and 8 are permanent once-only claims with no function gate: the progress already needs
    /// the feature. A threshold not reached answers CurrencyShort for every type.
    /// </summary>
    private int StageError(GameSession session, int stageId)
    {
        if (_data.Row("GenericStageRewardsConfig", stageId) is not { } r) return ErrorCodes.RewardMissing;
        long type = GameData.Int(r, "_iType"), item = GameData.Int(r, "_iItem"), score = GameData.Int(r, "_iScore");
        int level = (int)GameData.Int(r, "_iLevel");
        bool? reached = type switch
        {
            TodoStage when item == Liveness => _character.CountOf(session, Liveness) >= score,
            FashionStage => FashionScore(session) >= score,
            LibraryStage when item > 0 => _character.CountOf(session, (int)item) >= score,
            ChickenStage when level > 0 => (session.TimedBattle?.ChickenRank ?? 1) is var rank && rank >= level
                && (rank > 1 || _character.CountOf(session, ChickenScore) > 0),
            PowerStage => Math.Max(PowerService.Total(PowerService.History(session)), PowerService.Total(PowerService.Current(session))) >= score,
            TreasureStage when item > 0 => _character.CountOf(session, (int)item) >= score,
            _ => null
        };
        return reached == null ? ErrorCodes.RewardMissing
            : type == TodoStage && !_functions.IsOpen(session, TodoFunction) ? ErrorCodes.FeatureUnavailable
            : type == PowerStage && !_functions.IsOpen(session, PowerFunction) ? ErrorCodes.FeatureUnavailable
            : session.Daily!.Claims.Contains(stageId) ? ErrorCodes.RewardClaimed
            : reached == true ? 0 : ErrorCodes.CurrencyShort;
    }

    private long FashionScore(GameSession session) => session.Wardrobe?.AllLightUpAppearances.Values
        .Where(a => a.OverdueTimeMs == 0)
        .Sum(a => _data.Row("WardRobeConfig", a.AppearanceId) is { } row ? GameData.Int(row, "_iFashionScore") : 0) ?? 0;

    /// <summary>
    /// The stage's goods, null when its _iAward resolves to nothing. A row without an award (4001, the Codex's level 1)
    /// pays nothing and is only marked (inferred: LC_Library_Helper.SendRewardByType lists it in every overview claim,
    /// so refusing it would refuse the whole batch).
    /// </summary>
    private List<(int Entry, int Count, int Bind)>? StageGrant(GameSession session, int stageId)
    {
        int group = (int)GameData.Int(_data.Row("GenericStageRewardsConfig", stageId)!.Value, "_iAward");
        if (group == 0) return [];
        var award = StageAward(session, group);
        return award.Count == 0 ? null : award;
    }

    private List<(int Entry, int Count, int Bind)> StageAward(GameSession session, int group)
    {
        var award = new List<(int, int, int)>();
        if (_data.Row("MultiverseGroupConfig", group) is not { } mv) return award;
        int book = _seasons.PlayBook;
        long target = GameData.Int(mv, $"_iValue_{book}");
        if (target == 0) target = GameData.Int(mv, "_iValue_default");
        if (_data.Row("AwardConfig", target) is not { ValueKind: JsonValueKind.Array } rows || rows.GetArrayLength() == 0) return award;
        var first = rows[0];
        if (first.TryGetProperty("_kAward", out var fixedAward) && fixedAward.ValueKind == JsonValueKind.Array)
            foreach (var item in fixedAward.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 2)
                    award.Add(((int)(GameData.Number(item[0]) ?? 0), (int)(GameData.Number(item[1]) ?? 0), item.GetArrayLength() > 2 ? (int)(GameData.Number(item[2]) ?? 0) : 0));
        award.AddRange(_rewards.RollAwardBound(target, session));
        return award.Where(a => a.Item1 > 0 && a.Item2 > 0 && _data.Row("ItemConfig", a.Item1) != null).ToList();
    }

    public void Tick(GameTime time)
    {
        if (time.Now - _lastTick < TimeSpan.FromSeconds(1)) return;
        _lastTick = time.Now;
        // The reconciler's own no-work test, without its quest lock: only a crossed day/week boundary or a due
        // window refresh is posted (every session used to be posted every second: a 300-item queue burst).
        var now = _clock.GetLocalNow().DateTime;
        long daily = Boundary(now), weekly = WeekBoundary(now), seconds = ToUnix(now);
        foreach (var session in _sessions.All())
        {
            if (session.Daily is not { } state || !session.DailyReady) continue;
            if (state.DailyEpoch >= daily && state.WeeklyEpoch >= weekly
                && seconds < session.DailyWindowRefreshAt && seconds >= session.DailyWindowSampledAt) continue;
            _loop.PostOwned(session, () => ReconcileAsync(session));
        }
    }

    private TimeSpan _lastTick;

    public async Task ReconcileAsync(GameSession session, DateTime? localNow = null)
    {
        using var gate = await TaskService.LockTasksAsync(session);
        await ReconcileCoreAsync(session, localNow);
    }

    // The caller owns TaskLock; resetting or accepting a ring must not interleave with a staged quest award.
    private async Task ReconcileCoreAsync(GameSession session, DateTime? localNow = null)
    {
        if (session.Daily == null || session.Role == null) return;
        var now = localNow ?? _clock.GetLocalNow().DateTime;
        if (session.Daily.DailyEpoch >= Boundary(now) && session.Daily.WeeklyEpoch >= WeekBoundary(now))
        {
            long seconds = ToUnix(now);
            if (session.DailyReady && (seconds >= session.DailyWindowRefreshAt || seconds < session.DailyWindowSampledAt))
                await SyncRowsAsync(session, now);
            return;
        }
        if (session.Daily.DailyEpoch < Boundary(now) && BeforeReset is { } settle)
            if (!await settle(session, Boundary(now)))
                throw new InvalidOperationException("AFK daily award is pending; retry settlement before resetting its period.");
        bool wasReady = session.DailyReady;
        session.DailyReady = false;
        try
        {
            // The running reset works on copies of this session's own state (no SQL re-read) and replaces it only
            // after its single commit. Without quest data the stored blob is carried through unchanged.
            var current = JsonSerializer.Deserialize<DailyState>(JsonSerializer.SerializeToUtf8Bytes(session.Daily))!;
            current.Rows = Rows(session).ToByteArray();
            var quests = session.Tasks?.Clone() ?? DBTaskInfo.Parser.ParseFrom(_roles.LoadTasks(session.Role.Id) ?? []);
            var committed = await RolloverAsync(session, current, quests, now);
            if (session.Tasks != null)
            {
                if (committed != null) session.Tasks = committed;
                await _tasks.EnsureDailyTrackingCoreAsync(session, TrackingTasks(session));
            }
            await _character.ItemsChangedAsync(session, stats: false);
            await SyncRowsAsync(session, now);
            await SyncClaimsAsync(session);
            session.DailyReady = wasReady;
            if (wasReady) await _functions.SyncAsync(session);
        }
        catch
        {
            session.DailyReady = wasReady;
            throw;
        }
    }

    private long ToUnix(DateTime local) => new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _clock.LocalTimeZone.GetUtcOffset(local)).ToUnixTimeSeconds();
    private long Boundary(DateTime now) => ToUnix(GameDay(now).AddHours(5));
    private long WeekBoundary(DateTime now) => ToUnix(GameWeek(now).AddHours(5));

    /// <summary>The game day a realm-local time belongs to: it starts at 05:00 (Lua_TimeHelper.GetDayRefreshTime).</summary>
    internal static DateTime GameDay(DateTime local) => local.TimeOfDay >= TimeSpan.FromHours(5) ? local.Date : local.Date.AddDays(-1);

    /// <summary>The Monday of the game week (Monday 05:00) a realm-local time belongs to.</summary>
    internal static DateTime GameWeek(DateTime local)
    {
        var day = GameDay(local);
        return day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
    }

    private static int Remaining(DailyState state) => DBDailyActivityInfo.Parser.ParseFrom(state.Rows).ActivityData.TryGetValue(RingActivity, out var row)
        ? row.ActivityInfo?.ActivityParam?.RunRingTask?.RemainRingCount ?? 0 : 0;

    private static void UpdateRingCount(DBDailyActivityInfo rows, DailyState state)
    {
        if (!rows.ActivityData.TryGetValue(RingActivity, out var row)) return;
        row.ActivityInfo ??= new PlayerActivityInfo();
        row.ActivityInfo.ActivityParam ??= new PlayerActivityParam();
        // Remain count is sent even while the chain is active; accept consumes one allowance (inferred).
        row.ActivityInfo.ActivityParam.RunRingTask = new PlayerActivityRunRingTask
        { RemainRingCount = state.ActiveRingStart != 0 || row.TaskCompleteTimes > 0 ? 0 : RingRuns };
    }

    private static DBDailyActivityData NewRow(int id, long epoch) => new()
    { ActivityId = id, ActivityState = (EDailyState)1, LastRefreshTime = epoch };

    private static DBDailyActivityInfo Rows(GameSession session) => session.DailyRows!;

    private void Save(GameSession session)
    {
        if (session.Daily == null) return;
        session.Daily.Rows = Rows(session).ToByteArray();
        _roles.SaveDaily(session.Role!.Id, JsonSerializer.SerializeToUtf8Bytes(session.Daily));
    }

    private async Task SyncClaimsAsync(GameSession session)
    {
        var info = new DBStageRewardInfo();
        info.AllStageRewardsFinished.Add(session.Daily!.Claims.Order());
        if (info.AllStageRewardsFinished.Count > 0) { await CharacterService.SyncAsync(session, StageSync, info); return; }
        // NW_GameServerHandler.lua enables no_decode_default_array; LC_PbHelper.lua:1979-2112 iterates all.value.
        // Lua-pb allocates {} only when the packed value tag is present. An omitted list or all:{} leaves the
        // client's StageRewardReceiveMap nil (native client runtime NOT RUN).
        long version = session.SyncVersions.GetValueOrDefault(StageSync) + 1;
        session.SyncVersions[StageSync] = version;
        session.Synced[StageSync] = info;
        var full = AutoSync.Full(info, version).ToByteArray();
        await session.SendGameRawAsync(StageSync, [.. full, 0x52, 0x04, 0x0A, 0x02, 0x0A, 0x00]);
    }

    /// <summary>Replay the claims snapshot after reconnect without reconciling epochs or enrolling tasks.</summary>
    public async Task ReplayClaimsAsync(GameSession session)
    {
        session.Synced.Remove(StageSync);
        await SyncClaimsAsync(session);
    }

    private bool IsDailyStage(int id) => _data.Row("GenericStageRewardsConfig", id) is { } row && GameData.Int(row, "_iType") == TodoStage;

    private bool Available(GameSession session, ActivityDef def) => def.Books.Length == 0 || def.Books.Contains(0) || def.Books.Contains(_seasons.PlayBook);

    /// <summary>Selected Limited-Time task rows progress only in the client calendar's active window (inferred server gate).</summary>
    private bool ParticipationOpen(int id) => id is not (114 or 126 or 200 or 201 or 3003 or 3013)
        || _timedSchedule.IsOpen(id);

    private Dictionary<int, ActivityDef> Defs()
    {
        if (_definitions != null) return _definitions;
        var defs = new Dictionary<int, ActivityDef>();
        foreach (var row in _data.Table("DailyActivityConfig").Values)
        {
            int id = (int)GameData.Int(row, "_iID");
            if (id == 0) continue;
            var ids = new HashSet<long>(Numbers(row, "_kTaskProgress"));
            if (row.TryGetProperty("_iTaskReceiveAction", out var actions) && actions.ValueKind == JsonValueKind.Array)
                foreach (var entry in actions.EnumerateArray())
                {
                    var action = entry.ValueKind == JsonValueKind.Array ? entry.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray() : [];
                    if (action.Length >= 3 && action[0] == 31)
                        foreach (long task in action.Skip(2).Take((int)action[1])) ids.Add(task);
                }
            if (_data.Row("LC_ConditionList", GameData.Int(row, "_iCheckCondition")) is { } cond
                && cond.TryGetProperty("m_kSubConditionList", out var subs) && subs.ValueKind == JsonValueKind.Array)
                foreach (var sub in subs.EnumerateArray())
                    if (Numbers(sub, "m_kData") is [4004, var task, ..]) ids.Add(task);
            defs[id] = new ActivityDef(id, (int)GameData.Int(row, "_iFunction"), (int)GameData.Int(row, "_iCount"),
                (int)GameData.Int(row, "_iResetTimeType"), Numbers(row, "_kMultiverseArray").Select(n => (int)n).ToArray(),
                ids.Where(task => _data.Row("TaskConfig", task) != null).ToArray());
        }
        _definitions = defs;
        return defs;
    }

    private Dictionary<long, List<int>> ByTask()
    {
        if (_byTask != null) return _byTask;
        var result = new Dictionary<long, List<int>>();
        foreach (var def in Defs().Values)
            foreach (long task in def.Tasks)
                (result.TryGetValue(task, out var ids) ? ids : result[task] = new()).Add(def.Id);
        return _byTask = result;
    }

    private static long[] Numbers(JsonElement row, string field) => row.TryGetProperty(field, out var a) && a.ValueKind == JsonValueKind.Array
        ? a.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray() : [];
}
