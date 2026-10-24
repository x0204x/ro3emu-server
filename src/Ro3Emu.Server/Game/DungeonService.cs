using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Ordinary dungeons: 30400 selection, 42033/30406 preformed-team ready check,
/// 70010/30005 private scene, 30401/30404 state, authoritative kill settlement 30403/82584, 5062 and exit.
/// Encounter scripts absent from the export are explicitly bounded in DungeonCatalog; no client event grants wins
/// directly. A reconstructed scripted scene (DungeonService.Scripted) advances only on its configured client events
/// (an interaction on its own shown actor, a walk into its region, the end of the story it started).
/// </summary>
public sealed partial class DungeonService
{
    private const long EggThiefDungeon = 1021301;
    private const int EggThiefWaveSpeedCm = 550;
    private const float EggThiefWaveLeash = 100f;

    public const uint Sync = 5062, Enter = 30400, BaseInfo = 30401, Leave = 30402, Result = 30403, Event = 30404,
        Confirm = 30406, ClientEvent = 30106, CreateResult = 30107, EnterSceneResult = 30108,
        Reset = 30124, ReadyQueue = 42033, TeamInfo = 30441, ConfirmReminder = 42047, ConfirmReminderNotify = 42046,
        AwardNotify = 82584, ClaimFirstPass = 82587, SetJobPosition = 82590, SetGuide = 82592;
    public const uint ReviveInfo = 30112;
    /// <summary>84012 PlotActingTriggerReq: a story's end action 150 PlotActingTrigger, sent beside 84011.</summary>
    public const uint PlotSceneEvent = 84012;

    /// <summary>Committed clears only; consumers receive the scene entry and whether an actual personal award was paid.</summary>
    public List<Func<GameSession, int, bool, Task>> Completed { get; } = new();
    /// <summary>After <see cref="Completed"/> for a TenGameplay run: the boss (TenGameplayBossConfig._iBoss) and the
    /// run's settled participants (quest target 106602).</summary>
    public List<Func<GameSession, long, IReadOnlyCollection<GameSession>, Task>> MultiBossCleared { get; } = new();
    /// <summary>Guild Challenge owns admission and rewards; this engine retains scenes, combat and cleanup.</summary>
    internal Func<GameSession, EnterDungeonReq, Task<bool>>? GuildChallengeEnter { get; set; }
    /// <summary>Another private-room owner whose 30402 exit revives a dead member on return (the guild home).</summary>
    internal Func<long, bool>? RecoverDeadOnLeave { get; set; }
    /// <summary>A preformed-team ready check is waiting on this member.</summary>
    internal bool Preparing(GameSession s) => _pending.ContainsKey(s) || _rolling.Contains(s);

    private readonly GameData _data;
    private readonly DungeonCatalog _catalog;
    private readonly RoleStore _roles;
    private readonly Ownership.OwnerRegistry _owners;
    private readonly DungeonObligationStore _obligations;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly SeasonService _seasons;
    private readonly InstanceService _instances;
    private readonly TeamService _teams;
    private readonly SessionRegistry _sessions;
    private readonly RewardService _rewards;
    private readonly AuctionService _auctions;
    private readonly GuildStore _guilds;
    private readonly MonsterService _monsters;
    private readonly CombatService _combat;
    private readonly BuffService _buffs;
    private readonly SceneService _scenes;
    private readonly World _world;
    private readonly GameLoop _loop;
    private readonly TimeProvider _clock;
    private readonly ILogger<DungeonService> _log;
    private readonly AccountStore _accounts;
    private readonly AwardService _awards;
    private readonly ServerOptions _options;
    private readonly PraiseService _praise;
    private readonly Dictionary<GameSession, Preparation> _pending = new();

    public DungeonService(GameData data, DungeonCatalog catalog, RoleStore roles, Ownership.OwnerRegistry owners,
        DungeonObligationStore obligations, CharacterService character,
        FunctionService functions, SeasonService seasons, InstanceService instances, TeamService teams,
        SessionRegistry sessions, RewardService rewards, AuctionService auctions, GuildStore guilds, MonsterService monsters, CombatService combat,
        SceneService scene, BuffService buffs, GameLoop loop, TimeProvider clock, ILogger<DungeonService> log, World world,
        GmCommands gm, AccountStore accounts, AwardService awards, Microsoft.Extensions.Options.IOptions<ServerOptions> options,
        PraiseService praise)
    {
        this._accounts = accounts; this._awards = awards; this._options = options.Value; this._praise = praise;
        gm.Register("firstclear", "<bossId> [daysAgo] | <bossId> clear (Realm of the Gods test aid: the server's first clear "
            + "of a boss, which unlocks its hall of fame entry and Sacred Blessing)", FirstClearGmAsync);
        this._data = data; this._catalog = catalog; this._roles = roles; this._owners = owners;
        this._obligations = obligations; this._character = character;
        this._functions = functions; this._seasons = seasons; this._instances = instances; this._teams = teams;
        this._sessions = sessions; this._rewards = rewards; this._auctions = auctions; this._guilds = guilds;
        this._monsters = monsters; this._loop = loop;
        this._clock = clock; this._log = log;
        this._combat = combat;
        this._buffs = buffs;
        this._scenes = scene;
        this._world = world;
        gm.Register("actors", "[npcId | walk <x> <z> | use] (scripted scene test aid: the step's scene actors, jump beside one, arrive at x z as a walk would, or complete the step server-side)", ActorsGmAsync);
        teams.MultiDungeonInfo = TeamDungeonInfo;
        combat.DamageAdjusters.Add((s, m, _, _, damage) => BondDamage(s, m, damage, incoming: false));
        combat.IncomingDamageAdjusters.Add((m, s, _, damage) => Task.FromResult(BondDamage(s, m, damage, incoming: true)));
        scene.Loaded.Add(LoadedAsync);
        scene.Leaving.Add(AbandonAsync);
        combat.Killed.Add(KilledAsync);
        combat.BeforeKilled.Insert(0, FreezeTerminalKillAsync);
        combat.PlayerDied.Add(PlayerDiedAsync);
        combat.ReviveInfoOwners.Add(s => Active(s) != null); // PlayerDiedAsync publishes the run's own 30112
        combat.Dealt.Add((s, m, n) => { if (Active(s) is { } a && m.InstanceId == a.Room.Id) a.Stats[s.Role!.Id].TotalDamage += n; });
        combat.Taken.Add((m, s, n, dead) => { if (Active(s) is { } a && m.InstanceId == a.Room.Id) a.Stats[s.Role!.Id].TotalBeDamage += n; });
        combat.Healed.Add((s, n) => { if (Active(s) is { } a) a.Stats[s.Role!.Id].TotalCure += n; });
        character.ProgressHandlers.Add(SyncLockedAsync); // CharacterService invokes these while holding StateLock.
    }

