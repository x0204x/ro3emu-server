using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Ilmata (DailyActivity 125), LC_DAL_Whale / LC_WhaleHelper and MG_Guild 83300-83302.
/// Guild state is durable; scene/combat lifetime belongs to DungeonService/InstanceService. Stage grants
/// and auction publication recover from a committed clear journal, never from a client success claim.
/// </summary>
public sealed class GuildChallengeService : IGameTicker
{
    public const uint Notify = 83300, Info = 83301, Open = 83302;
    public const int Activity = 125;
    private readonly GameData data;
    private readonly GuildStore guilds;
    private readonly GuildChallengeStore store;
    private readonly RoleStore roles;
    private readonly CharacterService character;
    private readonly FunctionService functions;
    private readonly SeasonService seasons;
    private readonly TimedTodoSchedule schedule;
    private readonly DailyActivityService daily;
    private readonly DungeonCatalog catalog;
    private readonly DungeonService dungeons;
    private readonly InstanceService instances;
    private readonly RewardService rewards;
    private readonly AuctionService auctions;
    private readonly AuctionStore auctionStore;
    private readonly SessionRegistry sessions;
    private readonly TimeProvider clock;
    private readonly GameLoop loop;
    private readonly ILogger<GuildChallengeService> log;
    private readonly SemaphoreSlim gate = new(1, 1);

    private ReservationKey[] Keys(long guild, long role)
    {
        string id = guild.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return [ReservationKey.Entity("guild", id), ReservationKey.Entity("guild_challenge", id),
            .. (guilds.Get(guild)?.Members.Keys ?? Enumerable.Empty<long>()).Append(role)
                .Where(value => value > 0).Distinct().Select(value => ReservationKey.Role(value))];
    }

    /// <summary>C/D 15: the login's cycle rollover writes its guild's challenge state; the role select reserves the
    /// same keys as the challenge gate up front.</summary>
    internal ReservationKey[] LoginKeys(long roleId) => guilds.ForRole(roleId) is { } g ? Keys(g.Id, roleId) : [];

    private async Task<LegacyOwnerScope> EnterWithGateAsync(string origin, long guild, long role)
    {
        var owners = loop.Owners ?? throw new InvalidOperationException("Challenge owner registry is unavailable.");
        while (true)
        {
            var keys = Keys(guild, role);
            var scope = await owners.EnterWithGateAsync(origin, gate, keys);
            if (keys.ToHashSet().SetEquals(Keys(guild, role))) return scope;
            gate.Release();
            scope.Dispose();
        }
    }
    private readonly Dictionary<long, DungeonService.Attempt> runs = new();
    private long lastSweep;
    private long? testCycle;
    private bool sweeping;
    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();

    public GuildChallengeService(GameData data, GuildStore guilds, GuildChallengeStore store, RoleStore roles,
        CharacterService character, FunctionService functions, SeasonService seasons, TimedTodoSchedule schedule,
        DailyActivityService daily, DungeonCatalog catalog, DungeonService dungeons, InstanceService instances,
        RewardService rewards, AuctionService auctions, AuctionStore auctionStore, SessionRegistry sessions,
        TimeProvider clock, GameLoop loop, ILogger<GuildChallengeService> log)
    {
        this.data = data; this.guilds = guilds; this.store = store; this.roles = roles; this.character = character;
        this.functions = functions; this.seasons = seasons; this.schedule = schedule; this.daily = daily;
        this.catalog = catalog; this.dungeons = dungeons; this.instances = instances; this.rewards = rewards;
        this.auctions = auctions; this.auctionStore = auctionStore; this.sessions = sessions;
        this.clock = clock; this.loop = loop; this.log = log;
        dungeons.GuildChallengeEnter = TryEnterAsync;
    }

    public sealed class GuildState
    {
        public int Season { get; set; }
        public long Cycle { get; set; }
        public bool Activated { get; set; }
        public int Unlocked { get; set; } = 1;
        public int LastUnlocked { get; set; } = 1;
        public int PassedDifficulty { get; set; }
        public int Difficulty { get; set; }
        public int Stage { get; set; }
        public bool Selected { get; set; }
        public bool Complete { get; set; }
        public Dictionary<int, int> Progress { get; set; } = new();
        public Dictionary<int, int> BestTimeMs { get; set; } = new();
        public List<int> AuctionEntries { get; set; } = new();
    }

    public sealed class PersonalState
    {
        // (inferred) Once per ordinal stage across difficulties, matching Whale's merged GotStageRewardInfo.
        // Keys include the season and schedule occurrence, not guild membership; switching guild cannot reset a claim.
        public Dictionary<string, int> Granted { get; set; } = new();
    }

