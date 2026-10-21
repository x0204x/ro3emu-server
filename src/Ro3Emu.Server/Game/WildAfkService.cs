using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Field Bonus Time: 5035 and the OnHook panel's 434xx requests (LC_DAL_OnHook).
/// PublicParamConfig supplies the daily 60 minutes, 180 minute cap, 05:00 reset and ten-second kill monitor;
/// Localization_en 10960000077 proves suspension, dungeon exclusion and MVP aggro exclusion.
/// (inferred) AutoBattle is the activity switch; only ordinary field kills with granted drops renew the monitor.
/// NPCConfig._iDrop uses environment 0 for reduced rewards and 1 for qualified Bonus Time (inferred);
/// _iNormalDrop remains additive under its own configured conditions.
/// No offline reward formula is present in the current schema; 43405 refuses without granting phantom loot.
/// </summary>
public sealed class WildAfkService : IGameTicker
{
    public const uint Sync = 5035, Info = 43401, Notify = 43402, SaveCharge = 43403, Collection = 43404,
        OfflineReward = 43405, Magnification = 43406, DungeonStart = 43408, Start = 43409, Pause = 43410,
        Snapshot = 43411, SuspendReason = 43412;

    private readonly GameData _data;
    private readonly RoleStore _roles;
    private readonly FunctionService _functions;
    private readonly TaskService _tasks;
    private readonly DailyActivityService _daily;
    private readonly SessionRegistry _sessions;
    private readonly World _world;
    private readonly GameLoop _loop;

    /// <summary>Durable dataset and reset/accounting metadata; clock anchors and the UI power-saving flag are session-only.</summary>
    public sealed class State
    {
        public byte[] Data { get => Info.ToByteArray(); set => Info = DBWildAFKInfo.Parser.ParseFrom(value); }
        public long DailyEpoch { get; set; }
        public long ConsumedTodaySeconds { get; set; }
        public long RemainderMs { get; set; }
        public long LastToggleMs { get; set; }
        public Dictionary<int, long> TotalDrops { get; set; } = new();
        [JsonIgnore] public DBWildAFKInfo Info { get; set; } = new();
        [JsonIgnore] public long AccountedAtMs { get; set; }
        [JsonIgnore] public long LastKillAtMs { get; set; }
        [JsonIgnore] public bool PowerSaving { get; set; }
        [JsonIgnore] public string LastReason { get; set; } = "";
        /// <summary>The bytes last written to the role row; an identical state is not written again.</summary>
        [JsonIgnore] public byte[]? Persisted { get; set; }
        /// <summary>The 5035 body this session last synced; an identical one is not diffed again.</summary>
        [JsonIgnore] public byte[]? SyncedInfo { get; set; }
        [JsonIgnore] public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    public WildAfkService(GameData data, RoleStore roles, FunctionService functions, TaskService tasks, DailyActivityService daily,
        SessionRegistry sessions, World world, GameLoop loop, SceneService scene, CombatService combat, RewardService rewards)
    {
        this._data = data;
        this._roles = roles;
        this._functions = functions;
        this._tasks = tasks;
        this._daily = daily;
        this._sessions = sessions;
        this._world = world;
        this._loop = loop;
        scene.Leaving.Add(s => SuspendAsync(s));
        scene.Loaded.Add(s => ReconcileAsync(s));
        combat.PlayerDied.Add(s => SuspendAsync(s));
        rewards.QualifiesForBonus = BeforeKillAsync;
        rewards.StageAfkKill = StageAfkKill;
        rewards.KillDropsGranted.Add(DropsGrantedAsync);
        daily.BeforeReset = BeforeDailyResetAsync;
        daily.PlanAfkRollover = PlanDailyRollover;
    }

