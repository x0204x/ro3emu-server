using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Seven-Day Goal (FirstHeart, NewActivityConfig 114, enum_NormalActivities.SevenDayGoal; ThreeDays panel tab).
/// LC_DAL_FirstHeart reads 5049 DBFirstHeartTaskActivityData.activityData[1] (the _iActivityType / request type):
/// cfgData = FirstHeartGroupConfig day -> 0 locked / 1 unlocked (enum_SevenDayGoalDayLockStatus; only unlocked days list
/// their tasks), tasks = FirstHeartTaskInfo {status 0 running, 1 claimable, 2 claimed (enum_SevenDayGoalTaskStatus),
/// scores[1] = progress}, scores = the score bar, GetRewards keys = claimed FirstHeartScoreConfig ids. 83500 claims a
/// claimable task (_iAward), 83501 a score reward once scores reach _iScore (UI_Sys_ThreeDays_SevenDayGoal_*).
/// Progress targets are the shared ProgressConfig ids: 101002 base level and 103007/103044 card quality/stars (TaskService
/// snapshots), 101011 cumulative login days (AchievementService's count). The role blob is "first_heart".
/// (inferred) The seven days start when the role first meets the activity's open condition (GetLvTime[1] = that time);
/// day N of _kStage [30, N] opens N-1 local 05:00 days later; a task's _iPoint is scored when its reward is claimed;
/// isAllTaskComplete = every listed task claimed; the configured window (TimeLimit 1010) is permanent, so days never close.
/// </summary>
public sealed class FirstHeartService : IGameTicker
{
    public const uint Sync = 5049, GetTaskReward = 83500, GetScoreReward = 83501;
    public const long Activity = 114;
    private const long Type = 1; // _iActivityType of every FirstHeart row: the activityData key and the requests' type
    private const long LoginDays = 101011; // CheckLoginDayCumulativeNum
    private const long Locked = 0, Unlocked = 1;
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromSeconds(1);
    private const int SweepSlices = 5;

    private sealed record Day(long Id, int Number, long[] Tasks);
    private sealed record TaskRow(long Id, long Type, long Point, long Award, long[] Target);

    private readonly GameData _data;
    private readonly FunctionService _functions;
    private readonly SeasonService _seasons;
    private readonly ActivityClock _activityClock;
    private readonly AwardService _awards;
    private readonly TaskService _tasks;
    private readonly AchievementService _achievements;
    private readonly CharacterService _character;
    private readonly RoleStore _roles;
    private readonly SessionRegistry _sessions;
    private readonly GameLoop _loop;
    private readonly TimeProvider _clock;
    private readonly ILogger<FirstHeartService> _log;
    private readonly Day[] _days;
    private readonly Dictionary<long, TaskRow> _taskRows = new();
    private readonly HashSet<GameSession> _dirty = new(), _queued = new();
    private TimeSpan _lastTick;
    private int _sweepIndex;

    private long Now => _clock.GetUtcNow().ToUnixTimeSeconds();

    public FirstHeartService(GameData data, CharacterService character, FunctionService functions, SeasonService seasons,
        ActivityClock activityClock, AwardService awards, TaskService tasks, AchievementService achievements, CardService cards,
        RoleStore roles, SessionRegistry sessions, GameLoop loop, GmCommands gm, TimeProvider clock, ILogger<FirstHeartService> log)
    {
        this._data = data; this._character = character; this._functions = functions; this._seasons = seasons;
        this._activityClock = activityClock; this._awards = awards; this._tasks = tasks; this._achievements = achievements;
        this._roles = roles; this._sessions = sessions; this._loop = loop; this._clock = clock; this._log = log;
        _days = data.Table("FirstHeartGroupConfig").Values
            .Where(r => GameData.Int(r, "_iActivityType") == Type && GameData.Int(r, "_iActivityID") == Activity)
            .Select(r => new Day(GameData.Int(r, "_iID"), (int)(Numbers(r, "_kStage").ElementAtOrDefault(1)), Numbers(r, "_kTasklist")))
            .Where(d => d.Number > 0).OrderBy(d => d.Number).ToArray();
        foreach (var r in data.Table("FirstHeartConfig").Values)
            if (r.TryGetProperty("_kProgressTarget", out var targets) && targets.ValueKind == JsonValueKind.Array
                && targets.GetArrayLength() > 0 && targets[0].ValueKind == JsonValueKind.Array)
                _taskRows[GameData.Int(r, "_iID")] = new TaskRow(GameData.Int(r, "_iID"), GameData.Int(r, "_iType"),
                    GameData.Int(r, "_iPoint"), GameData.Int(r, "_iAward"),
                    targets[0].EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray());
        character.StateChanged.Add(MarkDirty);
        character.ProgressHandlers.Add(s => { MarkDirty(s); return Task.CompletedTask; });
        cards.CollectionChanged.Add(s => { MarkDirty(s); return Task.CompletedTask; });
        gm.Register("firstheart", "day <n> | reset | info (Seven-Day Goal test aid: move the start so day n is open)", GmAsync);
    }