    public sealed class Attempt
    {
        public required InstanceService.Room Room { get; init; }
        public required DungeonCatalog.Plan Plan { get; init; }
        public required long Leader { get; init; }
        public required long TeamId { get; init; }
        public required int PlayBook { get; init; }
        public required int SeasonId { get; init; }
        public bool Matched { get; init; }
        public HashSet<GameSession> Loaded { get; } = new();
        /// <summary>30441 roster slots by role (index = 0-based position), fixed on the first roster.</summary>
        public List<long> Slots { get; } = new();
        public HashSet<long> Killed { get; } = new();
        /// <summary>The completing events a step with "all" has seen so far.</summary>
        public HashSet<(ReconstructedDungeons.TriggerKind Kind, long Id)> StepSeen { get; } = [];
        /// <summary>A scripted step's strikes taken so far, by monster uid.</summary>
        public Dictionary<long, int> Struck { get; } = [];
        /// <summary>A step's own timers' generation: each step (and each new hide-and-seek game) starts a new one, so a
        /// stale timer does nothing.</summary>
        public int StepGeneration { get; set; }
        /// <summary>A hide-and-seek step: the round being played (1-based) and the hiding places already searched in
        /// this game.</summary>
        public int SeekRound { get; set; }
        public HashSet<long> Searched { get; } = [];
        public HashSet<long> Precounted { get; } = new();
        public bool ObligationsFrozen { get; set; }
        public bool ObligationsActivated { get; set; }
        public bool FrozenWin { get; set; }
        public MonsterUnit? TerminalMonster { get; set; }
        public long TerminalKillerRole { get; set; }
        public Ownership.AttachmentLease? TerminalKillerAttachment { get; set; }
        public string? TerminalKillSource { get; set; }
        public RewardSettlement? ActivationSettlement { get; set; }
        internal SemaphoreSlim FreezeGate { get; } = new(1, 1);
        public Dictionary<long, RoleStatisticsInfo> Stats { get; } = new();
        public Dictionary<GameSession, Dictionary<int, long>> StartingItems { get; } = new();
        public HashSet<GameSession> Paid { get; } = new();
        public Dictionary<long, int> RevivesUsed { get; } = new();
        public Dictionary<long, long> AutoReviveAt { get; } = new();
        public HashSet<GameSession> Reviving { get; } = new();
        public bool Started { get; set; }
        public bool Settled { get; set; }
        public int Wave { get; set; }
        public int Spawned { get; set; }
        /// <summary>Spawned before the current wave (its own kill count is Killed.Count minus this).</summary>
        public int WaveBase { get; set; }
        public long StartMs { get; set; }
        public TimeSpan StartedAt { get; set; }
        public bool Advancing { get; set; }
        public bool Entering { get; set; } = true;
        public bool Spawning { get; set; }
        /// <summary>The current scripted step got one of its completing client events (DungeonService.Scripted).</summary>
        public bool StepDone { get; set; }
        /// <summary>A Realm of the Gods raid waits in its preparation phase (DungeonService.RealmOfGods).</summary>
        public bool Preparing { get; set; }
        /// <summary>The boss's Sacred Blessing level (TenGameplayBuffConfig row) active for this run, fixed at its start.</summary>
        public JsonElement? Blessing { get; set; }
        /// <summary>The scripted scene actors shown now (also tracked by the room for cleanup).</summary>
        internal List<SceneActorUnit> SceneActors { get; } = new();
        internal GuildChallengeService.Run? GuildChallenge { get; set; }
        /// <summary>Dracula's Lair: DraculaWeddingService owns admission, the bride, the key and the settlement.</summary>
        internal DraculaWeddingService.Run? Dracula { get; set; }
    }

    private sealed class Preparation
    {
        public required DungeonCatalog.Plan Plan;
        public required GameSession Leader;
        public required GameSession[] Members;
        public required long TeamId;
        public readonly HashSet<GameSession> Ready = new();
        public bool Closed;
    }

    private static Attempt? Active(GameSession s) => s.DungeonAttempt is { Started: true, Settled: false } a
        && !a.Room.Closed && s.InstanceId == a.Room.Id && !s.SceneLoading ? a : null;
    private static GameSession[] Present(Attempt a) => a.Room.ActiveMembers.Select(m => m.Session)
        .Where(s => ReferenceEquals(s.DungeonAttempt, a) && s.InstanceId == a.Room.Id && s.Role != null).ToArray();
    private static Dictionary<int, long> Inventory(GameSession s)
    {
        var counts = s.Items.GroupBy(i => i.Entry).ToDictionary(g => g.Key, g => g.Sum(i => (long)i.Stack));
        foreach (var (id, n) in s.Progress!.Currency) counts[id] = n;
        return counts;
    }

    private int Admission(GameSession s, DungeonCatalog.Plan plan, bool continuation = false)
    {
        if (s.Role == null || s.Dungeons == null || s.Progress == null) return ErrorCodes.Parameter;
        if (s.SeasonState is not { } state || state.SeasonId != _seasons.SeasonId || state.PlayBookId != _seasons.PlayBook)
            return ErrorCodes.FeatureUnavailable;
        if (s.SceneLoading) return ErrorCodes.SwitchingScenes;
        if (s.Dead) return ErrorCodes.FeatureUnavailable;
        if (s.InstanceId != 0 && !(continuation && s.DungeonAttempt is { Settled: true })) return ErrorCodes.DungeonBusy;
        if (!_functions.ConditionMet(s, GameData.Int(plan.Config, "_iDungoenLockCondition"))
            || !_functions.ConditionMet(s, GameData.Int(plan.Config, "_iDungeonConditionConfig"))) return ErrorCodes.FeatureUnavailable;
        if (GameData.Int(plan.Config, "_iGamePlayType") == 24 && !JourneyCanEnter(s, plan.SceneEntry))
            return ErrorCodes.TaskRequirements;
        if (plan.Multi is { } multi)
        {
            int function = GameData.Int(multi, "_iDungeonType") == 10 ? 61 : 62;
            if (!TowerService.FitsPlayBook(multi, _seasons.PlayBook) || !_functions.IsOpen(s, function)
                || !Meets(s, multi) || plan.Boss is not { } boss || !Meets(s, boss))
                return ErrorCodes.FeatureUnavailable;
        }
        return 0;
    }