    private long Param(string key, long fallback) => _data.Table("PublicParamConfig").TryGetValue(key, out var value)
        ? (long)(GameData.Number(value) ?? fallback) : fallback;
    private long DailySeconds => Param("OnHookDailyFightTime", 60) * 60;
    private long CapSeconds => Param("OnHookFightTimeLimit", 180) * 60;
    private long MonitorMs => Param("OnHookConditionMonito", 10) * 1000;
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    // A queued operation may acquire the old role's gate after logout or a role switch replaced this state.
    private static bool Owns(GameSession s, State state) => s.Role != null && ReferenceEquals(s.WildAfk, state);
    private async Task<LegacyOwnerScope?> EnterStateScopeAsync(GameSession s, State state, string origin)
    {
        long? role = s.Role?.Id;
        if (role is null || !Owns(s, state)) return null;
        if (_loop.Owners is not { } owners) return null;
        var scope = await owners.EnterAsync("wild-afk/" + origin, [ReservationKey.Role(role.Value)]);
        if (s.Role?.Id != role || !Owns(s, state)) { scope.Dispose(); return null; }
        try { if (s.Owner is { } owner) scope.Context.Pin(owner); }
        catch { scope.Dispose(); throw; }
        return scope;
    }
    private bool Unlocked(GameSession s) => s.Progress != null && _functions.IsOpen(s, 18);
    private bool Field(GameSession s) => s.InstanceId == 0 && _data.Row("MapConfig", s.SceneEntry) is { } map
        && GameData.Int(map, "_iSceneType") == 1; // enum scene type: field; private/dungeon maps never qualify.
    private bool Ordinary(MonsterUnit monster) => _data.Row("NPCConfig", monster.Template.NpcId) is { } npc
        && GameData.Int(npc, "_iWildMonsters") == 1 && GameData.Int(npc, "_iNPCSubType") is 1 or 2;
    private bool MvpAggro(GameSession s) => _world.AllUnits().OfType<MonsterUnit>().Any(m => !m.Dead && !m.Returning
        && m.WorldScene == s.WorldScene && m.Target == s && _data.Row("NPCConfig", m.Template.NpcId) is { } npc
        && GameData.Int(npc, "_iNPCSubType") is 3 or 4);