    public sealed record Good(int Entry, int Count, int Bind);
    public sealed class Clear
    {
        public string Id { get; set; } = "";
        public long Guild { get; set; }
        public long Cycle { get; set; }
        public int Season { get; set; }
        public int PlayBook { get; set; }
        public int OpenDay { get; set; }
        public int Challenge { get; set; }
        public int Difficulty { get; set; }
        public int Stage { get; set; }
        public int StageTimeMs { get; set; }
        public int PassTimeMs { get; set; }
        public bool NewRecord { get; set; }
        public Dictionary<long, List<Good>> Personal { get; set; } = new();
        public HashSet<long> Delivered { get; set; } = new();
        public bool AuctionPublished { get; set; }
        public List<int> AuctionEntries { get; set; } = new();
    }

    internal sealed class Run
    {
        public required GuildChallengeService Owner { get; init; }
        public required long Guild { get; init; }
        public required long Cycle { get; init; }
        public required int Challenge { get; init; }
        public required int Difficulty { get; init; }
        public required int Stage { get; init; }
        public required long End { get; init; }
        public IEnumerable<DungeonVar> Vars() => [new() { VarId = 901, Vi64 = { Challenge } },
            new() { VarId = 902, Vi64 = { Stage } }, new() { VarId = 903, Vi64 = { End } }];
    }