    private static long[] Numbers(JsonElement row, string field) =>
        row.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray() : [];

    private int PlayBook(GameSession s) => (int)(s.SeasonState?.PlayBookId ?? _seasons.PlayBook);

    /// <summary>NewActivityConfig 114: its book, window and open condition (21000012: base level 30, 10 before the
    /// 20 Oct 2026 client), as for 5013.</summary>
    private bool Open(GameSession s)
    {
        if (s.Role == null || s.Progress == null || _data.Row("NewActivityConfig", Activity) is not { } row
            || _seasons.OpenContent.Closes(Activity)) return false;
        int book = PlayBook(s);
        if (!row.TryGetProperty("_kMultiverseArray", out var books) || books.ValueKind != JsonValueKind.Array
            || !books.EnumerateArray().Any(b => GameData.Number(b) is 0 || GameData.Number(b) == book)) return false;
        long now = Now;
        if (!_activityClock.ResolveActivity(row, now, s.Role.CreateTime, book).Contains(now)) return false;
        return _seasons.OpenContent.ForcesActivity(Activity) || _functions.ConditionMet(s, GameData.Int(row, "_kOpenCondition"));
    }

    /// <summary>Day number since the start: local days that begin at 05:00, the realm's daily reset (inferred).</summary>
    private int DayNumber(long start)
    {
        DateTime LocalDay(long seconds) =>
            TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(seconds), _clock.LocalTimeZone).DateTime.AddHours(-5).Date;
        return (LocalDay(Now) - LocalDay(start)).Days + 1;
    }

    /// <summary>A target's (current, required): login days from the achievement count, the rest from task snapshots.</summary>
    private (long Value, long Required)? Progress(GameSession s, long[] target) => target switch
    {
        [LoginDays, var n] => (_achievements.LoginDays(s), n),
        _ => _tasks.SnapshotTarget(s, target),
    };

    /// <summary>The state with the start, day locks and running tasks' progress brought up to now (a pure read of the role).</summary>
    private DBFirstHeartTaskActivityData Evaluate(GameSession s, DBFirstHeartTaskActivityData state)
    {
        bool open = Open(s);
        if (!state.GetLvTime.ContainsKey(Type))
        {
            if (!open) return state;
            state.GetLvTime[Type] = Now;
        }
        if (!open) return state; // a closed activity keeps its state as it was
        if (!state.ActivityData.TryGetValue(Type, out var cfg)) state.ActivityData[Type] = cfg = new FirstHeartTaskCfgData();
        int today = DayNumber(state.GetLvTime[Type]);
        foreach (var day in _days)
        {
            bool unlocked = today >= day.Number;
            cfg.CfgData[day.Id] = unlocked ? Unlocked : Locked;
            if (!unlocked) continue;
            foreach (long id in day.Tasks)
            {
                if (!_taskRows.TryGetValue(id, out var row)) continue;
                if (!cfg.Tasks.TryGetValue(id, out var info)) cfg.Tasks[id] = info = new FirstHeartTaskInfo { TaskId = id };
                if (info.Status != FirstHeartTaskState.None) continue;
                var progress = Progress(s, row.Target);
                long value = progress is { Required: > 0 } p ? Math.Min(p.Value, p.Required) : 0;
                if (info.Scores.Count != 1 || info.Scores[0] != value) { info.Scores.Clear(); info.Scores.Add(value); }
                if (progress is { Required: > 0 } done && done.Value >= done.Required) info.Status = FirstHeartTaskState.Accept;
            }
        }
        cfg.IsAllTaskComplete = _days.SelectMany(d => d.Tasks).Where(_taskRows.ContainsKey)
            .All(id => cfg.Tasks.GetValueOrDefault(id)?.Status == FirstHeartTaskState.GetReward);
        return state;
    }

    private bool Ready(GameSession s) => s.Role != null && s.FirstHeart != null;

    // ------------------------------------------------------------------ state / 5049

    /// <summary>Before login nothing is shown: the state is read and brought up to date by <see cref="LoginAsync"/>.</summary>
    public void Load(GameSession s) => s.FirstHeart = null;

    /// <summary>After achievements (their login day is counted): bring the saved state up to now, save, 5049.</summary>
    public async Task LoginAsync(GameSession s)
    {
        var bytes = _roles.LoadFirstHeart(s.Role!.Id);
        var saved = bytes is { Length: > 0 } ? DBFirstHeartTaskActivityData.Parser.ParseFrom(bytes) : new DBFirstHeartTaskActivityData();
        var next = Evaluate(s, saved.Clone());
        if (!next.Equals(saved)) _roles.SaveFirstHeart(s.Role.Id, next.ToByteArray());
        s.FirstHeart = next;
        await SyncAsync(s);
    }

    /// <summary>
    /// LC_DAL_FirstHeart indexes activityData[1] and calls pairs on cfgData, tasks and GetRewards without nil checks, so
    /// nothing is sent before the start. An absent protobuf map decodes as nil: the first 5049 of a connection carries a
    /// wire-only GetRewards[0] that the next increment deletes, as SeasonRefineService does (inferred transport marker;
    /// the client keeps 0 in its claimed list, which no score row uses).
    /// </summary>
    private async Task SyncAsync(GameSession s)
    {
        if (s.FirstHeart is not { } state || !state.ActivityData.TryGetValue(Type, out var cfg)) return;
        if (!s.Synced.ContainsKey(Sync) && cfg.GetRewards.Count == 0)
        {
            var wire = state.Clone();
            wire.ActivityData[Type].GetRewards[0] = 0;
            await CharacterService.SyncAsync(s, Sync, wire);
        }
        await CharacterService.SyncAsync(s, Sync, state.Clone());
    }

    /// <summary>Asset replay (1023): the same first-sync initialization as login.</summary>
    public async Task ReplayAsync(GameSession s)
    {
        s.Synced.Remove(Sync);
        await SyncAsync(s);
    }

    // ------------------------------------------------------------------ progress

    private void MarkDirty(GameSession s)
    {
        if (!_loop.IsOnLoop) { _loop.PostRuntime(() => { MarkDirty(s); return Task.CompletedTask; }); return; }
        if (Ready(s)) _dirty.Add(s);
    }

    /// <summary>Hook-marked roles every second, every role once per five seconds (a day opening, a login day counted).</summary>
    public void Tick(GameTime time)
    {
        if (time.Now - _lastTick < ProbeEvery) return;
        _lastTick = time.Now;
        var all = _sessions.All();
        var probe = new HashSet<GameSession>(_dirty);
        _dirty.Clear();
        if (all.Count > 0)
        {
            int slice = (all.Count + SweepSlices - 1) / SweepSlices;
            for (int i = 0; i < slice; i++) probe.Add(all[(_sweepIndex + i) % all.Count]);
            _sweepIndex = (_sweepIndex + slice) % all.Count;
        }
        foreach (var s in probe)
        {
            if (!Ready(s) || _queued.Contains(s)) continue;
            if (s.Handoff != null) { _dirty.Add(s); continue; }
            var current = s.FirstHeart!;
            if (current.ActivityData.GetValueOrDefault(Type)?.IsAllTaskComplete == true) continue;
            if (!Evaluate(s, current.Clone()).Equals(current)) PostRefresh(s);
        }
    }

    private void PostRefresh(GameSession s)
    {
        if (!_queued.Add(s)) return;
        _loop.PostOwned(s, async () =>
        {
            _queued.Remove(s);
            if (!Ready(s)) return;
            bool changed;
            using (await CharacterService.Lock(s)) changed = Ready(s) && Refresh(s);
            if (changed) await SyncAsync(s);
        }, "firstheart refresh", discarded: () => _queued.Remove(s));
    }

    /// <summary>Under the character lock: save the brought-up-to-date state when it changed.</summary>
    private bool Refresh(GameSession s)
    {
        var next = Evaluate(s, s.FirstHeart!.Clone());
        if (next.Equals(s.FirstHeart)) return false;
        _roles.SaveFirstHeart(s.Role!.Id, next.ToByteArray());
        s.FirstHeart = next;
        return true;
    }

    // ------------------------------------------------------------------ claims

    /// <summary>83500: a claimable task of an open day pays its _iAward once and adds its _iPoint to the score bar.</summary>
    public async Task ClaimTaskAsync(GameSession s, FirstHeartGetTaskRewardReq req)
    {
        int result;
        using (await CharacterService.Lock(s))
        {
            result = Validate(s, req.Type, out var cfg);
            if (result == 0)
            {
                var info = cfg!.Tasks.GetValueOrDefault(req.TaskId);
                result = !_taskRows.TryGetValue(req.TaskId, out var row) || row.Type != req.Type ? ErrorCodes.Parameter
                    : info == null || !OpenDay(cfg, req.TaskId) ? ErrorCodes.TaskRequirements
                    : info.Status == FirstHeartTaskState.GetReward ? ErrorCodes.RewardClaimed
                    : info.Status != FirstHeartTaskState.Accept ? ErrorCodes.TaskRequirements
                    : 0;
                if (result == 0)
                {
                    var next = s.FirstHeart!.Clone();
                    var claimed = next.ActivityData[Type];
                    claimed.Tasks[req.TaskId].Status = FirstHeartTaskState.GetReward;
                    claimed.Scores += row!.Point;
                    claimed.IsAllTaskComplete = _days.SelectMany(d => d.Tasks).Where(_taskRows.ContainsKey)
                        .All(id => claimed.Tasks.GetValueOrDefault(id)?.Status == FirstHeartTaskState.GetReward);
                    result = await CommitClaimAsync(s, next, row.Award, $"task/{req.TaskId}");
                }
            }
        }
        await s.SendGameAsync(GetTaskReward, new FirstHeartGetTaskRewardResp { Result = result });
        await SyncAsync(s);
    }

    /// <summary>83501: a FirstHeartScoreConfig row of the type pays its _iAward once the score bar reaches its _iScore.</summary>
    public async Task ClaimScoreAsync(GameSession s, FirstHeartGetScoreRewardReq req)
    {
        int result;
        using (await CharacterService.Lock(s))
        {
            result = Validate(s, req.Type, out var cfg);
            if (result == 0)
            {
                var row = _data.Row("FirstHeartScoreConfig", req.RewardId);
                result = row is not { } r || GameData.Int(r, "_iActivityType") != req.Type ? ErrorCodes.Parameter
                    : cfg!.GetRewards.ContainsKey(req.RewardId) ? ErrorCodes.RewardClaimed
                    : cfg.Scores < GameData.Int(r, "_iScore") ? ErrorCodes.TaskRequirements
                    : 0;
                if (result == 0)
                {
                    var next = s.FirstHeart!.Clone();
                    next.ActivityData[Type].GetRewards[req.RewardId] = Now; // the client reads only the key (inferred value)
                    result = await CommitClaimAsync(s, next, GameData.Int(row!.Value, "_iAward"), $"score/{req.RewardId}");
                }
            }
        }
        await s.SendGameAsync(GetScoreReward, new FirstHeartGetScoreRewardResp { Result = result });
        await SyncAsync(s);
    }

    /// <summary>The claim's common gate, after bringing the state up to now: a started, open activity of this type.</summary>
    private int Validate(GameSession s, long type, out FirstHeartTaskCfgData? cfg)
    {
        cfg = null;
        if (!Ready(s)) return ErrorCodes.FeatureUnavailable;
        Refresh(s);
        if (type != Type) return ErrorCodes.Parameter;
        if (!Open(s) || !s.FirstHeart!.ActivityData.TryGetValue(Type, out cfg)) return ErrorCodes.ActivityClosed;
        return 0;
    }

    private bool OpenDay(FirstHeartTaskCfgData cfg, long taskId) =>
        _days.Any(d => d.Tasks.Contains(taskId) && cfg.CfgData.GetValueOrDefault(d.Id) == Unlocked);

    /// <summary>The claim's award and the new state in one owned-award receipt; on any failure nothing changed.</summary>
    private async Task<int> CommitClaimAsync(GameSession s, DBFirstHeartTaskActivityData next, long group, string claim)
    {
        long role = s.Role!.Id;
        AwardPlan plan;
        try { plan = _awards.Resolve(s, group, PlayBook(s)); }
        catch (AwardResolutionException e)
        {
            _log.LogWarning("Seven-Day Goal {Claim} reward unresolved for {Role}: {Reason}", claim, role, e.Message);
            return ErrorCodes.RewardMissing;
        }
        byte[] bytes = next.ToByteArray();
        // The start is part of the key: a GM reset starts a new run whose claims are new sources.
        string source = $"firstheart/{role}/{next.GetLvTime[Type]}/{claim}";
        try
        {
            bool ok = await _character.ApplyAwardAsync(s, new AwardPlan(0, 0, plan.Grants),
                persist: tx => _roles.SaveFirstHeart(role, bytes, tx), stateLockHeld: true,
                committed: () => s.FirstHeart = next,
                ownedAward: new CharacterService.OwnedAwardSource("cd13e/firstheart-award", source,
                    new { Version = 1, Claim = claim, Award = plan.AwardId }));
            if (!ok) _log.LogWarning("Seven-Day Goal {Claim} not paid for {Role}", claim, role);
            return ok ? 0 : ErrorCodes.Unknown;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError(e, "Seven-Day Goal {Claim} transaction failed for {Role}", claim, role);
            return ErrorCodes.Unknown;
        }
    }

    // ------------------------------------------------------------------ GM

    private async Task GmAsync(GameSession s, string[] args)
    {
        string reply;
        bool changed = false;
        using (await CharacterService.Lock(s))
        {
            changed = Ready(s) && Refresh(s); // a level just reached starts it before the command reads it
            if (!Ready(s)) reply = "No Seven-Day Goal state.";
            else if (args is ["day", _] && !s.FirstHeart!.GetLvTime.ContainsKey(Type)) reply = "Seven-Day Goal not started (base level 10).";
            else if (args is ["day", var text] && int.TryParse(text, out int day) && day >= 1)
            {
                var next = s.FirstHeart!.Clone();
                next.GetLvTime[Type] = Now - (day - 1) * 86400L;
                _roles.SaveFirstHeart(s.Role!.Id, next.ToByteArray());
                s.FirstHeart = next;
                Refresh(s);
                changed = true;
                reply = $"Seven-Day Goal: day {day} is open.";
            }
            else if (args is ["reset"])
            {
                var next = Evaluate(s, new DBFirstHeartTaskActivityData());
                _roles.SaveFirstHeart(s.Role!.Id, next.ToByteArray());
                s.FirstHeart = next;
                s.Synced.Remove(Sync);
                changed = true;
                reply = "Seven-Day Goal reset.";
            }
            else if (args is [] or ["info"])
            {
                var cfg = s.FirstHeart!.ActivityData.GetValueOrDefault(Type);
                reply = cfg == null ? "Seven-Day Goal not started (base level 10)."
                    : $"Seven-Day Goal day {DayNumber(s.FirstHeart.GetLvTime[Type])}, score {cfg.Scores}, tasks "
                        + string.Join(" ", cfg.Tasks.Values.OrderBy(t => t.TaskId).Select(t => $"{t.TaskId}:{(int)t.Status}"));
            }
            else reply = "Usage: !firstheart day <n> | reset | info";
        }
        if (changed) await SyncAsync(s);
        await GmCommands.ReplyAsync(s, reply);
    }
}