    public async Task EnterAsync(GameSession s, EnterDungeonReq req)
    {
        await DeliverPendingAsync(s);
        if (GuildChallengeEnter != null && await GuildChallengeEnter(s, req)) return;
        var plan = _catalog.Resolve(req);
        int error = plan == null ? ErrorCodes.DungeonDataMissing : Admission(s, plan, continuation: true);
        if (error == 0 && Preparing(s)) error = ErrorCodes.DungeonPreparing;
        var membership = s.Role != null ? _teams.TeamOf(s.Role.Id) : default;
        long[] ids = s.Role == null ? [] : plan?.MaxPlayers == 1 ? [s.Role.Id] : _teams.MembersOf(s.Role.Id).ToArray();
        var online = _sessions.All().Where(p => p.Role != null).ToDictionary(p => p.Role!.Id);
        var members = ids.Where(online.ContainsKey).Select(id => online[id]).ToArray();
        if (error == 0 && ids.Length > 1 && membership.Leader != s.Role!.Id) error = ErrorCodes.NoPermission;
        // GM "!dungeonsolo on" (test aid, not original behaviour): a lone role may also enter a team boss with 30400.
        if (error == 0 && ids.Length < plan!.MinPlayers && !(ids.Length == 1 && s.DungeonSoloMatch && plan.Multi != null))
            error = ErrorCodes.DungeonTeamSize;
        if (error == 0 && ids.Length > plan!.MaxPlayers) error = ErrorCodes.PartyFull;
        if (error == 0 && members.Length != ids.Length) error = ErrorCodes.PlayerOffline;
        if (error == 0)
            foreach (var member in members)
            {
                error = Preparing(member) ? ErrorCodes.DungeonPreparing : Admission(member, plan!, continuation: true);
                if (error != 0) break;
                // A continuation may include only members of this settled room, never a second party's instance.
                if (member.InstanceId != 0 && member.InstanceId != s.InstanceId) { error = ErrorCodes.DungeonBusy; break; }
            }
        // Reject server-owned run/team identities supplied by a forged request; zero is the direct Lua sender.
        var selected = req.InitData?.MultiDungeonInitData;
        if (error == 0 && selected != null && (selected.TeamID != 0 && selected.TeamID != membership.TeamId
            || selected.DungeonId != 0 && selected.DungeonId != s.InstanceId)) error = ErrorCodes.Parameter;
        await s.SendGameAsync(Enter, new EnterDungeonRsp { Result = error });
        if (error != 0) return;
        if (members.Length == 1) { await BeginAsync(plan!, members, s.Role!.Id, membership.TeamId); return; }
        var p = new Preparation { Plan = plan!, Members = members, Leader = s, TeamId = membership.TeamId };
        p.Ready.Add(s); // Initiating the request is the leader's affirmative answer (inferred).
        foreach (var member in members) _pending[member] = p;
        await BroadcastReadyAsync(p);
        int seconds = _data.Table("PublicParamConfig").TryGetValue("GetInPositionMaxTime", out var timeout)
            ? (int)(GameData.Number(timeout) ?? 30) : 30;
        _loop.ScheduleLegacy(TimeSpan.FromSeconds(seconds), () => p.Closed ? Task.CompletedTask : CancelAsync(p, ErrorCodes.Parameter, 0));
    }

    /// <summary>
    /// A Valhalla Trials layer (ValhallaService owns its admission): the ordinary private-room engine runs the boss,
    /// the clear is a Records entry written with its reward (FreezeLockedAsync). 0 or the refusal to answer with.
    /// </summary>
    internal async Task<int> AdmitValhallaAsync(GameSession s, DungeonCatalog.Plan plan)
    {
        await DeliverPendingAsync(s);
        int error = Admission(s, plan);
        return error == 0 && Preparing(s) ? ErrorCodes.DungeonPreparing : error;
    }

    /// <summary>After <see cref="AdmitValhallaAsync"/> and the 83490 reply that closes the entry panel.</summary>
    internal Task BeginValhallaAsync(GameSession s, DungeonCatalog.Plan plan) => BeginAsync(plan, [s], s.Role!.Id, 0);

    /// <summary>The cleared Valhalla layers: Records keyed <c>valhalla:&lt;layer&gt;</c>.</summary>
    internal static IEnumerable<long> ValhallaCleared(State? state) => state == null ? []
        : state.Records.Where(r => r.Value.Passed && r.Key.StartsWith(ValhallaPrefix, StringComparison.Ordinal))
            .Select(r => long.Parse(r.Key.AsSpan(ValhallaPrefix.Length), System.Globalization.CultureInfo.InvariantCulture));

    internal const string ValhallaPrefix = "valhalla:";

    /// <summary>42033 is the actual prepare-panel producer (LC_DAL_Team.lua:3337-3587), not teleport invite 30118.</summary>
    private Task BroadcastReadyAsync(Preparation p, bool cancel = false, int error = 0, long role = 0)
    {
        var msg = new TeamEnterReadyQueueNotify
        {
            DungeonId = p.Plan.SceneEntry, DungeonInitData = p.Plan.Init(p.TeamId),
            Cancel = cancel, CancelErrorCode = error, CancelRoleId = role
        };
        foreach (var member in p.Members) if (member.Role != null) msg.ReadyQueue[member.Role.Id] = p.Ready.Contains(member);
        return Task.WhenAll(p.Members.Where(s => s.Role != null).Select(s => SendQuietAsync(s, ReadyQueue, msg)));
    }

    private async Task CancelAsync(Preparation p, int error, long role)
    {
        if (p.Closed) return;
        p.Closed = true;
        foreach (var member in p.Members) if (_pending.GetValueOrDefault(member) == p) _pending.Remove(member);
        await BroadcastReadyAsync(p, true, error, role);
    }

    public async Task ConfirmAsync(GameSession s, ConfirmTeamDungeonReq req)
    {
        var p = _pending.GetValueOrDefault(s);
        int error = p == null || p.Closed ? ErrorCodes.Parameter : Admission(s, p.Plan, continuation: true);
        if (error == 0 && (_teams.TeamOf(p!.Leader.Role!.Id) != (p.TeamId, p.Leader.Role.Id)
            || !_teams.MembersOf(p.Leader.Role.Id).Order().SequenceEqual(p.Members.Select(m => m.Role?.Id ?? 0).Order())))
            error = ErrorCodes.MemberLeft;
        await s.SendGameAsync(Confirm, new ConfirmTeamDungeonRsp { Result = error });
        if (p == null || p.Closed) return;
        if (error != 0 || !req.IsAgree) { await CancelAsync(p, error != 0 ? error : ErrorCodes.DungeonDeclined, s.Role!.Id); return; }
        p.Ready.Add(s);
        await BroadcastReadyAsync(p);
        if (p.Ready.Count != p.Members.Length) return;
        // Revalidate everyone after the last confirmation; no member can enter dead/offline/in another scene.
        foreach (var member in p.Members)
        {
            error = _sessions.All().Contains(member) ? Admission(member, p.Plan, continuation: true) : ErrorCodes.PlayerOffline;
            if (error != 0) { await CancelAsync(p, error, member.Role?.Id ?? 0); return; }
        }
        p.Closed = true;
        foreach (var member in p.Members) _pending.Remove(member);
        await BeginAsync(p.Plan, p.Members, p.Leader.Role!.Id, p.TeamId);
    }

    /// <summary>42047 reminds a member of this live 42033 ready vote (LC_DAL_Team:3641-3715).</summary>
    public async Task RemindAsync(GameSession s, TeamConfirmRemindersReq req)
    {
        var p = _pending.GetValueOrDefault(s);
        // PreparePanel passes (selfGuid, selectedUnreadyGuid) to TeamConfirmReminder.
        var target = p?.Members.FirstOrDefault(m => m.Role?.Id == req.RemindersGuid);
        int code = p == null || p.Closed || p.Leader != s || target == null || p.Ready.Contains(target)
            || req.PlayerId != s.Role?.Id || req.RemindersGuid == s.Role?.Id
            ? ErrorCodes.NoPermission : 0;
        await s.SendGameAsync(ConfirmReminder, new TeamConfirmRemindersResp { Result = code });
        if (code == 0) await SendQuietAsync(target!, ConfirmReminderNotify,
            new TeamConfirmRemindersNotify { MemberGuid = s.Role!.Id });
    }