    private IEnumerable<JsonElement> Configs => data.Table("GuildChallengeConfig").Values
        .Where(r => TowerService.FitsPlayBook(r, seasons.PlayBook)).OrderBy(r => GameData.Int(r, "_iLevel"));
    private JsonElement? Config(int difficulty) => Configs.Where(r => GameData.Int(r, "_iLevel") == difficulty)
        .Select(r => (JsonElement?)r).FirstOrDefault();
    private static int[] Values(JsonElement row, string field) => row.TryGetProperty(field, out var a) && a.ValueKind == JsonValueKind.Array
        ? a.EnumerateArray().Select(n => (int)(GameData.Number(n) ?? 0)).ToArray() : [];
    private static string Claim(int season, long cycle, int stage) => $"{season}:{cycle}:{stage}";
    private static string CompletionKey(Clear clear) => $"{clear.Guild}:{clear.Season}:{clear.Cycle}:{clear.Difficulty}:{clear.Stage}";
    private EconomyAsset Asset(int entry)
    {
        var row = data.Row("ItemConfig", entry) ?? throw new InvalidOperationException($"Missing challenge reward item {entry}.");
        return new(GameData.Int(row, "_iType") == 1 ? EconomyAssetKind.Currency : EconomyAssetKind.Item, entry);
    }
    private static EconomyOwner RoleOwner(long role) => new(EconomyOwnerKind.Role,
        role.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static EconomyCompartment Held(EconomyAsset asset) => asset.Kind == EconomyAssetKind.Currency
        ? EconomyCompartment.Currency : EconomyCompartment.Inventory;
    private EconomyEnvelope Envelope(long actorRole, string kind, string source, IReadOnlyList<EconomyEffect> effects)
    {
        var actor = roles.Get(actorRole) ?? throw new InvalidOperationException("Guild challenge actor is missing.");
        return new EconomyEnvelope(Guid.NewGuid().ToString("N"), actor.Account, actorRole, kind,
            source, source, clock.GetUtcNow().ToUnixTimeMilliseconds(), effects, []);
    }
    private List<EconomyEffect> ClearEffects(Clear clear)
    {
        var effects = new List<EconomyEffect>();
        foreach (var (role, goods) in clear.Personal)
            foreach (var group in goods.Where(g => g.Entry is not (100 or 101)).GroupBy(g => g.Entry))
                effects.Add(new(Asset(group.Key), RoleOwner(role), EconomyCompartment.Pending,
                    group.Sum(g => (long)g.Count), EconomyLegKind.Source, EconomyReason.ActivityReward));
        return effects;
    }
    private List<EconomyEffect> DeliveryEffects(long role, IReadOnlyList<Good> goods, bool claimed)
    {
        var effects = new List<EconomyEffect>();
        int edge = 0;
        foreach (var group in goods.Where(g => g.Entry is not (100 or 101)).GroupBy(g => g.Entry))
        {
            var asset = Asset(group.Key);
            long count = group.Sum(g => (long)g.Count);
            if (claimed)
                effects.Add(new(asset, RoleOwner(role), EconomyCompartment.Pending,
                    -count, EconomyLegKind.Sink, EconomyReason.ExplicitDestruction));
            else
            {
                effects.Add(new(asset, RoleOwner(role), EconomyCompartment.Pending,
                    -count, EconomyLegKind.Transfer, EconomyReason.ChallengePendingDelivery, ++edge));
                effects.Add(new(asset, RoleOwner(role), Held(asset),
                    count, EconomyLegKind.Transfer, EconomyReason.ChallengePendingDelivery, edge));
            }
        }
        return effects;
    }
    private GuildStore.State? Guild(GameSession s) => s.Role == null ? null : guilds.ForRole(s.Role.Id);
    private bool IsManager(GameSession s, GuildStore.State g) => g.Members.TryGetValue(s.Role!.Id, out var m)
        && data.Table("PublicParamConfig").TryGetValue("GuildChallengeSet", out var ranks)
        && ranks.ValueKind == JsonValueKind.Array && ranks.EnumerateArray().Any(v => GameData.Number(v) == m.Official);
    private bool Available(GameSession s) => s.Progress != null && s.ActivitiesReady
        && s.SeasonState?.SeasonId == seasons.SeasonId && s.SeasonState.PlayBookId == seasons.PlayBook
        && data.Row("DailyActivityConfig", Activity) is { } row && s.Progress.Level >= GameData.Int(row, "_iiLv")
        && functions.IsOpen(s, GameData.Int(row, "_iFunction")) && Configs.Any();
    private DungeonService.Attempt? Live(long guild) => runs.GetValueOrDefault(guild) is { Settled: false, Room.Closed: false } a ? a : null;

    private int Unlocked(GuildState state)
    {
        int result = 0;
        long days = seasons.OpenContent.ServerDayFree ? long.MaxValue : (long)Math.Floor((Now - seasons.StartTime) / 86400d) + 1;
        foreach (var config in Configs)
        {
            if (data.Row("LC_ConditionList", GameData.Int(config, "_kCondition")) is not { } condition) continue;
            bool ok = true;
            foreach (var sub in condition.GetProperty("m_kSubConditionList").EnumerateArray())
            {
                var tuple = sub.GetProperty("m_kData");
                long type = (long)(GameData.Number(tuple[0]) ?? 0), value = (long)(GameData.Number(tuple[1]) ?? 0);
                // LC_InteractCondition.CheckServerDay / CheckGuildChallengeLevel. A prior full clear unlocks
                // the next occurrence (inferred from DBGuildChallengeInfo.nextTimeMaxDiffiulty).
                ok &= type == 3031 ? days >= value : type == 6510 && data.Row("GuildChallengeConfig", value) is { } previous
                    && state.PassedDifficulty >= GameData.Int(previous, "_iLevel");
            }
            if (ok) result = Math.Max(result, (int)GameData.Int(config, "_iLevel"));
        }
        return result;
    }

    private GuildState State(long guild, out bool changed)
    {
        changed = false;
        var state = store.Load(guild);
        if (state.Season != seasons.SeasonId)
        { state = new GuildState { Season = seasons.SeasonId }; changed = true; }
        var window = schedule.CurrentOrNext(Activity);
        long cycle = testCycle ?? window?.Start ?? 0;
        if (state.Cycle == 0 || cycle != state.Cycle && (window?.Contains(Now) == true || state.Cycle < 0 && cycle > 0))
        {
            state.Cycle = cycle; state.Activated = window?.Contains(Now) == true;
            state.LastUnlocked = state.Unlocked; state.Unlocked = Unlocked(state);
            state.Difficulty = 0; state.Stage = 0; state.Selected = state.Complete = false;
            state.Progress.Clear(); state.AuctionEntries.Clear();
            changed = true;
        }
        else if (!state.Activated && window?.Contains(Now) == true)
        {
            state.Activated = true; state.Unlocked = Unlocked(state);
            changed = true;
        }
        return state;
    }
    private GuildState State(long guild) => State(guild, out _);
    private async Task SaveStateAsync(long guild, GuildState state, long actor, string kind)
    {
        var tx = roles.NewTransaction();
        store.EnlistState(guild, state, tx);
        string operation = Guid.NewGuid().ToString("N");
        tx.WithEconomy(Envelope(actor, kind, $"guild-challenge:state:{guild}:{operation}", []));
        await roles.CommitAsync(tx, "guild-challenge/state");
    }
    private async Task<GuildState> EnsureStateAsync(long guild, long actor)
    {
        var state = State(guild, out bool changed);
        if (changed) await SaveStateAsync(guild, state, actor, "guild-challenge-cycle");
        return state;
    }

    private PersonalState Personal(GameSession s) => s.GuildChallenge ??= roles.LoadGuildChallenge(s.Role!.Id) is { } bytes
        ? JsonSerializer.Deserialize<PersonalState>(bytes)! : new();

    internal GuildDungeonChallengeSync Snapshot(GameSession s)
    {
        var info = new GuildDungeonChallengeSync { DataInfo = new GuildChallenge(), RecommendDifficulty = 1 };
        if (Guild(s) is not { } g) return info;
        var state = State(g.Id);
        var personal = Personal(s);
        info.DataInfo.CurTimeMaxDiffiulty = state.Unlocked;
        info.DataInfo.LastTimeMaxDiffiulty = state.LastUnlocked;
        info.DataInfo.AuctionRewards.Add(state.AuctionEntries);
        foreach (var c in Configs)
        {
            int difficulty = (int)GameData.Int(c, "_iLevel");
            var item = new ChallengeDifficultyInfo { Difficulty = difficulty };
            foreach (int stage in Values(c, "_kStageId"))
                // InstancesNewWidgetCtrl displays remaining reward count from IsStageGotReward.
                // The manager's skip-rewarded-stage prompt also reads these personal flags.
                item.StageReward[stage] = personal.Granted.GetValueOrDefault(Claim(state.Season, state.Cycle, stage)) == difficulty;
            info.DataInfo.DifficultyInfo[difficulty] = item;
        }
        info.CurDifficulty = state.Difficulty;
        info.CurStage = state.Stage;
        info.RecommendDifficulty = Math.Max(1, state.Unlocked);
        info.LockState = state.Complete || Live(g.Id)?.Started == true;
        if (state.Selected && schedule.IsOpen(Activity) && Config(state.Difficulty) is { } cfg)
        {
            var scenes = Values(cfg, "_kDungeonId");
            if (state.Stage > 0 && state.Stage <= scenes.Length)
                info.DungeonNtf = new DungeonCallMemberNtf
                {
                    Difficulty = state.Difficulty, SceneEntryID = scenes[state.Stage - 1],
                    Endtime = schedule.CurrentOrNext(Activity)!.End, UId = Live(g.Id)?.Room.Id ?? 0,
                    CallData = new SceneCallData
                    {
                        GuildChallengeData = new GuildChallengeCallData
                        { ConfigId = (int)GameData.Int(cfg, "_iGuildChallengeId") }
                    }
                };
        }
        return info;
    }

    public async Task LoginAsync(GameSession s)
    {
        s.GuildChallenge = null;
        Personal(s);
        // C/D 15: recovery of committed-but-undelivered clears (Realm, other members' bags) is realm maintenance.
        // The login requests it as its own operation, which runs right after the login releases its scope.
        RequestRecovery();
        if (Guild(s) is { } guild) await EnsureStateAsync(guild.Id, s.Role!.Id);
        await s.SendGameAsync(Notify, Snapshot(s));
    }

    public async Task InfoAsync(GameSession s)
    {
        var guild = Guild(s)?.Id ?? 0;
        var role = s.Role;
        var attachment = s.Attachment;
        using var scope = await EnterWithGateAsync("guild-challenge/info", guild, s.Role?.Id ?? 0);
        try
        {
            // A membership transfer can complete while admission waits. Never persist or project
            // a different guild under the captured guild's reservation.
            if (!ReferenceEquals(s.Role, role) || !ReferenceEquals(s.Attachment, attachment)
                || Guild(s)?.Id != guild)
            {
                await s.SendGameAsync(Info, new GuildDungeonChallengeResp { Result = ErrorCodes.GuildMissing });
                return;
            }
            if (Guild(s) is { } current) await EnsureStateAsync(current.Id, s.Role!.Id);
            await s.SendGameAsync(Info, new GuildDungeonChallengeResp
            { Result = Guild(s) == null ? ErrorCodes.GuildMissing : 0, Data = Snapshot(s) });
        }
        finally { gate.Release(); }
    }

    public async Task OpenAsync(GameSession s, GuildDungeonChallengeOpenReq req)
    {
        var guild = Guild(s)?.Id ?? 0;
        var role = s.Role;
        var attachment = s.Attachment;
        using var scope = await EnterWithGateAsync("guild-challenge/open", guild, s.Role?.Id ?? 0);
        try
        {
            if (!ReferenceEquals(s.Role, role) || !ReferenceEquals(s.Attachment, attachment)
                || Guild(s)?.Id != guild)
            {
                await s.SendGameAsync(Open, new GuildDungeonChallengeOpenResp
                { Result = ErrorCodes.GuildMissing, Difficulty = req.Difficulty, Stage = req.Stage });
                return;
            }
            var g = Guild(s); var c = Config(req.Difficulty); var state = g == null ? null : await EnsureStateAsync(g.Id, s.Role!.Id);
            int code = g == null ? ErrorCodes.GuildMissing : !Available(s) ? ErrorCodes.FeatureUnavailable
                : !IsManager(s, g) ? ErrorCodes.GuildPermission : !schedule.IsOpen(Activity) ? ErrorCodes.ActivityPeriod
                : c == null || !Values(c.Value, "_kStageId").Contains(req.Stage) ? ErrorCodes.Parameter
                : req.Difficulty > state!.Unlocked ? ErrorCodes.FeatureUnavailable
                : state.Complete || Live(g.Id) != null ? ErrorCodes.DungeonBusy
                : req.Stage > state.Progress.GetValueOrDefault(req.Difficulty) + 1 ? ErrorCodes.Parameter : 0;
            if (code == 0)
            {
                state!.Difficulty = req.Difficulty; state.Stage = req.Stage; state.Selected = true;
                await SaveStateAsync(g!.Id, state, s.Role!.Id, "guild-challenge-open");
                await BroadcastAsync(g!.Id);
            }
            await s.SendGameAsync(Open, new GuildDungeonChallengeOpenResp { Result = code, Difficulty = req.Difficulty, Stage = req.Stage });
        }
        finally { gate.Release(); }
    }

    internal async Task<bool> TryEnterAsync(GameSession s, EnterDungeonReq req)
    {
        if (!data.Table("GuildChallengeConfig").Values.Any(c => Values(c, "_kDungeonId").Contains((int)req.SceneEntry))) return false;
        long guild = Guild(s)?.Id ?? 0;
        var role = s.Role;
        var attachment = s.Attachment;
        using var scope = await EnterWithGateAsync("guild-challenge/enter", guild, role?.Id ?? 0);
        try
        {
            if (!ReferenceEquals(s.Role, role) || !ReferenceEquals(s.Attachment, attachment)
                || Guild(s)?.Id != guild)
            {
                await s.SendGameAsync(DungeonService.Enter, new EnterDungeonRsp { Result = ErrorCodes.GuildMissing });
                return true;
            }
            var g = Guild(s); var state = g == null ? null : await EnsureStateAsync(g.Id, s.Role!.Id); var c = state == null ? null : Config(state.Difficulty);
            var live = g == null ? null : Live(g.Id);
            // (inferred) Shared preparation accepts members; a running encounter does not accept late entries.
            bool continuation = s.DungeonAttempt is { Settled: true, GuildChallenge: not null } previous && previous.GuildChallenge.Guild == g?.Id;
            int code = g == null ? ErrorCodes.GuildMissing : !Available(s) ? ErrorCodes.FeatureUnavailable
                : !schedule.IsOpen(Activity) ? ErrorCodes.ActivityPeriod
                : req.InitData?.CalculateSize() > 0 || req.Param?.CalculateSize() > 0 ? ErrorCodes.Parameter
                : s.SceneLoading ? ErrorCodes.SwitchingScenes : s.Dead ? ErrorCodes.FeatureUnavailable
                : s.InstanceId != 0 && !continuation ? ErrorCodes.DungeonBusy
                : c == null || !state!.Selected || state.Complete ? ErrorCodes.FeatureUnavailable
                : live?.Started == true ? ErrorCodes.DungeonBusy
                : live?.Room.ActiveMembers.Count >= GameData.Int(c.Value, "_iLimitNum") ? ErrorCodes.DungeonTeamSize : 0;
            if (code == 0 && Values(c!.Value, "_kDungeonId")[state!.Stage - 1] != req.SceneEntry) code = ErrorCodes.Parameter;
            var plan = code == 0 ? catalog.GuildChallenge(req.SceneEntry, (int)GameData.Int(c!.Value, "_iLimitNum")) : null;
            if (code == 0 && plan == null) code = ErrorCodes.DungeonDataMissing;
            await s.SendGameAsync(DungeonService.Enter, new EnterDungeonRsp { Result = code });
            if (code != 0) return true;
            var run = live?.GuildChallenge ?? new Run
            {
                Owner = this, Guild = g!.Id, Cycle = state!.Cycle,
                Challenge = (int)GameData.Int(c!.Value, "_iGuildChallengeId"), Difficulty = state.Difficulty, Stage = state.Stage,
                End = schedule.CurrentOrNext(Activity)!.End
            };
            var attempt = await dungeons.EnterGuildChallengeAsync(s, plan!, run, live);
            if (attempt != null) runs[g!.Id] = attempt;
            await BroadcastAsync(g!.Id);
            return true;
        }
        finally { gate.Release(); }
    }

    internal async Task ClientEventAsync(GameSession s, ClientDungeonEvent req)
    {
        if (s.DungeonAttempt is not { GuildChallenge: { } run } a || s.InstanceId != a.Room.Id || s.SceneLoading
            || a.Room.Closed || a.Settled || Guild(s) is not { } guild || guild.Id != run.Guild || !IsManager(s, guild)) return;
        // Only original manager controls. Params/functions/rewards supplied by the client never settle a win.
        if (req.StrCmd == "DungeonGiveUp") await dungeons.StopGuildChallengeAsync(a);
        // (inferred) Every currently admitted member must finish loading before the manager starts combat.
        else if (req.StrCmd == "DungeonStart" && !a.Started && schedule.IsOpen(Activity) && Now < run.End
            && a.Room.ActiveMembers.All(m => !m.Session.SceneLoading && a.Loaded.Contains(m.Session)))
        {
            await dungeons.StartGuildChallengeAsync(a);
            await BroadcastAsync(guild.Id);
        }
    }

    /// <summary>A settled stage attempt, won or lost, for each guild member present (quest target 103014); listeners post.</summary>
    public List<Action<GameSession>> Participated { get; } = new();

    internal async Task SettleAsync(DungeonService.Attempt a, bool won)
    {
        using var ownerScope = loop.Owners is { } owners
            ? await owners.EnterAsync("guild challenge settlement", Ownership.ReservationKey.Realm) : null;
        await gate.WaitAsync();
        try
        {
            var run = a.GuildChallenge!;
            // (inferred) Present, loaded guild members earn the clear; no contribution threshold was recovered.
            var present = a.Room.ActiveMembers.Select(m => m.Session).Where(s => a.Loaded.Contains(s)
                && s.InstanceId == a.Room.Id && s.Role != null && Guild(s)?.Id == run.Guild).ToArray();
            var state = await EnsureStateAsync(run.Guild, a.Leader);
            bool success = won && present.Length > 0 && state.Cycle == run.Cycle && Now < run.End
                && schedule.IsOpen(Activity) && a.PlayBook == seasons.PlayBook && a.SeasonId == seasons.SeasonId;
            Clear? clear = null;
            if (success)
            {
                try
                {
                    // A replay can include members who missed an earlier clear. Personal captures belong
                    // to this room; auction publication below is deduplicated across rooms of the same stage.
                    string id = $"{run.Guild}:{a.SeasonId}:{run.Cycle}:{run.Difficulty}:{run.Stage}:{a.Room.Id}";
                    clear = store.LoadClear(id);
                    if (clear == null)
                    {
                        int elapsed = Math.Max(1, (int)(loop.Now - a.StartedAt).TotalMilliseconds);
                        // UI_Sys_GVE_VictoryNewPanelCtrl divides passDifficultTime by 1000. Summing stages is
                        // (inferred) from DBGuildChallengeInfo's stageTime plus passTime fields. Use each
                        // prior stage's best time (inferred) so reward replays do not add duplicate time.
                        int total = elapsed + store.Clears(run.Guild, a.SeasonId, run.Cycle, run.Difficulty)
                            .Where(c => c.Stage < run.Stage).GroupBy(c => c.Stage).Sum(g => g.Min(c => c.StageTimeMs));
                        clear = new Clear
                        {
                            Id = id, Guild = run.Guild, Cycle = run.Cycle, Season = a.SeasonId,
                            PlayBook = a.PlayBook, Challenge = run.Challenge, Difficulty = run.Difficulty, Stage = run.Stage,
                            OpenDay = Math.Max(1, (clock.GetLocalNow().Date - seasons.Start.Date).Days + 1),
                            StageTimeMs = elapsed, PassTimeMs = total
                        };
                        var stage = data.Table("GuildChallengeRewardConfig").Values.Single(r =>
                            GameData.Int(r, "_iGuildChallengeId") == run.Challenge && GameData.Int(r, "_iStage") == run.Stage);
                        var prior = store.Clears()
                            .Where(c => c.Season == a.SeasonId && c.Cycle == run.Cycle && c.Stage == run.Stage).ToArray();
                        foreach (var s in present)
                        {
                            long role = s.Role!.Id;
                            bool alreadyEarned = Personal(s).Granted.ContainsKey(Claim(a.SeasonId, run.Cycle, run.Stage))
                                || prior.Any(c => c.Personal.TryGetValue(role, out var priorGoods) && priorGoods.Count > 0);
                            clear.Personal[role] = alreadyEarned ? []
                                : Roll(GameData.Int(stage, "_kRewardStagePersonal"), s).Select(g => new Good(g.Entry, g.Count, g.Bind)).ToList();
                        }
                        state.Progress[run.Difficulty] = Math.Max(state.Progress.GetValueOrDefault(run.Difficulty), run.Stage);
                        int final = Values(Config(run.Difficulty)!.Value, "_kStageId").Max();
                        if (run.Stage == final)
                        {
                            state.Complete = true; state.PassedDifficulty = Math.Max(state.PassedDifficulty, run.Difficulty);
                            clear.NewRecord = !state.BestTimeMs.TryGetValue(run.Difficulty, out int old) || total < old;
                            if (clear.NewRecord) state.BestTimeMs[run.Difficulty] = total;
                        }
                        else state.Stage = run.Stage + 1;
                        var transaction = roles.NewTransaction();
                        store.EnlistState(run.Guild, state, transaction);
                        store.EnlistClear(clear, transaction);
                        transaction.WithEconomy(Envelope(present[0].Role!.Id, "guild-challenge-clear",
                            "guild-challenge:clear:" + clear.Id, ClearEffects(clear)));
                        await roles.CommitAsync(transaction, "guild-challenge/clear");
                    }
                    try { await DeliverAsync(clear); }
                    catch (Exception e) { log.LogError(e, "Committed Guild Challenge clear {Clear} delivery deferred", clear.Id); }
                }
                catch (Exception e)
                {
                    // A failed clear commit cannot qualify progress or rewards. The party can reopen and retry.
                    success = false; clear = null;
                    log.LogError(e, "Guild Challenge clear commit failed for guild {Guild}, room {Room}", run.Guild, a.Room.Id);
                }
            }
            foreach (var s in present)
            {
                s.Attack = null;
                var result = new DungeonResNotify
                {
                    DungeonEntry = a.Plan.SceneEntry, Res = (DungeonRes)(success ? 1 : 2),
                    Data = new DungeonResData
                    {
                        GuildChallengeData = new GuildChallengeResData
                        { PassDifficultTime = clear?.PassTimeMs ?? 0, IsNew = clear?.NewRecord ?? false }
                    }
                };
                if (clear != null)
                {
                    if (clear.Delivered.Contains(s.Role!.Id))
                        foreach (var g in clear.Personal.GetValueOrDefault(s.Role.Id, []).GroupBy(g => g.Entry)) result.Rewards[g.Key] = g.Sum(g => (long)g.Count);
                    result.AuctionRewards.Add(clear.AuctionEntries);
                }
                try { await s.SendGameAsync(DungeonService.Result, result); }
                catch (Exception e) { log.LogWarning(e, "Guild Challenge result delivery failed for {Role}", s.Role?.Id); }
                foreach (var handler in Participated)
                    try { handler(s); }
                    catch (Exception e) { log.LogError(e, "Guild Challenge participation hook failed for {Role}", s.Role?.Id); }
            }
            await BroadcastAsync(run.Guild);
        }
        finally { gate.Release(); }
    }

    private List<(int Entry, int Count, int Bind)> Roll(long group, GameSession s)
    {
        var mg = data.Row("MultiverseGroupConfig", group) ?? throw new InvalidOperationException($"Missing challenge reward group {group}");
        long award = GameData.Int(mg, $"_iValue_{seasons.PlayBook}");
        if (award == 0) award = GameData.Int(mg, "_iValue_default");
        return rewards.RollAuctionAwardBound(award, s.Progress!, Math.Max(1, (clock.GetLocalNow().Date - seasons.Start.Date).Days + 1));
    }

    private async Task DeliverAsync(Clear clear)
    {
        foreach (var (role, goods) in clear.Personal.Where(p => !clear.Delivered.Contains(p.Key)).ToArray())
        {
            var s = sessions.All().FirstOrDefault(s => s.Role?.Id == role && s.GuildChallenge != null);
            if (s == null) continue; // durable earned entitlement is delivered on next login, even after leaving the guild
            using (await CharacterService.Lock(s))
            {
                if (s.Role?.Id != role) continue;
                var next = new PersonalState { Granted = new(Personal(s).Granted) };
                string key = Claim(clear.Season, clear.Cycle, clear.Stage);
                bool claimed = next.Granted.ContainsKey(key);
                if (!claimed) next.Granted[key] = clear.Difficulty;
                var paid = clear.Delivered.ToHashSet(); paid.Add(role);
                var persisted = JsonSerializer.Deserialize<Clear>(JsonSerializer.SerializeToUtf8Bytes(clear))!;
                persisted.Delivered = paid;
                if (claimed) persisted.Personal[role] = []; // recovery/replay result reports only actual grants
                var granted = await character.ExchangeLockedAsync(s, [], [], popup: false, sync: false,
                    boundGoods: claimed ? [] : goods.Select(g => (g.Entry, g.Count, g.Bind)).ToList(), persist: tx =>
                    {
                        tx.RoleBlob(role, "guild_challenge", JsonSerializer.SerializeToUtf8Bytes(next));
                        tx.Add(new Ro3Emu.Server.Accounts.Persistence.GuildChallengeClearPlan(persisted));
                        tx.WithEconomy(Envelope(role, "guild-challenge-delivery",
                            $"guild-challenge:deliver:{clear.Id}:{role}", DeliveryEffects(role, goods, claimed)));
                    }, committed: () => { s.GuildChallenge = next; clear.Delivered = paid; clear.Personal = persisted.Personal; },
                    asyncCommit: true);
                if (granted == null) throw new InvalidOperationException("Guild Challenge personal award refused");
            }
            await character.ItemsChangedAsync(s, stats: false);
        }
        if (!clear.AuctionPublished)
        {
            string completion = CompletionKey(clear);
            int result = await auctions.PublishGuildChallengeAsync(completion, clear.Guild, clear.Challenge, clear.Stage,
                clear.Personal.Keys.ToArray(), committedParticipants: true, rewardPlayBook: clear.PlayBook, rewardOpenDay: clear.OpenDay);
            if (result != 0) { log.LogWarning("Guild Challenge auction {Clear} deferred: {Result}", clear.Id, result); return; }
            string prefix = $"guild:challenge:{clear.Guild}:{clear.Challenge}:{clear.Stage}:{completion}:";
            clear.AuctionEntries = auctionStore.Items().Where(i => i.SourceKey.StartsWith(prefix, StringComparison.Ordinal)).Select(i => i.Entry).ToList();
            clear.AuctionPublished = true;
            // One source per stage, preserving repeated entry IDs representing distinct auction lots.
            var occurrenceEntries = store.Clears(clear.Guild, clear.Season, clear.Cycle)
                .Where(c => c.Id != clear.Id).Append(clear).Where(c => c.AuctionPublished).GroupBy(CompletionKey)
                .SelectMany(g => g.First().AuctionEntries).ToList();
            var transaction = roles.NewTransaction();
            store.EnlistClear(clear, transaction);
            var state = store.Load(clear.Guild);
            if (state.Cycle == clear.Cycle && state.Season == clear.Season)
            {
                state.AuctionEntries = occurrenceEntries;
                store.EnlistState(clear.Guild, state, transaction);
            }
            long actor = clear.Personal.Keys.FirstOrDefault();
            transaction.WithEconomy(Envelope(actor, "guild-challenge-auction-published",
                "guild-challenge:auction:" + clear.Id, []));
            await roles.CommitAsync(transaction, "guild-challenge/auction-published");
        }
    }

    private async Task BroadcastAsync(long guild)
    {
        foreach (var s in sessions.All().Where(s => s.Role != null && Guild(s)?.Id == guild))
            try { await s.SendGameAsync(Notify, Snapshot(s)); }
            catch (Exception e) { log.LogWarning(e, "Guild Challenge sync failed for {Role}", s.Role?.Id); }
    }

    private bool recoveryRequested;
    /// <summary>Queue one recovery after the current operation; repeated requests before it runs coalesce.</summary>
    private void RequestRecovery()
    {
        if (recoveryRequested) return;
        recoveryRequested = true;
        loop.PostRuntime(async () => { recoveryRequested = false; await RecoverAsync(); }, "guild challenge recovery request");
    }

    internal async Task RecoverAsync()
    {
        // C/D 16: Realm only while a committed clear is still undelivered (rare); the check reads off the loop. Every
        // login requested this recovery, and as Realm it waited behind owned work for up to 16 s (S3).
        if ((await store.PendingAsync()).Count == 0) return;
        // Realm covers the clear journal, offline recipients and the still-shared auction publisher.
        using var ownerScope = loop.Owners is { } owners
            ? await owners.EnterAsync("guild challenge recovery", ReservationKey.Realm) : null;
        await gate.WaitAsync();
        try
        {
            foreach (var clear in store.Pending())
                try { await DeliverAsync(clear); }
                catch (Exception e) { log.LogError(e, "Guild Challenge clear {Clear} remains pending", clear.Id); }
        }
        finally { gate.Release(); }
    }

    /// <summary>Explicit GM test aid. Overrides only activity 125, in memory; never unlocks difficulty or grants rewards.</summary>
    public async Task TestWindowAsync(bool open)
    {
        if (open)
        {
            if (testCycle == null && schedule.IsOpen(Activity)) return; // the real occurrence is already open; never replace it
            // A disjoint persisted identity prevents a Friday test clear from consuming next Sunday's event.
            testCycle ??= -roles.AllocateInstanceId();
            schedule.TestWindows[Activity] = new(Now, Now + 90 * 60);
        }
        else
        {
            // Close the test occurrence first (including when the real event happens to be open now).
            schedule.TestWindows[Activity] = new(Now - 2, Now - 1);
            foreach (var a in runs.Values.Where(a => !a.Settled && !a.Room.Closed && a.GuildChallenge!.Cycle < 0).ToArray())
                await dungeons.StopGuildChallengeAsync(a);
            testCycle = null;
            schedule.TestWindows.Remove(Activity);
        }
        await RoleFanout.EachAsync(sessions.All().Where(s => s.Role != null && s.DailyReady), async s =>
        {
            await daily.SyncEventsAsync(s);
            if (Guild(s) is { } guild) await EnsureStateAsync(guild.Id, s.Role!.Id);
            await s.SendGameAsync(Notify, Snapshot(s));
        });
    }

    private bool TestWindowExpired => testCycle != null && schedule.TestWindows.TryGetValue(Activity, out var test) && Now >= test.End;
    private DungeonService.Attempt[] RunsToStop() => runs.Values.Where(a => !a.Settled && !a.Room.Closed
        && (Now >= a.GuildChallenge!.End || !schedule.IsOpen(Activity)
            || a.SeasonId != seasons.SeasonId || a.PlayBook != seasons.PlayBook)).ToArray();

    public void Tick(GameTime _)
    {
        if (sweeping || Now == lastSweep) return;
        lastSweep = Now;
        // C/D 16: a Realm operation only when a run must stop or the test window closed; the five-second recovery
        // takes Realm only with a pending clear. As one Realm operation every second it waited up to 16 s (S3).
        bool stop = TestWindowExpired || RunsToStop().Length != 0, recover = Now % 5 == 0;
        if (!stop && !recover) return;
        sweeping = true;
        loop.PostRuntime(async () =>
        {
            try
            {
                if (stop)
                {
                    using var scope = loop.Owners is { } owners
                        ? await owners.EnterAsync("guild challenge sweep", ReservationKey.Realm) : null;
                    if (TestWindowExpired) await TestWindowAsync(false);
                    foreach (var a in RunsToStop()) await dungeons.StopGuildChallengeAsync(a);
                }
                if (recover) await RecoverAsync();
            }
            finally { sweeping = false; }
        }, "guild challenge sweep");
    }
}
