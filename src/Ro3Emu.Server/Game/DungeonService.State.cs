using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    /// <summary>Durable 5062 progress, reward allowances and bounded participant-only result history.</summary>
    public sealed class State
    {
        public int PlayBook { get; set; }
        public long SeasonStart { get; set; }
        public long JobPosition { get; set; }
        public bool FirstSetted { get; set; }
        public Dictionary<string, Record> Records { get; set; } = new();
        public Dictionary<string, Allowance> Allowances { get; set; } = new();
        public Dictionary<long, byte[]> Statistics { get; set; } = new();
        public Dictionary<string, byte[]> Results { get; set; } = new();
        public Dictionary<string, JourneyVictory> PendingJourney { get; set; } = new();
        /// <summary>Final Trial honors per "multi:mark": TenGameplayFinalTrialConfig id -> DBMultiDungeonHonorState.</summary>
        public Dictionary<string, Dictionary<long, int>> Honors { get; set; } = new();
        /// <summary>Sacred Blessing level (TenGameplayBuffConfig id) last acknowledged per boss (82588 clears isNeedTip).</summary>
        public Dictionary<long, long> BlessingsSeen { get; set; } = new();
    }

    public sealed class Record
    {
        public bool Passed { get; set; }
        public long FastestMs { get; set; }
    }

    public sealed class Allowance
    {
        public long Epoch { get; set; }
        public int Remaining { get; set; }
    }

    private static State Copy(State state) => JsonSerializer.Deserialize<State>(JsonSerializer.SerializeToUtf8Bytes(state))!;
    private static string RecordKey(DungeonCatalog.Plan plan) => $"{plan.RewardMultiId}:{plan.RewardMark}";
    private async Task SaveAsync(GameSession s, State state)
    {
        var transaction = _roles.NewTransaction();
        transaction.RoleBlob(s.Role!.Id, "dungeons", JsonSerializer.SerializeToUtf8Bytes(state));
        await _roles.CommitAsync(transaction, "dungeon/state", _ => s.Dungeons = state);
    }

    private State CurrentState(State previous)
    {
        long start = _seasons.StartTime;
        // Key progress by configured season identity; preserve role-level job preference (inferred season policy).
        return previous.PlayBook == _seasons.PlayBook && previous.SeasonStart == start ? Copy(previous)
            : new State
            {
                PlayBook = _seasons.PlayBook, SeasonStart = start,
                JobPosition = previous.JobPosition, FirstSetted = previous.FirstSetted,
                PendingJourney = new(previous.PendingJourney),
                // Valhalla layers are one-time for good (no season reset in HeroicSpirit data).
                Records = previous.Records.Where(r => r.Key.StartsWith(ValhallaPrefix, StringComparison.Ordinal))
                    .ToDictionary(r => r.Key, r => r.Value)
            };
    }

    public async Task LoginAsync(GameSession s)
    {
        s.Dungeons = _roles.LoadDungeons(s.Role!.Id) is { } bytes ? JsonSerializer.Deserialize<State>(bytes) ?? new() : new();
        await DeliverPendingAsync(s);
        await SyncAsync(s);
    }

    /// <summary>
    /// Refresh type 1 daily / 2 weekly, [weekday,hour] for weekly, server-local time (inferred reset convention).
    /// These are reward allowances, NOT entry counters: the client's prepare panel permits rewardless assistance.
    /// Seed one configured increment; bank increments up to the configured maximum (inferred initialization/carry).
    /// </summary>
    internal static (long Epoch, int PeriodDays) RewardPeriod(JsonElement refresh, DateTime now)
    {
        long type = GameData.Int(refresh, "_iRewardNumRefreshType");
        var args = refresh.TryGetProperty("_iRewardNumRefreshParam", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Select(v => (int)(GameData.Number(v) ?? 0)).ToArray() : [];
        int hour = type == 2 && args.Length > 1 ? args[1] : args.Length > 0 ? args[0] : 5;
        var day = now.AddHours(-Math.Clamp(hour, 0, 23)).Date;
        if (type == 2)
        {
            int weekday = args.Length > 0 ? args[0] % 7 : 1;
            day = day.AddDays(-((7 + (int)day.DayOfWeek - weekday) % 7));
        }
        return (DateOnly.FromDateTime(day).DayNumber, type == 1 ? 1 : type == 2 ? 7 : 0);
    }

    private Allowance GetAllowance(State state, JsonElement multi, int mark)
    {
        string key = DungeonCatalog.AllowanceKey(multi, mark);
        var refresh = _catalog.Refresh(multi);
        if (refresh == null) return new(); // no recovered entitlement means no fabricated payout
        var r = refresh.Value;
        int max = (int)GameData.Int(r, "_iRewardMaxNum");
        int add = (int)GameData.Int(r, GameData.Int(multi, "_iIsRandomDungeon") != 0 ? "_iAddRandomRewardNum" : "_iAddRewardNum");
        var (epoch, days) = RewardPeriod(r, _clock.GetLocalNow().DateTime);
        if (!state.Allowances.TryGetValue(key, out var allowance))
            state.Allowances[key] = allowance = new Allowance { Epoch = epoch, Remaining = Math.Clamp(add, 0, max) };
        else if (days > 0 && epoch > allowance.Epoch)
        {
            allowance.Remaining = (int)Math.Min(max, allowance.Remaining + Math.Min(10000, (epoch - allowance.Epoch) / days) * add);
            allowance.Epoch = epoch;
        }
        return allowance;
    }

    private bool Meets(GameSession s, JsonElement row) => _functions.ConditionMet(s, GameData.Int(row, "_kUnlock"))
        && _functions.ConditionMet(s, GameData.Int(row, "_kPersonalUnlock"));

    /// <summary>
    /// GetBossRewardNum (LC_MultiDungeonHelper:183-258) lets Normal use Hard's extra balance, never the reverse.
    /// Own balance first, then Hard, paying the selected boss's personal award is (inferred consumption policy).
    /// </summary>
    private Allowance SpendableAllowance(State state, GameSession s, JsonElement multi, int mark)
    {
        var own = GetAllowance(state, multi, mark);
        if (own.Remaining > 0 || GameData.Int(multi, "_iDifficulty") != 1) return own;
        return _catalog.HardCounterpart(multi) is { } hard && Meets(s, hard)
            && _catalog.Boss(hard, mark) is { } boss && Meets(s, boss) ? GetAllowance(state, hard, mark) : own;
    }

    public async Task SyncAsync(GameSession s)
    {
        if (s.Role == null || s.Dungeons == null) return;
        using var stateLock = await CharacterService.Lock(s);
        await SyncLockedAsync(s);
    }

    /// <summary>ProgressHandlers already run under CharacterService.StateLock; never reacquire it in a callback.</summary>
    private async Task SyncLockedAsync(GameSession s)
    {
        if (s.Role == null || s.Dungeons == null) return;
        var state = CurrentState(s.Dungeons);
        // (inferred) A class change invalidates an old preference; keep 5062 and team cards consistent.
        var positions = PositionsFor(_data, s.Progress?.Career ?? s.Role.Career);
        if (state.JobPosition != 0 && !positions.Contains(state.JobPosition)) state.JobPosition = positions.FirstOrDefault();
        var info = new DBMultiDungeonInfo { JobPosition = state.JobPosition, FirstSetted = state.FirstSetted };
        foreach (var multi in _catalog.MultiRows.OrderBy(r => GameData.Int(r, "_iID")))
        {
            int id = (int)GameData.Int(multi, "_iID");
            if (!Meets(s, multi)) continue;
            var dungeon = new DBMultiDungeon { MultiDungeonId = id };
            for (int mark = 1; mark <= DungeonCatalog.Marks(multi); mark++)
            {
                if (_catalog.Boss(multi, mark) is not { } boss || !Meets(s, boss)) break;
                var allowance = GetAllowance(state, multi, mark);
                var record = state.Records.GetValueOrDefault($"{id}:{mark}");
                int maximum = _catalog.Refresh(multi) is { } refresh ? (int)GameData.Int(refresh, "_iRewardMaxNum") : 0;
                var award = new DBMultiDungeonAwardInfo
                {
                    TotalNumber = maximum, UseNumber = maximum - allowance.Remaining,
                    // Separate first clear from recurring counters: Lua adds a free first entitlement for NotPass.
                    // Automatic personal first-clear settlement (Finish=2) is inferred; no separate bonus is invented.
                    IsFirst = record?.Passed == true ? 2 : 0,
                    FirstPassState = (MultiDungeonFirstPassState)(record?.Passed == true ? 2 : 0),
                    FastRecord = record?.FastestMs ?? 0,
                };
                if (Blessing(boss) is { } blessing)
                {
                    award.SacredBlessings = blessing.Info;
                    award.SacredBlessings.IsNeedTip = blessing.Info.UnlockSacredBlessingID != 0
                        && state.BlessingsSeen.GetValueOrDefault(GameData.Int(boss, "_iBoss")) != blessing.Info.UnlockSacredBlessingID;
                }
                if (HonorView(state, $"{id}:{mark}", boss) is { } honor) award.Honor = honor;
                dungeon.AwardInfo.Add(award);
            }
            if (dungeon.AwardInfo.Count == 0) continue;
            info.DungeonInfo[id] = dungeon;
            info.MultiDungeonUnlockIndex[id] = dungeon.AwardInfo.Count;
        }
        if (!JsonSerializer.SerializeToUtf8Bytes(state).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(s.Dungeons)))
            await SaveAsync(s, state);
        await CharacterService.SyncAsync(s, Sync, info);
    }

    /// <summary>Heartbeat refresh keeps online menu unlocks and daily/weekly reward windows current.</summary>
    public async Task ReconcileAsync(GameSession s)
    {
        if (s.Dungeons == null) return;
        if (s.DungeonAttempt is { } attempt) await RefreshBondsAsync(attempt);
        long minute = _clock.GetLocalNow().Ticks / TimeSpan.TicksPerMinute;
        if (s.DungeonSyncMinute == minute) return;
        await SyncAsync(s);
        await _functions.SyncAsync(s); // 3031 day gates can open while the player remains online.
        s.DungeonSyncMinute = minute;
    }

    public async Task SetPositionAsync(GameSession s, MultiDungeonSetJobPositionReq req)
    {
        using var owner = s.Owners == null || s.Role == null ? null
            : await s.Owners.EnterAsync("dungeon/position", Ownership.ReservationKey.Role(s.Role!.Id));
        // (verified) UI_Sys_Skill_PositioninglWidgetCtrl:228-248 only offers this class's positions.
        int error = s.Dungeons == null || !PositionsFor(_data, s.Progress?.Career ?? 0).Contains(req.JobPosition) ? ErrorCodes.Parameter : 0;
        if (error == 0)
        {
            using (await CharacterService.Lock(s))
            {
                var state = CurrentState(s.Dungeons!);
                state.JobPosition = req.JobPosition;
                await SaveAsync(s, state);
            }
            await SyncAsync(s);
        }
        await s.SendGameAsync(SetJobPosition, new MultiDungeonSetJobPositionResp { Result = error });
    }

    public async Task SetGuideAsync(GameSession s, MultiDungeonSetJobPositionGuideFlagReq req)
    {
        using var owner = s.Owners == null || s.Role == null ? null
            : await s.Owners.EnterAsync("dungeon/guide", Ownership.ReservationKey.Role(s.Role!.Id));
        if (s.Dungeons != null)
        {
            using (await CharacterService.Lock(s))
            {
                var state = CurrentState(s.Dungeons);
                state.FirstSetted = req.GuideFlag;
                await SaveAsync(s, state);
            }
            await SyncAsync(s);
        }
        await s.SendGameAsync(SetGuide, new MultiDungeonSetJobPositionGuideFlagResp
        { Result = s.Dungeons == null ? ErrorCodes.Parameter : 0, GuideFlag = s.Dungeons?.FirstSetted ?? false });
    }

    /// <summary>TenGameplayBossConfig has no first-pass award field; never substitute personal/preview rewards.</summary>
    public Task ClaimFirstPassAsync(GameSession s, ClaimFirstPassAwardReq req) => s.SendGameAsync(ClaimFirstPass,
        new ClaimFirstPassAwardResp { Result = ErrorCodes.RewardMissing, MultiDungeonId = req.MultiDungeonId, MarkId = req.MarkId });

    private List<(int Entry, int Count, int Bind)>? Award(long group, GameSession s)
    {
        if (group == 0) return [];
        if (_data.Row("MultiverseGroupConfig", group) is not { } mg) return null;
        long id = GameData.Int(mg, $"_iValue_{_seasons.PlayBook}");
        if (id == 0) id = GameData.Int(mg, "_iValue_default");
        if (_data.Row("AwardConfig", id) is not { } raw) return null;
        var row = raw.ValueKind == JsonValueKind.Array && raw.GetArrayLength() > 0 ? raw[0] : raw;
        if (row.ValueKind != JsonValueKind.Object) return null;
        var goods = new List<(int Entry, int Count, int Bind)>();
        if (row.TryGetProperty("_kAward", out var fixedGoods) && fixedGoods.ValueKind == JsonValueKind.Array)
            foreach (var tuple in fixedGoods.EnumerateArray())
            {
                if (tuple.ValueKind != JsonValueKind.Array || tuple.GetArrayLength() < 2) return null;
                goods.Add(((int)(GameData.Number(tuple[0]) ?? 0), (int)(GameData.Number(tuple[1]) ?? 0),
                    tuple.GetArrayLength() > 2 ? (int)(GameData.Number(tuple[2]) ?? 0) : 0));
            }
        if (GameData.Int(row, "_iDropPool") is var pool and > 0 && _data.Row("DropPoolConfig", pool) == null) return null;
        goods.AddRange(_rewards.RollAwardBound(id, s));
        return goods.Any(g => g.Count <= 0 || _data.Row("ItemConfig", g.Entry) == null) ? null : goods;
    }
}