    private async Task BeginAsync(DungeonCatalog.Plan plan, GameSession[] members, long leader, long team, bool matched = false)
    {
        if (IsRandom(plan)) { await RollAsync(plan, members, leader, team, matched); return; } // DungeonService.HiddenRealm
        var room = _instances.Create(plan.SceneEntry, members);
        _praise.Remember(room.Id, plan.SceneEntry, members.Select(m => m.Role!.Id)); // MVP likes on its result screen
        // (inferred) 30107 acknowledges a real room allocation; this client build has no Lua handler beyond PB registration.
        foreach (var member in members)
            await SendQuietAsync(member, CreateResult, new CreateDungeonResultNotify());
        var a = new Attempt
        {
            Room = room, Plan = plan, Leader = leader, TeamId = team,
            PlayBook = _seasons.PlayBook, SeasonId = _seasons.SeasonId, Matched = matched,
            Blessing = plan.Boss is { } blessed ? Blessing(blessed)?.Row : null
        };
        foreach (var member in members)
        {
            a.Stats[member.Role!.Id] = new RoleStatisticsInfo
            {
                BaseInfo = new StatisticsRoleBaseInfo
                {
                    Id = member.Role.Id, Name = member.Role.Name, Job = member.Progress!.Career, Area = 1, TeamId = team,
                    RewardData = new StatisticsAwardCheckData { PlayerLevel = member.Progress.Level, Job = member.Progress.Career }
                }
            };
            a.StartingItems[member] = Inventory(member);
        }
        foreach (var member in members)
        {
            try
            {
                if (member.DungeonAttempt != null) await member.SendGameAsync(Reset, new DungeonResetNtf());
                if (await _instances.EnterAsync(room, member, members.Length > 1 ? 5 : 3))
                {
                    member.DungeonAttempt = a;
                    // (inferred) actual scene/room identities after successful handoff, never on a failed transfer.
                    await SendQuietAsync(member, EnterSceneResult, new ToSceneEnterDungeonResult
                    {
                        SceneId = plan.SceneEntry, DungeonId = room.Id,
                    });
                }
                else await _instances.AbandonAsync(room, member);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Dungeon transfer failed for {Role}, instance {Instance}", member.Role?.Id, room.Id);
                await _instances.AbandonAsync(room, member);
            }
        }
        a.Entering = false;
        if (ReadyToStart(a)) await StartAsync(a);
        // A client that never acknowledges scene loading cannot retain a room indefinitely (inferred timeout).
        _loop.ScheduleLegacy(TimeSpan.FromSeconds(60), async () =>
        {
            foreach (var member in Present(a).Where(s => !a.Loaded.Contains(s))) await _instances.LeaveAsync(member);
        });
    }

    private async Task LoadedAsync(GameSession s)
    {
        if (s.DungeonAttempt is not { } a || s.InstanceId != a.Room.Id || a.Room.Closed || !a.Loaded.Add(s)) return;
        await s.SendGameAsync(BaseInfo, Snapshot(a));
        await SendReviveInfoAsync(s, a);
        await SendRosterAsync(a);
        await RefreshBondsAsync(a);
        if (ReadyToStart(a)) await StartAsync(a);
    }

    // Waiting for all remaining members before starting the encounter clock is (inferred).
    private static bool ReadyToStart(Attempt a) => !a.Entering && !a.Started && !a.Settled
        && a.GuildChallenge == null // Whale waits for the manager's real DungeonStart command.
        && a.Room.ActiveMembers.Count > 0 && a.Room.ActiveMembers.All(m => a.Loaded.Contains(m.Session));

    // Lua_GameDefine enum_DungeonMapStage: 0/1/2 initializing/running/settling; LC_DunGeon_Base:1507-1522
    // divides these wire timestamps by 1000. One level and sequential stages remain (inferred).
    private DungeonBaseInfoNotify Snapshot(Attempt a)
    {
        var init = a.Plan.Init(a.TeamId, a.Room.Id);
        if (a.Matched && init.MultiDungeonInitData != null) init.MultiDungeonInitData.IsMatch = true;
        var snapshot = new DungeonBaseInfoNotify
        {
            DungeonEntry = a.Plan.SceneEntry, DungeonId = a.Room.Id, Stage = a.Wave + 1, Level = 1,
            InitData = init, ChangeType = (DungeonBaseInfoNotify.Types.eChangeType)2,
            Vars = { PlayerList(a) },
            StageModuleData = new DungeonStageModuleData
            {
                MapState = a.Settled ? 2 : a.Started ? 1 : 0,
                Stage = a.Wave + 1, MapStateStartTime = a.StartMs, StageStartTime = a.StartMs,
                MapStateEndTime = a.Started ? a.StartMs + a.Plan.DurationMs : 0,
                StageEndTime = a.Started ? a.StartMs + a.Plan.DurationMs : 0,
                NextStageStartTime = 0, NextStageEndTime = 0
            },
        };
        if (QuestList(a) is { } quests) snapshot.Vars.Add(quests);
        if (GoalParams(a) is { } goal) snapshot.Vars.Add(goal);
        if (a.GuildChallenge is { } challenge)
        {
            snapshot.Vars.Add(challenge.Vars());
            snapshot.Level = challenge.Difficulty;
            // Lua_GameDefine.enum_WhaleDungeonBigStage: Prepare=0, Fight=1, End=2.
            snapshot.Stage = snapshot.StageModuleData.Stage = a.Settled ? 2 : a.Started ? 1 : 0;
            snapshot.StageModuleData.MapStateEndTime = challenge.End * 1000;
        }
        if (a.Dracula is { } dracula) snapshot.Vars.Add(dracula.Vars());
        if (FunctionButtons(a) is { } buttons) snapshot.Vars.Add(buttons);
        if (a.Preparing)
        {
            // (inferred) The preparation phase is the map's initializing state, ending when the boss fight begins.
            snapshot.StageModuleData.MapState = 0;
            snapshot.StageModuleData.MapStateEndTime = a.StartMs + _options.RaidPreparationSeconds * 1000L;
            snapshot.StageModuleData.StageEndTime = snapshot.StageModuleData.MapStateEndTime;
        }
        return snapshot;
    }

    private static DungeonVar PlayerList(Attempt a)
    {
        // LC_DunGeon_VarDefine:145 CDungeonPlayerUidList. Its startup use is (inferred), values are real members.
        var v = new DungeonVar { VarId = 145 };
        v.Vi64.Add(Present(a).Select(s => s.Role!.Id));
        return v;
    }