    private long Epoch(long nowMs)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).LocalDateTime;
        var day = local.AddHours(-Param("OnHookDailyRefreshTime", 5)).Date.AddHours(Param("OnHookDailyRefreshTime", 5));
        return new DateTimeOffset(day).ToUnixTimeSeconds();
    }

    /// <summary>Before quest login: persisted seconds, rollover and a safe full 5035 even for a locked new role.</summary>
    public void Load(GameSession s)
    {
        var saved = _roles.LoadWildAfk(s.Role!.Id);
        var state = saved == null ? new State() : JsonSerializer.Deserialize<State>(saved) ?? new State();
        s.WildAfk = state;
        s.AutoBattle = false;
        long now = Now;
        if (!state.Info.IsInit)
        {
            state.Info.IsInit = true;
            state.Info.HighMultiplierTimeSeconds = DailySeconds;
            state.Info.IsHighMagnification = true; // daily auto-enable: UIWords 37014 (initial default inferred).
            state.DailyEpoch = Epoch(now);
        }
        var dailyBytes = _roles.LoadDaily(s.Role.Id);
        var oldDaily = dailyBytes == null ? null : JsonSerializer.Deserialize<DailyActivityService.DailyState>(dailyBytes);
        var oldRows = oldDaily == null ? null : DBDailyActivityInfo.Parser.ParseFrom(oldDaily.Rows);
        var questBytes = _roles.LoadTasks(s.Role.Id);
        var oldQuests = questBytes == null ? null : DBTaskInfo.Parser.ParseFrom(questBytes);
        if (!PendingDailyAward(state, oldDaily, oldQuests, oldRows)) Restore(state, now);
        state.Info.IsSuspended = state.Info.IsHighMagnification;
        state.AccountedAtMs = now;
        state.LastKillAtMs = 0; // Never charge disconnected time or reuse a previous role's activity.
        Save(s);
    }

    public Task LoginAsync(GameSession s) => SyncAsync(s);
    /// <summary>
    /// Writes the persisted part of the AFK state when it changed. The per-second reconcile of an idle role produced
    /// the same bytes every time (AccountedAtMs is not persisted) and still committed them: one durable UPDATE per
    /// eligible role per second.
    /// </summary>
    private void Save(GameSession s)
    {
        var state = s.WildAfk!;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        if (state.Persisted is { } last && last.AsSpan().SequenceEqual(bytes)) return;
        _roles.SaveWildAfk(s.Role!.Id, bytes);
        state.Persisted = bytes;
    }

    private async Task SyncAsync(GameSession s)
    {
        var state = s.WildAfk!;
        var bytes = state.Info.ToByteArray();
        if (state.SyncedInfo is { } last && last.AsSpan().SequenceEqual(bytes)) return; // the diff would be empty
        await CharacterService.SyncAsync(s, Sync, state.Info.Clone());
        state.SyncedInfo = bytes;
    }

    /// <summary>Calendar-day catch-up (not 86400-second division, so local DST cannot skip a reset).</summary>
    private bool Restore(State state, long now)
    {
        long epoch = Epoch(now);
        if (epoch <= state.DailyEpoch) return false;
        var previous = DateTimeOffset.FromUnixTimeSeconds(state.DailyEpoch).LocalDateTime.Date;
        var current = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime.Date;
        long days = Math.Max(1, (current - previous).Days);
        state.Info.HighMultiplierTimeSeconds = Math.Min(CapSeconds, state.Info.HighMultiplierTimeSeconds + days * DailySeconds);
        state.Info.TodayKillCount = 0;
        state.ConsumedTodaySeconds = 0;
        state.Info.IsHighMagnification = true;
        state.Info.IsSuspended = true;
        state.DailyEpoch = epoch;
        // Cumulative snapshots must not go backwards when old dated records are pruned (IncomeWidget subtracts them).
        int keep = (int)Math.Max(1, Param("OnHookRecordLimit", 8));
        foreach (long key in state.Info.DailyDropRecords.Keys.OrderDescending().Skip(keep).ToArray()) state.Info.DailyDropRecords.Remove(key);
        return true;
    }

    private string InactiveReason(GameSession s, long now)
    {
        if (!Unlocked(s) || !Field(s) || s.SceneLoading || s.Dead || !s.AutoBattle) return "";
        if (MvpAggro(s)) return "MvpHate"; // enum_OnHookSuspendReason, localized by LC_DAL_OnHook.
        if (s.WildAfk!.LastKillAtMs == 0 || now >= s.WildAfk.LastKillAtMs + MonitorMs) return "NoKillTimeout";
        return "active";
    }

    /// <summary>
    /// Settle elapsed eligible milliseconds once, capped at the kill-monitor deadline. Fractional seconds survive
    /// pause/relogin; requests cannot create free time by toggling. A backwards wall-clock correction costs zero.
    /// (inferred) sumHighMultiplierTime is lifetime consumed seconds; daily tracking uses its own reset counter.
    /// </summary>
    private async Task ReconcileCoreAsync(GameSession s, long now, bool forceSuspended = false, bool publish = true)
    {
        var state = s.WildAfk!;
        var info = state.Info;
        long consumed = 0;
        bool running = info.IsHighMagnification && !info.IsSuspended;
        // Settle the old day's eligible interval before adding the new allowance, even if the loop crossed 05:00.
        bool reset = Epoch(now) > state.DailyEpoch;
        if (reset && publish && s.DailyReady) await _daily.ReconcileAsync(s);
        if (reset && running) ConsumeUntil(state, Math.Min(now, Epoch(now) * 1000));
        if (reset && !publish && PendingDailyAward(state, s.Daily, s.Tasks, s.DailyRows))
        {
            info.IsSuspended = info.IsHighMagnification;
            Save(s); // disconnected: preserve the ending period until login can settle its held award
            return;
        }
        if (reset)
        {
            Restore(state, now);
            state.AccountedAtMs = Math.Max(state.AccountedAtMs, state.DailyEpoch * 1000);
        }
        if (running) consumed = ConsumeUntil(state, now);
        state.AccountedAtMs = Math.Max(state.AccountedAtMs, now);
        if (info.HighMultiplierTimeSeconds <= 0)
        {
            info.HighMultiplierTimeSeconds = 0;
            info.IsHighMagnification = false;
            state.RemainderMs = 0;
        }
        string reason = forceSuspended ? "" : InactiveReason(s, now);
        bool wasSuspended = info.IsSuspended;
        info.IsSuspended = info.IsHighMagnification && reason != "active";
        if (info.IsSuspended && !wasSuspended) info.HighMultiplierPausedAt = now / 1000;
        Save(s);
        if (!publish) return; // Disconnected sockets must not interrupt durable accounting or world cleanup.
        if ((reset || consumed > 0 || PendingDailyAward(state, s.Daily, s.Tasks, s.DailyRows))
            && s.Tasks != null && s.DailyReady) await _tasks.RefreshAsync(s);
        await SyncAsync(s);
        if (info.IsSuspended && reason.Length > 0 && reason != state.LastReason)
            await s.SendGameAsync(SuspendReason, new SuspendReasonRes { Reason = reason });
        state.LastReason = reason;
    }

    /// <summary>
    /// Called with TaskLock before the old quest is discarded. Mutate the clock synchronously on GameLoop;
    /// taking the AFK gate here would invert AFK->Task lock order. The award still uses the normal quest transaction.
    /// </summary>
    private async Task<bool> BeforeDailyResetAsync(GameSession s, long boundary)
    {
        if (s.WildAfk is not { } state || !Owns(s, state) || state.DailyEpoch != s.Daily?.DailyEpoch) return true;
        if (state.Info.IsHighMagnification && !state.Info.IsSuspended && state.AccountedAtMs < boundary * 1000)
        {
            ConsumeUntil(state, boundary * 1000);
            Save(s);
        }
        if (PendingDailyAward(state, s.Daily, s.Tasks, s.DailyRows)) await _tasks.SettleOwnerProgressCoreAsync(s);
        if (!Owns(s, state)) return true;
        return !PendingDailyAward(state, s.Daily, s.Tasks, s.DailyRows);
    }

    /// <summary>
    /// The daily reset command (DailyActivityService, TaskLock held under the role's scope; the AFK gate is not
    /// taken: AFK operations take AFK gate -> TaskLock, so the reverse order may only touch state, never the gate).
    /// The old interval was settled by <see cref="BeforeDailyResetAsync"/>; this restores the new day's allowance
    /// in memory and returns the bytes the reset transaction persists, with the undo for a failed commit.
    /// </summary>
    internal (byte[] Bytes, Action Committed, Action Rollback)? PlanDailyRollover(GameSession s)
    {
        if (s.WildAfk is not { } state || !Owns(s, state)) return null;
        var before = JsonSerializer.SerializeToUtf8Bytes(state);
        long accountedBefore = state.AccountedAtMs;
        if (Restore(state, Now)) state.AccountedAtMs = Math.Max(state.AccountedAtMs, state.DailyEpoch * 1000);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        if (state.Persisted is { } last && last.AsSpan().SequenceEqual(bytes)) return null;
        return (bytes, () => state.Persisted = bytes, () =>
        {
            var old = JsonSerializer.Deserialize<State>(before)!;
            state.Info = old.Info;
            state.DailyEpoch = old.DailyEpoch;
            state.ConsumedTodaySeconds = old.ConsumedTodaySeconds;
            state.RemainderMs = old.RemainderMs;
            state.LastToggleMs = old.LastToggleMs;
            state.TotalDrops = old.TotalDrops;
            state.AccountedAtMs = accountedBefore;
        }
        );
    }

    /// <summary>Daily row 106 is one 500130 delivery for [106901,3600]; held awards must not expire.</summary>
    internal static bool PendingDailyAward(State? afk, DailyActivityService.DailyState? daily,
        DBTaskInfo? quests, DBDailyActivityInfo? rows) => afk != null && daily?.DailyEpoch == afk.DailyEpoch
        && afk.ConsumedTodaySeconds >= 3600 && quests?.Infos.ContainsKey(500130) == true
        && quests.CompleteTaskNum.GetValueOrDefault(500130) == 0
        && rows?.ActivityData.TryGetValue(106, out var row) == true && row.TaskCompleteTimes == 0;

    private long ConsumeUntil(State state, long end)
    {
        long until = Math.Min(end, state.LastKillAtMs + MonitorMs);
        state.RemainderMs += Math.Max(0, until - state.AccountedAtMs);
        long consumed = Math.Min(state.Info.HighMultiplierTimeSeconds, state.RemainderMs / 1000);
        state.Info.HighMultiplierTimeSeconds -= consumed;
        // Nothing beyond an exhausted allowance may carry into the next day's freshly restored bank.
        state.RemainderMs = state.Info.HighMultiplierTimeSeconds > 0 ? state.RemainderMs % 1000 : 0;
        state.Info.SumHighMultiplierTime += consumed;
        state.ConsumedTodaySeconds += consumed;
        state.AccountedAtMs = Math.Max(state.AccountedAtMs, end);
        return consumed;
    }

    public Task ReconcileAsync(GameSession s) => ReconcileAsync(s, null);

    private async Task ReconcileAsync(GameSession s, Func<bool>? currentTick)
    {
        if (s.Role == null || s.WildAfk is not { } state) return;
        if (currentTick != null && !currentTick()) return;
        var role = s.Role;
        using var scope = s.Owners is { } owners
            ? await owners.EnterWithGateAsync("wild-afk/reconcile", state.Gate, [ReservationKey.Role(role.Id)]) : null;
        if (scope == null) await state.Gate.WaitAsync();
        try
        {
            if (Owns(s, state) && ReferenceEquals(s.Role, role) && (currentTick == null || currentTick()))
            {
                if (s.Owner is { } owner) scope?.Context.Pin(owner);
                await ReconcileCoreAsync(s, Now);
            }
        }
        finally { state.Gate.Release(); }
    }

    private async Task SuspendAsync(GameSession s)
    {
        if (s.WildAfk is not { } state || s.Role == null) return;
        using var scope = await EnterStateScopeAsync(s, state, "suspend");
        if (_loop.Owners != null && scope == null) return;
        await state.Gate.WaitAsync();
        try
        {
            if (!Owns(s, state)) return;
            await ReconcileCoreAsync(s, Now, forceSuspended: true);
            state.LastKillAtMs = 0;
        }
        finally { state.Gate.Release(); }
    }

    /// <summary>Disconnect/role-switch: persist the final fraction, with no offline clock or resumed AI.</summary>
    public async Task LeaveAsync(GameSession s)
    {
        if (s.WildAfk is not { } state) return;
        var role = s.Role;
        using var scope = s.Owners is { } owners && role != null
            ? await owners.EnterWithGateAsync("wild-afk/leave", state.Gate, [ReservationKey.Role(role.Id)]) : null;
        if (scope == null) await state.Gate.WaitAsync();
        try
        {
            if (Owns(s, state) && ReferenceEquals(s.Role, role))
            {
                if (s.Owner is { } owner) scope?.Context.Pin(owner);
                await ReconcileCoreAsync(s, Now, forceSuspended: true, publish: false);
            }
        }
        finally
        {
            if (ReferenceEquals(s.WildAfk, state))
            {
                s.AutoBattle = false;
                s.WildAfk = null;
            }
            state.Gate.Release();
        }
    }

    /// <summary>31054 is client AI, no reply. Starting it alone does not manufacture kills or extend the monitor.</summary>
    public async Task SetAutoBattleAsync(GameSession s, bool enabled)
    {
        if (s.WildAfk is not { } state) return;
        using var scope = await EnterStateScopeAsync(s, state, "auto-battle");
        if (_loop.Owners != null && scope == null) return;
        await state.Gate.WaitAsync();
        try
        {
            if (!Owns(s, state)) return;
            await ReconcileCoreAsync(s, Now);
            s.AutoBattle = enabled;
            await ReconcileCoreAsync(s, Now);
        }
        finally { state.Gate.Release(); }
    }

    /// <summary>39000 type 5: apply the merged preference after settling the old interval.</summary>
    public async Task SettingsChangedAsync(GameSession s, SettingWildAFKInfo setting)
    {
        if (s.WildAfk is not { } state) return;
        using var scope = await EnterStateScopeAsync(s, state, "settings");
        if (_loop.Owners != null && scope == null) return;
        await state.Gate.WaitAsync();
        try
        {
            if (!Owns(s, state)) return;
            await ReconcileCoreAsync(s, Now);
            if (setting.DisAbleConsumeAFKTime)
            {
                state.Info.IsHighMagnification = false;
                state.Info.IsSuspended = false;
                state.Info.HighMultiplierPausedAt = Now / 1000;
                Save(s);
                await SyncAsync(s);
            }
        }
        finally { state.Gate.Release(); }
    }

    public async Task InfoAsync(GameSession s)
    {
        if (s.WildAfk is not { } state) return;
        using var scope = await EnterStateScopeAsync(s, state, "info");
        if (_loop.Owners != null && scope == null) return;
        await state.Gate.WaitAsync();
        try
        {
            if (!Owns(s, state)) return;
            await ReconcileCoreAsync(s, Now);
            await s.SendGameAsync(Info, new WildAFKInfoRsp { CurAfkTime = state.Info.HighMultiplierTimeSeconds });
        }
        finally { state.Gate.Release(); }
    }

    /// <summary>43409/43410 (and legacy 43406): ack first, then authoritative 5035 corrects Lua's truthy-result handler.</summary>
    public async Task ToggleAsync(GameSession s, bool enabled, bool legacy = false)
    {
        if (s.WildAfk is not { } state) return;
        using var scope = await EnterStateScopeAsync(s, state, "toggle");
        if (_loop.Owners != null && scope == null) return;
        await state.Gate.WaitAsync();
        try
        {
            if (!Owns(s, state)) return;
            long now = Now;
            await ReconcileCoreAsync(s, now);
            var info = state.Info;
            int error = !Unlocked(s) ? ErrorCodes.FeatureUnavailable
                : enabled && !Field(s) ? ErrorCodes.AfkDungeon
                : enabled && info.HighMultiplierTimeSeconds <= 0 ? ErrorCodes.AfkTimeEmpty
                : enabled == info.IsHighMagnification ? (enabled ? ErrorCodes.AfkAlreadyActive : ErrorCodes.AfkNotActive)
                : now - state.LastToggleMs < Param("OnHookPauseButtonCD", 1) * 1000 ? ErrorCodes.AfkToggleCooldown : 0;
            if (error == 0)
            {
                state.LastToggleMs = now;
                info.IsHighMagnification = enabled;
                info.IsSuspended = enabled && InactiveReason(s, now) != "active";
                if (!enabled) info.HighMultiplierPausedAt = now / 1000;
                Save(s);
            }
            if (legacy) await s.SendGameAsync(Magnification, new SetHighMagnificationRsp { Result = error, Status = info.IsHighMagnification });
            else if (enabled) await s.SendGameAsync(Start, new StartHighMultiplierTimeRsp { Result = error, RemainingTimeSeconds = info.HighMultiplierTimeSeconds });
            // (inferred) The pause summary reports this day's consumed seconds, not time since the last toggle.
            else await s.SendGameAsync(Pause, new PauseHighMultiplierTimeRsp
            {
                Result = error,
                RemainingTimeSeconds = info.HighMultiplierTimeSeconds, ConsumedTimeSeconds = state.ConsumedTodaySeconds
            });
            // The recovered handlers test `if result` (Lua's 0 is truthy), even on refusals: always correct the asset.
            s.Synced.Remove(Sync);
            await SyncAsync(s);
        }
        finally { state.Gate.Release(); }
    }

    /// <summary>43404 favorites: OnHookMonsterNum slots, real recommended monster ids, exact duplicate/removal errors.</summary>
    public async Task CollectAsync(GameSession s, SetMonsterCollectionReq req)
    {
        if (s.WildAfk is not { } state) return;
        using var scope = await EnterStateScopeAsync(s, state, "collect");
        if (_loop.Owners != null && scope == null) return;
        await state.Gate.WaitAsync();
        try
        {
            if (!Owns(s, state)) return;
            var list = state.Info.MonsterCollection;
            bool exists = list.Contains(req.MonsterId);
            int error = !Unlocked(s) ? ErrorCodes.FeatureUnavailable
                : _data.Row("NPCConfig", req.MonsterId) is not { } npc || GameData.Int(npc, "_iWildMonsters") != 1 ? ErrorCodes.AfkCollectionInvalid
                : req.IsAdd && exists ? ErrorCodes.AfkCollectionExists
                : !req.IsAdd && !exists ? ErrorCodes.AfkCollectionMissing
                : req.IsAdd && list.Count >= Param("OnHookMonsterNum", 7) ? ErrorCodes.AfkCollectionFull : 0;
            if (error == 0)
            {
                if (req.IsAdd) list.Add(req.MonsterId); else list.Remove(req.MonsterId);
                Save(s);
            }
            await s.SendGameAsync(Collection, new SetMonsterCollectionRsp { Result = error, MonsterId = req.MonsterId, IsAdd = req.IsAdd });
            // Same Lua truthiness issue as the timer responses, including rejection of a duplicate/full collection.
            s.Synced.Remove(Sync);
            await SyncAsync(s);
        }
        finally { state.Gate.Release(); }
    }

    /// <summary>43411 rolls cumulative drop totals to the previous snapshot; it never grants items a second time.</summary>
    public async Task SnapshotAsync(GameSession s)
    {
        if (s.WildAfk is not { } state) return;
        using var scope = await EnterStateScopeAsync(s, state, "snapshot");
        if (_loop.Owners != null && scope == null) return;
        await state.Gate.WaitAsync();
        try
        {
            if (!Owns(s, state)) return;
            await ReconcileCoreAsync(s, Now);
            var info = state.Info;
            long now = Now / 1000;
            int error = !Unlocked(s) ? ErrorCodes.FeatureUnavailable
                : info.CurrentSnapshot is { Timestamp: > 0 } snap && now - snap.Timestamp < Param("OnHookAwardSnapshotCD", 5)
                    ? ErrorCodes.Cooldown : 0;
            if (error == 0)
            {
                info.PreviousSnapshot = info.CurrentSnapshot?.Clone() ?? new AFKDropSnapshot();
                info.CurrentSnapshot = new AFKDropSnapshot { Timestamp = now };
                foreach (var (entry, count) in state.TotalDrops) info.CurrentSnapshot.ItemDropCount[entry] = count;
                Save(s);
            }
            await s.SendGameAsync(Snapshot, new GenerateAFKDropSnapshotRsp { Result = error });
            await SyncAsync(s);
        }
        finally { state.Gate.Release(); }
    }

    /// <summary>Called before RewardService rolls: only an active unlocked field kill selects the
    /// configured Bonus Time environment (localization 10960000077; environment mapping inferred).</summary>
    private async Task<bool> BeforeKillAsync(GameSession s, MonsterUnit monster)
    {
        if (!Unlocked(s) || !Field(s) || !Ordinary(monster) || s.WildAfk is not { } state) return false;
        if (BeforeKillGateForTest is { } beforeGate) await beforeGate();
        // A kill waiting behind an old state's gate must not reserve that role and block
        // its wire role-switch. This first wait only observes readiness; ReconcileAsync
        // acquires the role reservation before its separate mutating gate section.
        var gateWait = state.Gate.WaitAsync();
        KillGateWaitingForTest?.Invoke();
        await gateWait;
        state.Gate.Release();
        if (!Owns(s, state)) return false;
        await ReconcileAsync(s);
        if (!Owns(s, state)) return false; // The old role's kill is obsolete; RewardService also aborts its grant.
        return s.AutoBattle && !s.Dead && !s.SceneLoading && !MvpAggro(s)
            && state.Info.IsHighMagnification && state.Info.HighMultiplierTimeSeconds > 0;
    }

    internal Func<Task>? BeforeKillGateForTest { get; set; }
    internal Action? KillGateWaitingForTest { get; set; }

    /// <summary>Freeze the granted field-kill record under the killer's role reservation and write it
    /// with the parent E04 reward. The already reconciled clock state stays intact, including fractions.</summary>
    private void StageAfkKill(GameSession s, MonsterUnit monster, RewardSettlement settlement,
        IReadOnlyList<(int Entry, int Count)> items, long baseExp, long jobExp)
    {
        settlement.ResetAfkStage?.Invoke();
        if (s.WildAfk is not { } state || !Unlocked(s) || !Field(s) || !Ordinary(monster)) return;
        var context = s.Owners?.Current ?? throw new InvalidOperationException("AFK kill record requires its role owner.");
        context.AssertRole(s.Role!.Id);
        context.Pin(s.Owner!);
        long at = Now;
        var next = JsonSerializer.Deserialize<State>(JsonSerializer.SerializeToUtf8Bytes(state))!;
        long day = Math.Min(state.DailyEpoch, Epoch(at));
        if (day <= 0) throw new InvalidOperationException("AFK kill has no reconciled day.");
        if (day == state.DailyEpoch) next.Info.TodayKillCount++;
        if (!next.Info.DailyDropRecords.TryGetValue(day, out var record))
            next.Info.DailyDropRecords[day] = record = new DailyAFKDropRecord();
        foreach (long key in next.Info.DailyDropRecords.Keys.OrderDescending()
            .Skip((int)Math.Max(1, Param("OnHookRecordLimit", 8))).ToArray())
            next.Info.DailyDropRecords.Remove(key);
        record.MonsterKillCount[monster.Template.NpcId] = record.MonsterKillCount.GetValueOrDefault(monster.Template.NpcId) + 1;
        next.Info.CurrentSnapshot ??= new AFKDropSnapshot { Timestamp = at / 1000 };
        next.Info.PreviousSnapshot ??= new AFKDropSnapshot { Timestamp = at / 1000 };
        var drops = items.Select(x => (x.Entry, Count: (long)x.Count))
            .Concat(baseExp > 0 ? [(100, baseExp)] : [])
            .Concat(jobExp > 0 ? [(101, jobExp)] : []).Where(x => x.Count > 0).ToArray();
        foreach (var (entry, count) in drops)
        {
            record.ItemDropCount[entry] = checked(record.ItemDropCount.GetValueOrDefault(entry) + count);
            next.TotalDrops[entry] = checked(next.TotalDrops.GetValueOrDefault(entry) + count);
        }
        bool renewMonitor = drops.Length > 0 && s.AutoBattle && !MvpAggro(s);
        byte[] before = JsonSerializer.SerializeToUtf8Bytes(state);
        byte[] after = JsonSerializer.SerializeToUtf8Bytes(next);
        var priorEnlist = settlement.Enlist;
        var priorApplied = settlement.Applied;
        settlement.Enlist = builder =>
        {
            priorEnlist?.Invoke(builder);
            byte[]? stored = _roles.ReadWildAfk(builder, s.Role.Id);
            if (stored == null || !stored.AsSpan().SequenceEqual(before))
                throw new InvalidOperationException("AFK kill source changed before parent admission.");
            builder.RoleBlob(s.Role.Id, "wild_afk", after);
        };
        settlement.Applied = () =>
        {
            priorApplied?.Invoke();
            if (!Owns(s, state)) return;
            state.Info = next.Info;
            state.TotalDrops = next.TotalDrops;
            state.Persisted = after;
            if (renewMonitor) state.LastKillAtMs = at;
        };
        settlement.AfkRecordStaged = true;
        settlement.ResetAfkStage = () =>
        {
            settlement.Enlist = priorEnlist;
            settlement.Applied = priorApplied;
            settlement.AfkRecordStaged = false;
            settlement.ResetAfkStage = null;
        };
    }

    /// <summary>Record only RewardService's actual granted self drops (including corrected EXP), never inventory grants/quest awards.</summary>
    private async Task DropsGrantedAsync(GameSession s, MonsterUnit monster, IReadOnlyList<(int Entry, long Count)> drops)
    {
        if (monster.RewardSettlement?.AfkRecordStaged == true)
        {
            if (s.WildAfk != null && Owns(s, s.WildAfk)) await ReconcileAsync(s);
            return;
        }
        if (s.WildAfk is not { } state || !Unlocked(s) || !Field(s) || !Ordinary(monster)) return;
        await state.Gate.WaitAsync();
        state.Gate.Release();
        if (!Owns(s, state)) return;
        using var scope = await EnterStateScopeAsync(s, state, "kill-drops");
        if (_loop.Owners != null && scope == null) return;
        await state.Gate.WaitAsync();
        try
        {
            if (!Owns(s, state)) return;
            long now = Now;
            await ReconcileCoreAsync(s, now);
            var info = state.Info;
            info.TodayKillCount++;
            long day = state.DailyEpoch;
            if (!info.DailyDropRecords.TryGetValue(day, out var record)) info.DailyDropRecords[day] = record = new DailyAFKDropRecord();
            foreach (long key in info.DailyDropRecords.Keys.OrderDescending().Skip((int)Math.Max(1, Param("OnHookRecordLimit", 8))).ToArray())
                info.DailyDropRecords.Remove(key);
            record.MonsterKillCount[monster.Template.NpcId] = record.MonsterKillCount.GetValueOrDefault(monster.Template.NpcId) + 1;
            // Snapshot baseline is created before the first drop. Current totals grow; 43411 captures the next baseline.
            info.CurrentSnapshot ??= new AFKDropSnapshot { Timestamp = now / 1000 };
            info.PreviousSnapshot ??= new AFKDropSnapshot { Timestamp = now / 1000 };
            foreach (var (entry, count) in drops.Where(d => d.Count > 0))
            {
                record.ItemDropCount[entry] = record.ItemDropCount.GetValueOrDefault(entry) + count;
                state.TotalDrops[entry] = state.TotalDrops.GetValueOrDefault(entry) + count;
            }
            if (drops.Any(d => d.Count > 0) && s.AutoBattle && !MvpAggro(s)) state.LastKillAtMs = now;
            await ReconcileCoreAsync(s, now);
        }
        finally { state.Gate.Release(); }
    }

    private readonly HashSet<State> _pendingReconciles = [];
    internal Action<GameSession>? TickAttemptCompletedForTest { get; set; }
    public void Tick(GameTime time)
    {
        // Still every session once a second (accrual and suspension follow the clock), each in its own tick.
        if (_phases.Due(time.Now) is not { } due) return;
        foreach (var s in _sessions.All().Where(s => s.Role != null && s.WildAfk != null && due[SecondPhases.PhaseOf(s)]).ToArray())
        {
            var state = s.WildAfk!;
            if (!_pendingReconciles.Add(state)) continue;
            var role = s.Role;
            var attachment = s.Attachment;
            var owner = s.Owner;
            bool CurrentTick() => ReferenceEquals(s.Role, role) && ReferenceEquals(s.WildAfk, state) &&
                ReferenceEquals(s.Attachment, attachment) &&
                (_loop.Owners?.IsCurrentAttachedTimer(s, owner, attachment) ?? true);
            _loop.PostRuntime(async () =>
            {
                try
                {
                    if (CurrentTick()) await ReconcileAsync(s, CurrentTick);
                }
                finally { _pendingReconciles.Remove(state); TickAttemptCompletedForTest?.Invoke(s); }
            });
        }
    }

    private readonly SecondPhases _phases = new();
}