    /// <summary>
    /// LC_DunGeon_VarDefine 10001 PDungeonQuestIdList: the dungeon task widget shows the last id's DungeonQuestConfig
    /// title and goals (UI_Sys_Main_TeamUp_GVETaskWidgetBaseCtrl._refreshQuest). Sent when the current wave names its
    /// quest: every configured quest of the scene up to it, so earlier ones read as done (inferred).
    /// </summary>
    private DungeonVar? QuestList(Attempt a)
    {
        if (a.Wave >= a.Plan.Waves.Count || a.Plan.Waves[a.Wave].Quest is not (> 0 and var current)) return null;
        var v = new DungeonVar { VarId = 10001 };
        v.Vi64.Add(_data.Table("DungeonQuestConfig").Values
            .Where(q => GameData.Int(q, "_iDungeonID") == a.Plan.SceneEntry && GameData.Int(q, "_iQuestID") <= current)
            .Select(q => GameData.Int(q, "_iQuestID")).Distinct().Order());
        return v;
    }

    /// <summary>
    /// LC_DunGeon_VarDefine 10003 PDungeonQuestGoalParamList: a goal [type, i, j] shows params i+1 and j+1 as its
    /// "(done/total)" (Lua_CFG_DungeonQuestConfig._getQuestInfoByID). The story goals are [5,0,1]: this wave's kills
    /// and its size (inferred: the original server's counters are not recovered).
    /// </summary>
    private static DungeonVar? GoalParams(Attempt a)
    {
        if (a.Wave >= a.Plan.Waves.Count || a.Plan.Waves[a.Wave].Quest <= 0) return null;
        int size = a.Plan.Waves[a.Wave].Spawns.Count;
        var v = new DungeonVar { VarId = 10003 };
        if (Step(a) is { } step && size == 0)
        {
            // A scripted step: its configured counter, or the events an "all" step has seen (inferred).
            v.Vi64.Add(step.All ? [a.StepSeen.Count, step.Complete.Count] : [step.Goal.Done, step.Goal.Total]);
            return v;
        }
        v.Vi64.Add([Math.Clamp(a.Killed.Count - a.WaveBase, 0, size), size]);
        return v;
    }

    private async Task SendQuietAsync(GameSession s, uint id, IMessage msg)
    {
        try { await s.SendGameAsync(id, msg); }
        catch (Exception e) { _log.LogWarning(e, "Dungeon notification {Message} failed for {Role}", id, s.Role?.Id); }
    }

    private Task BroadcastAsync(Attempt a, uint id, IMessage msg) =>
        Task.WhenAll(Present(a).Where(a.Loaded.Contains).Select(s => SendQuietAsync(s, id, msg)));

    /// <summary>
    /// 30441 DungeonTeamInfoNotify. A nonzero teamId switches the client to its Virtual team (LC_DAL_VirtualTeam
    /// _processDungeonTeamInfoNotify, LC_TeamDataRouter), whose actions are 30442-30451; every group this engine runs is
    /// a TeamService party or no party, so the id is 0 (Default mode): the party HUD and the 42009/42012/42015/42031 team
    /// actions stay on the real team, as the _iConvene=1 dungeons (PVE_10P, FivePeopleDungeon, ...) imply (inferred).
    /// Positions are 0-based: the client shows position + 1 in slots 1..5 (UI_Sys_TeamFiveWidgetCtrl).
    /// </summary>
    private Task SendRosterAsync(Attempt a)
    {
        var roster = new DungeonTeamInfoNotify { TeamId = 0, LeaderGuid = a.Leader, TeamType = (DungeonTeamType)1 };
        var squad = new DungeonTeamSquadInfo { SquadIdx = 1, SquadLeaderGuid = a.Leader };
        foreach (var member in Present(a))
            squad.Members[member.Role!.Id] = new DungeonTeamMemberInfo { Guid = member.Role.Id, Position = Slot(a, member.Role.Id) };
        roster.Squads[1] = squad; // ordinary preformed roster, tactical reorganization is not implemented (inferred).
        return BroadcastAsync(a, TeamInfo, roster);
    }

    /// <summary>
    /// A member's 0-based roster slot, kept for the whole run: the party's own slot order when the run started (the
    /// leader's slot in it), else the leader first; later arrivals take the next slot (inferred).
    /// </summary>
    private int Slot(Attempt a, long role)
    {
        if (a.Slots.Count == 0)
        {
            if (a.TeamId != 0 && _teams.TeamOf(a.Leader).TeamId == a.TeamId) a.Slots.AddRange(_teams.MembersOf(a.Leader));
            else a.Slots.Add(a.Leader);
        }
        int slot = a.Slots.IndexOf(role);
        if (slot >= 0) return slot;
        a.Slots.Add(role);
        return a.Slots.Count - 1;
    }

    private async Task StartAsync(Attempt a)
    {
        if (a.Started || a.Settled || a.Room.Closed) return;
        a.Started = true; a.StartedAt = _loop.Now; a.StartMs = MonsterService.NowMs;
        if (StartBattleScenes.Contains(a.Plan.SceneEntry) && a.Plan.Multi != null)
        {
            // A Realm of the Gods raid opens with its preparation phase: the leader's Start Battle (30125) or the
            // configured time begins the fight (DungeonService.RealmOfGods).
            a.Preparing = true;
            await BroadcastAsync(a, BaseInfo, Snapshot(a));
            _loop.ScheduleLegacy(TimeSpan.FromSeconds(Math.Max(0, _options.RaidPreparationSeconds)), () => EndPreparationAsync(a));
            return;
        }
        await BeginFightAsync(a);
    }

    private async Task BeginFightAsync(Attempt a)
    {
        a.StartedAt = _loop.Now; a.StartMs = MonsterService.NowMs;
        await BroadcastAsync(a, BaseInfo, Snapshot(a));
        await SpawnWaveAsync(a);
        _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(a.Plan.DurationMs), () => a.Settled || a.Room.Closed ? Task.CompletedTask : SettleAsync(a, false));
    }

    private async Task SpawnWaveAsync(Attempt a)
    {
        if (a.Settled || a.Room.Closed) return;
        a.Spawning = true;
        a.WaveBase = a.Spawned;
        var change = new DungeonEventNotify
        {
            DungeonEntry = a.Plan.SceneEntry,
            EventType = (DungeonEventType)1, StageNum = a.Wave + 1, LevelNum = 1, Vars = { PlayerList(a) }
        };
        if (QuestList(a) is { } quests) change.Vars.Add(quests);
        if (GoalParams(a) is { } goal) change.Vars.Add(goal);
        if (a.GuildChallenge is { } challenge) change.Vars.Add(challenge.Vars());
        await BroadcastAsync(a, Event, change);
        // Seen live: the task widget takes its vars from 30401 only, not from this event; a later wave whose
        // quest is known republishes the state so the tracker moves on (the first one follows StartAsync's).
        if (a.Wave > 0 && QuestList(a) != null) await BroadcastAsync(a, BaseInfo, Snapshot(a));
        if (a.Dracula is { } dracula) await dracula.Owner.WaveAsync(a);
        if (Step(a) is { } step)
        {
            await BeginStepAsync(a, step);
            if (a.Plan.Waves[a.Wave].Spawns.Count == 0)
            {
                a.Spawning = false;
                await AdvanceAsync(a); // waits for the step's client event
                return;
            }
            // A step with a fight spawns it below; it advances once its event and its kills are both in.
        }
        foreach (var spawn in a.Plan.Waves[a.Wave].Spawns)
        {
            if (a.Settled || a.Room.Closed) { a.Spawning = false; return; }
            MonsterTemplate? waveTemplate = null;
            if (a.Plan.SceneEntry == EggThiefDungeon && _monsters.Template(spawn.NpcId) is { } template)
            {
                // (inferred) The missing original encounter script sent these three portal waves toward the player. The
                // reconstructed first-wave Pupa is immobile and all three authored portals lie outside its normal
                // 20 m home leash, so ordinary field AI leaves two actors off-camera forever. Use the 550 cm/s pace
                // already carried by this encounter's Boa/Willow waves and a room-sized leash for this scene only.
                waveTemplate = template with
                {
                    SpeedCm = Math.Max(template.SpeedCm, EggThiefWaveSpeedCm),
                    LeashDistance = Math.Max(template.LeashDistance, EggThiefWaveLeash),
                };
            }
            var monster = await _instances.SpawnAsync(a.Room, spawn.NpcId, spawn.Point, customTemplate: waveTemplate);
            if (a.Settled || a.Room.Closed)
            {
                a.Spawning = false;
                await _instances.ClearMonstersAsync(a.Room);
                return;
            }
            if (monster == null) { a.Spawning = false; await SettleAsync(a, false); return; }
            a.Spawned++;
            // Multi-bosses retain their AI instead of being ordered to attack on scene load. In Culvert the
            // born point 15 is 15m from monster point 19; NPC 4300000/1 uses non-roaming AI 25000001 and
            // RaidFive_Boss00[easy], whose server script is absent. Waiting for a player hit/taunt is an
            // (inferred) fallback, not proof that its 100m view/leash means automatic entry aggro.
            // No five-player preparation duration/start-button contract was recovered; GVE/Whale's
            // DungeonStart preparation UI belongs to other gameplay types. Story-wave engagement is unchanged.
            if (a.Plan.Multi == null && Present(a).FirstOrDefault(s => !s.Dead && !s.SceneLoading) is { } target)
                _monsters.Engage(monster, target);
        }
        a.Spawning = false;
        await AdvanceAsync(a);
    }

    private async Task KilledAsync(GameSession s, MonsterUnit monster)
    {
        if (Active(s) is not { } a || monster.InstanceId != a.Room.Id || !a.Room.Monsters.Contains(monster)) return;
        bool precounted = a.Precounted.Remove(monster.Uid);
        if (!precounted && !a.Killed.Add(monster.Uid)) return;
        // A terminal freeze may still be awaiting its writer when this already-durable
        // actor publishes. Its count was added during precounting, even before Frozen flips.
        if (!precounted && !a.ObligationsFrozen)
        {
            var stats = a.Stats[s.Role!.Id];
            if (!stats.KillNpcData.TryGetValue(monster.Template.NpcId, out var count))
                stats.KillNpcData[monster.Template.NpcId] = count = new ExtraValueData();
            count.LongValue++;
        }
        // The tracker's "(done/total)" follows each kill of a wave that plays a known quest.
        if (!a.Settled && GoalParams(a) != null) // a precounted kill is already in Killed
            await BroadcastAsync(a, BaseInfo, Snapshot(a));
        if (a.Precounted.Count == 0) await AdvanceAsync(a);
    }

    private async Task AdvanceAsync(Attempt a)
    {
        if (a.Settled || a.Preparing || a.Spawning || a.Precounted.Count != 0 || a.Killed.Count != a.Spawned || a.Advancing) return;
        if (Step(a) != null && !a.StepDone) return;
        a.Advancing = true;
        if (_loop.Now - a.StartedAt >= TimeSpan.FromMilliseconds(a.Plan.DurationMs)) await SettleAsync(a, false);
        else if (a.Wave + 1 == a.Plan.Waves.Count) await SettleAsync(a, true);
        else { a.Wave++; await SpawnWaveAsync(a); }
        a.Advancing = false;
        // Kills can arrive while asynchronous spawning is finishing; recheck the completed wave once.
        if (!a.Settled && a.Killed.Count == a.Spawned) await AdvanceAsync(a);
    }

    // Lua_GameDefine:8927-8934: 0 OnClickRevive, 1 AutoRevive, 2 NoRevive. Within OnClickRevive,
    // LC_SceneCom_Revive.lua:650-659,1165-1182 interprets [2,5] as an automatic request after five seconds.
    private static int AutoReviveDelay(Attempt a)
    {
        if (GameData.Int(a.Plan.Config, "_iReviveTrigger") != 0
            || !a.Plan.Config.TryGetProperty("_kTriggerParm", out var options)
            || options.ValueKind != JsonValueKind.Array) return 0;
        foreach (var option in options.EnumerateArray())
            if (option.ValueKind == JsonValueKind.Array && option.GetArrayLength() >= 2
                && GameData.Number(option[0]) == 2)
                return (int)Math.Clamp(GameData.Number(option[1]) ?? 0, 0, 3600);
        return 0;
    }

    // Lua_GameDefine.lua:8936-8952 names option 0 Immediately; the client's normal revive button sends 31013.
    private static bool ImmediateRevive(Attempt a)
    {
        if (a.Plan.Boss != null || GameData.Int(a.Plan.Config, "_iReviveTrigger") != 0
            || !a.Plan.Config.TryGetProperty("_kTriggerParm", out var options)
            || options.ValueKind != JsonValueKind.Array) return false;
        foreach (var option in options.EnumerateArray())
            if (option.ValueKind == JsonValueKind.Array && option.GetArrayLength() > 0
                && GameData.Number(option[0]) == 0) return true;
        return false;
    }

    // In particular 3120401 is NoRevive; a boss's initial count does not authorize self-revive there.
    // A Sacred Blessing level's _iAddReviveNum ("increases the team's Revive count in the Dungeon") adds to the boss's own.
    private static int ReviveLimit(Attempt a) => GameData.Int(a.Plan.Config, "_iReviveTrigger") == 0
        && a.Plan.Boss is { } boss ? Math.Max(0, (int)GameData.Int(boss, "_iOriginalReviveNum")) + BlessingRevives(a) : 0;
    private static bool Defeated(Attempt a) => !ImmediateRevive(a) && Present(a).All(p => p.Dead
        && !a.AutoReviveAt.ContainsKey(p.Role!.Id)
        && a.RevivesUsed.GetValueOrDefault(p.Role.Id) >= ReviveLimit(a));

    /// <summary>All resurrection sources obey the active dungeon's click-revive allowance.</summary>
    public int ExternalReviveError(GameSession s)
    {
        if (!s.Dead) return ErrorCodes.TargetAlive;
        if (s.DungeonAttempt == null) return 0;
        var a = Active(s);
        return a == null || s.Role == null || a.Reviving.Contains(s)
            || a.RevivesUsed.GetValueOrDefault(s.Role.Id) >= ReviveLimit(a)
            ? ErrorCodes.FeatureUnavailable : 0;
    }

    /// <summary>Call exactly once after a successful skill/item resurrection in an active run.</summary>
    public void RecordExternalRevive(GameSession s)
    {
        if (s.DungeonAttempt == null) return;
        var a = Active(s);
        if (a == null || s.Role == null) return;
        a.RevivesUsed[s.Role.Id] = a.RevivesUsed.GetValueOrDefault(s.Role.Id) + 1;
    }

    public Task PublishExternalReviveAsync(GameSession s) => s.Role != null && Active(s) is { } a
        ? SendReviveInfoAsync(s, a) : Task.CompletedTask;

    private async Task PlayerDiedAsync(GameSession s)
    {
        if (Active(s) is not { } a) return;
        int delay = AutoReviveDelay(a);
        if (delay > 0) a.AutoReviveAt.TryAdd(s.Role!.Id, _clock.GetUtcNow().ToUnixTimeSeconds() + delay);
        await SendReviveInfoAsync(s, a);
        if (Defeated(a)) await SettleAsync(a, false);
    }

    /// <summary>
    /// NoRevive and out-of-scope top-level automatic/script modes refuse self-revival. OnClickRevive [0] admits
    /// immediate normal-button requests; [2,5] admits the client's timed automatic request. Finite boss counts
    /// retain their existing precedence and are per participant
    /// (inferred; shared-pool scope is unproven).
    /// Default 31013 uses the existing map resurrection point/full HP (inferred); arbitrary point/script requests
    /// are refused. A party loses when all remaining players are dead and out of revivals (inferred).
    /// </summary>
    public async Task ReviveAsync(GameSession s, EntityReliveReq req)
    {
        var a = Active(s);
        long roleId = s.Role?.Id ?? 0;
        long autoAt = a?.AutoReviveAt.GetValueOrDefault(roleId) ?? 0;
        int error = !s.Dead ? ErrorCodes.TargetAlive : a == null || s.Role == null ? ErrorCodes.FeatureUnavailable
            : req.ReviveParam != 0 || (int)req.Type != 0 || (int)req.ReqType != 0 ? ErrorCodes.Parameter
            : a.Reviving.Contains(s) || (autoAt > 0
                ? _clock.GetUtcNow().ToUnixTimeSeconds() < autoAt
                : !ImmediateRevive(a) && a.RevivesUsed.GetValueOrDefault(roleId) >= ReviveLimit(a))
                ? ErrorCodes.FeatureUnavailable : 0;
        if (error == 0 && !_combat.TryClaimRevive(s)) error = s.Dead ? ErrorCodes.FeatureUnavailable : ErrorCodes.TargetAlive;
        if (error != 0)
        {
            await s.SendGameAsync(CombatService.Relive, new EntityReliveAck { Result = error, EntityID = (ulong)(s.Role?.Id ?? 0) });
            return;
        }
        a!.Reviving.Add(s);
        bool counted = autoAt == 0 && !ImmediateRevive(a);
        try
        {
            await _combat.ReviveClaimedAsync(s, () =>
            {
                if (counted) a.RevivesUsed[roleId] = a.RevivesUsed.GetValueOrDefault(roleId) + 1;
                a.AutoReviveAt.Remove(roleId);
            });
        }
        finally
        {
            a.Reviving.Remove(s);
        }
        await SendReviveInfoAsync(s, a);
    }

    private Task SendReviveInfoAsync(GameSession s, Attempt a) => a.Plan.Multi == null && !s.Dead ? Task.CompletedTask
        : SendQuietAsync(s, ReviveInfo, new SyncClientPlayerReviveInfoNtf
        {
            BDead = s.Dead,
            // LC_SceneCom_Revive.lua:254-261 explicitly interprets freeReviveNum as USED, not remaining.
            FreeReviveNum = a.RevivesUsed.GetValueOrDefault(s.Role!.Id),
            ReviveTime = a.AutoReviveAt.GetValueOrDefault(s.Role.Id), SceneEntry = a.Plan.SceneEntry
        });

    public async Task ClientEventAsync(GameSession s, ClientDungeonEvent req)
    {
        if (s.DungeonAttempt?.GuildChallenge is { } challenge)
        {
            await challenge.Owner.ClientEventAsync(s, req);
            await s.SendGameAsync(ClientEvent, new NullArg());
            return;
        }
        // (inferred) The lair is the whole guild's room: no member's give-up or reset ends it for the others.
        if (s.DungeonAttempt?.Dracula != null)
        {
            await s.SendGameAsync(ClientEvent, new NullArg());
            return;
        }
        // LC_DAL_Dungeon.lua:69-87: only control events with no client-authored rewards/functions are accepted.
        // Leader-only surrender and reset-after-settlement into a fresh room are (inferred control policy).
        if (s.DungeonAttempt is { } a && s.InstanceId == a.Room.Id && !s.SceneLoading && s.Role!.Id == a.Leader)
        {
            if (req.StrCmd == "DungeonGiveUp" && !a.Settled) await SettleAsync(a, false);
            else if (req.StrCmd == "DungeonReset" && a.Settled)
                await EnterAsync(s, new EnterDungeonReq { SceneEntry = a.Plan.SceneEntry, InitData = a.Plan.Init() });
            // DungeonStart / timeline / unknown commands cannot advance combat or produce a success.
        }
        await s.SendGameAsync(ClientEvent, new NullArg());
    }

    public async Task LeaveAsync(GameSession s)
    {
        var role = s.Role;
        var room = _instances.Find(s.InstanceId);
        var member = room?.MemberFor(s);
        bool valid = role != null && !s.SceneLoading && room != null && member != null
            && s.SceneEntry == room.SceneEntry;
        var attempt = s.DungeonAttempt;
        // Only an owned ordinary/multi run opts into inferred recovery; other private-room owners retain
        // their existing exit semantics. A stale dungeon attempt cannot turn another room's exit into a revive.
        bool dungeonReturn = valid && (attempt != null && attempt.GuildChallenge == null
            && ReferenceEquals(attempt.Room, room) || attempt == null && RecoverDeadOnLeave?.Invoke(room!.Id) == true);
        await s.SendGameAsync(Leave, new LeaveDungeonRsp { Result = valid ? 0 : ErrorCodes.SceneMissing });
        if (!valid || !ReferenceEquals(s.Role, role) || s.SceneLoading || s.InstanceId != room!.Id
            || s.SceneEntry != room.SceneEntry || !ReferenceEquals(_instances.Find(room.Id), room)
            || room.MemberFor(s) != member || !ReferenceEquals(s.DungeonAttempt, attempt)) return;
        await _instances.LeaveAsync(s, recoverDeadOnReturn: dungeonReturn);
    }

    /// <summary>
    /// Disconnect/exit cancels a pending vote and detaches this participant. Remaining members may continue below
    /// the entry minimum; reconnect returns outside and does not rejoin the run (inferred abandonment policy).
    /// </summary>
    public async Task AbandonAsync(GameSession s)
    {
        if (_pending.TryGetValue(s, out var p)) await CancelAsync(p, ErrorCodes.MemberLeft, s.Role?.Id ?? 0);
        if (s.DungeonAttempt is not { } a) return;
        s.DungeonAttempt = null; s.Attack = null;
        if (a.Dracula is { } dracula) await dracula.Owner.LeftAsync(a, s);
        await ClearBondsAsync(s, a);
        await _instances.AbandonAsync(s);
        var remaining = Present(a);
        if (remaining.Length == 0) { a.Settled = true; return; }
        await RefreshBondsAsync(a);
        await SendRosterAsync(a);
        if (ReadyToStart(a)) await StartAsync(a);
        else if (!a.Settled && Defeated(a)) await SettleAsync(a, false);
    }

    /// <summary>
    /// All configured actors dead before timeout wins (inferred predicate). Reserve once before any await.
    /// Each role's earned personal award and allowance are one SQLite transaction; disconnect is not a clear.
    /// </summary>
    private async Task SettleAsync(Attempt a, bool won)
    {
        if (a.Settled || a.Room.Closed) return;
        if (a.GuildChallenge is { } challenge)
        {
            a.Settled = true;
            await _instances.ClearMonstersAsync(a.Room);
            await BroadcastAsync(a, BaseInfo, Snapshot(a));
            await challenge.Owner.SettleAsync(a, won);
            return;
        }
        if (a.Dracula is { } dracula)
        {
            a.Settled = true;
            await _instances.ClearMonstersAsync(a.Room);
            a.SceneActors.Clear(); // removed with the room's monsters
            await BroadcastAsync(a, BaseInfo, Snapshot(a));
            await dracula.Owner.SettleAsync(a, won);
            return;
        }
        if (a.ObligationsFrozen && !a.ObligationsActivated)
            if (!await ResolvePreparedAsync(a)) won = false;
        await FreezeAsync(a, won);
        won = a.FrozenWin;
        a.Settled = true;
        var participants = Present(a).Where(a.Loaded.Contains).Select(s => (Session: s, Role: s.Role!.Id)).ToArray();
        var notifications = new List<Func<Task>>();
        var auctionAwards = new List<ItemBaseInfo>();
        await _instances.ClearMonstersAsync(a.Room);
        a.SceneActors.Clear(); // removed with the room's monsters
        foreach (var (s, roleId) in participants)
        {
            if (s.Role?.Id != roleId) continue;
            s.Attack = null;
            var sourceKey = $"dungeon/{_character.RewardRealmId}/{a.Room.Id}/{roleId}";
            var row = _obligations.Pending(roleId).SingleOrDefault(p => p.Key == sourceKey);
            if (row == null) throw new InvalidOperationException("Frozen dungeon recipient obligation is missing.");
            bool success = row.Success;
            var goods = row.Goods;
            var awardedItems = new List<ItemBaseInfo>();
            bool newRecord = row.NewRecord;
            bool committed = false;
            if (!a.Paid.Contains(s))
            {
                try
                {
                    var delivered = await DeliverAsync(s, row);
                    committed = delivered.Committed;
                    awardedItems = delivered.Awarded;
                    if (committed) a.Paid.Add(s);
                }
                catch (Exception e)
                {
                    _log.LogError(e, "Dungeon settlement failed for {Role}, instance {Instance}", roleId, a.Room.Id);
                    if (s.Owner?.State == Ownership.RoleOwnerState.RecoveryRequired)
                    {
                        s.CancelPendingCharacterIntents();
                        s.AbortForRecovery();
                    }
                    // The frozen outcome remains a win (or a loss) with a pending recipient delivery.
                    // Do not publish a contradictory terminal result while its economic source is unresolved.
                    continue;
                }
            }
            if (!committed) continue;
            if (committed && success)
            {
                foreach (var handler in Completed)
                    try { await handler(s, (int)a.Plan.SceneEntry, goods.Count > 0); }
                    catch (Exception e) { _log.LogError(e, "Dungeon clear callback failed for {Role}, instance {Instance}", roleId, a.Room.Id); }
                if (a.Plan.Boss is { } boss)
                {
                    var party = participants.Select(p => p.Session).ToArray();
                    foreach (var handler in MultiBossCleared)
                        try { await handler(s, GameData.Int(boss, "_iBoss"), party); }
                        catch (Exception e) { _log.LogError(e, "Dungeon boss clear callback failed for {Role}, instance {Instance}", roleId, a.Room.Id); }
                }
            }
            var result = new DungeonResNotify { DungeonEntry = a.Plan.SceneEntry, Res = (DungeonRes)(success ? 1 : 2) };
            if (success)
            {
                foreach (var group in goods.GroupBy(g => g.Entry)) result.Rewards[group.Key] = group.Sum(g => (long)g.Count);
                // Story rewards belong to existing NPC/task pipelines; report only actual item/currency deltas.
                if (a.Plan.Multi == null)
                    foreach (var (entry, count) in Inventory(s))
                        if (count > a.StartingItems[s].GetValueOrDefault(entry)) result.Rewards[entry] = count - a.StartingItems[s].GetValueOrDefault(entry);
            }
            notifications.Add(async () =>
            {
                if (s.Role?.Id != roleId) return;
                try
                {
                    if (committed && goods.Count > 0) await _character.ItemsChangedAsync(s, stats: false);
                    await s.SendGameAsync(Result, result);
                    if (success && a.Plan.Multi != null)
                    {
                        var award = new SyncMultiDungeonAwardNotify
                        {
                            PassTime = row.DurationMs, IsFastRecord = newRecord,
                            Honor = EarnedHonors(row)
                        };
                        award.NormalAward.Add(awardedItems);
                        award.AutionAward.Add(auctionAwards);
                        await s.SendGameAsync(AwardNotify, award);
                    }
                    if (committed) await SyncAsync(s);
                }
                catch (Exception e) { _log.LogWarning(e, "Dungeon result delivery failed for {Role}, instance {Instance}", roleId, a.Room.Id); }
            });
        }
        if (won && a.Plan.Multi != null && a.Plan.Boss is { } cleared)
            RecordFirstClear(a, cleared, participants.Select(p => p.Session).Where(s => s.Role != null).ToArray());
        // Finalize only from committed personal entitlements; pending journals recover on restart.
        if (won && a.Plan.Multi != null && _obligations.PendingRoom(a.Room.Id).Count == 0)
        {
            try
            {
                await _auctions.MarkDungeonRunReadyAsync(a.Room.Id);
                await _auctions.FinalizeDungeonRunAsync(a.Room.Id);
                foreach (var group in _auctions.DungeonRows(a.Room.Id).GroupBy(r => (r.Entry, r.Bind)))
                    auctionAwards.Add(_character.ItemInfoOf(new InvItem
                    { Entry = group.Key.Entry, Stack = group.Count(), BindType = group.Key.Bind }));
            }
            catch (Exception e)
            {
                // Personal rewards already committed. The durable run journal lets the sweep retry auction
                // publication; it must not suppress the dungeon result/normal award notifications.
                _log.LogError(e, "Auction publication deferred after committed dungeon {Instance}", a.Room.Id);
            }
        }
        // A disconnected member's channel must never prevent another participant's committed award.
        foreach (var notify in notifications) await notify();
    }
}
