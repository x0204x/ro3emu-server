using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Economy;
using Microsoft.Extensions.Options;

namespace Ro3Emu.Server.Game;

/// <summary>
/// 82681/82636/82637 Kafra Survivor and Dragon Coffin matchmaking. MatchConfig 2 and 6 select the
/// exported EatChickenMatchConfig/DragonIslandMatchConfig scenes and specialized population/team sizes.
/// CombatService calls CanFight/PlayerKilledAsync for actual opposing-player combat.
/// </summary>
public sealed partial class TimedBattleService : IGameTicker
{
    public const uint StartMatch = 82681, CancelMatch = 82682, Ready = 82637, ReadyNotify = 82636,
        MatchStartNotify = 82638, ChickenResult = 82633, DragonClaim = 84071, DragonSettle = 84074,
        OnePieceInfo = 83351, OnePieceMap = 83350;
    private const int ChickenEntry = 2, DragonEntry = 6;

    internal bool IsOwnerRequest(uint message, ReadOnlyMemory<byte> body)
    {
        if (message is DragonClaim or OnePieceInfo) return true;
        if (message == StartMatch)
        {
            var request = StartMatchReq.Parser.ParseFrom(body.Span);
            return request.MatchEntryId is ChickenEntry or DragonEntry && request.MatchData?.MultiDungeon == null;
        }
        if (message == CancelMatch)
        {
            var request = CancelMatchReq.Parser.ParseFrom(body.Span);
            return request.MatchEntryId is ChickenEntry or DragonEntry && request.MatchData?.MultiDungeon == null
                && request.NextMatchData?.MultiDungeon == null;
        }
        if (message == Ready)
            return MatchReadyReq.Parser.ParseFrom(body.Span).MatchEntryId is ChickenEntry or DragonEntry;
        if (message == DungeonService.Enter)
            return data.Row("OnePieceRewardConfig", EnterDungeonReq.Parser.ParseFrom(body.Span).SceneEntry) != null;
        if (message == SceneService.NpcInteraction)
            return IsRiftUid(NpcInteractionReq.Parser.ParseFrom(body.Span).NpcUid);
        return false;
    }
    private readonly GameData data;
    private readonly TimedTodoSchedule schedule;
    private readonly TimeProvider time;
    private readonly RoleStore roles;
    private readonly AccountStore accounts;
    private readonly CharacterService character;
    private readonly FunctionService functions;
    private readonly AwardService awards;
    private readonly TaskService tasks;
    private readonly InstanceService instances;
    private readonly SceneService scene;
    private readonly WorldData worldData;
    private readonly MonsterService monsters;
    private readonly CombatService combat;
    private readonly BuffService buffs;
    private readonly SkillService skills;
    private readonly World world;
    private readonly ServerOptions options;
    private readonly SessionRegistry sessions;
    private readonly TeamService teams;
    private readonly LifeJobService life;
    private readonly GameLoop loop;
    private readonly PraiseService praise;
    private readonly ILogger<TimedBattleService> log;
    private readonly Dictionary<int, List<GameSession>> queue = new();
    private readonly Dictionary<GameSession, Match> preparing = new();
    private readonly Dictionary<GameSession, Match> active = new();
    private readonly Dictionary<long, Match> matches = new();
    private readonly HashSet<long> pendingDeliveryRoles = new();
    private long lastSecond = -1;

    public sealed class State
    {
        /// <summary>RestrictConfig 1020 (daily): the extra awards taken on <see cref="OnePieceExtraDay"/>.</summary>
        public long OnePieceExtraDay { get; set; }
        public int OnePieceExtraUsed { get; set; }
        /// <summary>The week's extra-award items (AwardRestricConfig caps are weekly) for <see cref="OnePieceWeek"/>.</summary>
        public long OnePieceWeek { get; set; }
        public Dictionary<int, int> OnePieceWeekItems { get; set; } = new();
        /// <summary>Legacy per-day clear and item counters (before the 1020 / weekly limits): read, dropped on the next Rift pay.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<long, int>? OnePieceGains { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<long, Dictionary<int, int>>? OnePieceItemGains { get; set; }
        public HashSet<long> ChickenSettled { get; set; } = new();
        public int ChickenRank { get; set; } = 1;
        public long ChickenScore { get; set; }
        public int ChickenMatches { get; set; }
        public HashSet<long> DragonSettled { get; set; } = new();
        public HashSet<int> DragonRankClaimed { get; set; } = new();
        public int DragonRank { get; set; } = 1;
        public long DragonScore { get; set; }
        public long DragonRankScore { get; set; }
        public long DragonCurrencyDay { get; set; }
        public int DragonCurrencyEarned { get; set; }
        public Dictionary<long, long> OnePieceBestMilliseconds { get; set; } = new();
        /// <summary>Mock Battle (Leisure Forty, CasualBattleService): rating (0 = not yet rated), recent results, the
        /// best and lifetime numbers (5070 DBCasualBattleInfo) and the settled matches (by room).</summary>
        public int CasualMmr { get; set; }
        public List<CasualRecord> CasualRecords { get; set; } = new();
        public CasualStats CasualBest { get; set; } = new();
        public CasualStats CasualTotal { get; set; } = new();
        public HashSet<long> CasualSettled { get; set; } = new();
    }

    public sealed class CasualRecord
    {
        public string ResultId { get; set; } = "";
        public long BattleTime { get; set; }
        public int Outcome { get; set; }
    }

    public sealed class CasualStats
    {
        public long PersonalScore { get; set; }
        public long Damage { get; set; }
        public long Kills { get; set; }
        public long Assists { get; set; }
        public long DamageTaken { get; set; }
        public long Healing { get; set; }
    }

    /// <summary>5070 DBCasualBattleInfo of the role's Mock Battle state (LC_DAL_DailyActivity._processCasualBattleInfo).</summary>
    internal static DBCasualBattleInfo CasualInfo(State state, int startMmr)
    {
        static CasualBattleStats Wire(CasualStats s) => new() { PersonalScore = s.PersonalScore, Damage = s.Damage, Kills = s.Kills,
            Assists = s.Assists, DamageTaken = s.DamageTaken, Healing = s.Healing };
        var info = new DBCasualBattleInfo { Mmr = state.CasualMmr == 0 ? startMmr : state.CasualMmr,
            BestRecord = Wire(state.CasualBest), LifetimeTotal = Wire(state.CasualTotal) };
        info.RecentRecords.Add(state.CasualRecords.Select(r => new CasualBattleHistoryRecord
        { ResultId = r.ResultId, BattleTime = r.BattleTime, Outcome = r.Outcome }));
        return info;
    }

    private sealed class Match
    {
        public required int Entry;
        public required int TeamSize;
        public int TeamCount => Players.Length / TeamSize;
        public required GameSession[] Players;
        public required long Open;
        public required long Deadline;
        public InstanceService.Room? Room;
        public HashSet<GameSession> Ready { get; } = new();
        public HashSet<GameSession> Loaded { get; } = new();
        public Dictionary<GameSession, int> Faction { get; } = new();
        public Dictionary<GameSession, Participant> Participants { get; } = new();
        public Dictionary<GameSession, int> Kills { get; } = new();
        public HashSet<GameSession> Fallen { get; } = new();
        public HashSet<GameSession> Absent { get; } = new();
        public bool Settled;
        /// <summary>Survivor Battle's in-scene state (entry 2), from the room's creation.</summary>
        public ChickenRun? Chicken;
        /// <summary>Dragon Coffin Island's score battle (entry 6), from the room's creation (TimedBattleService.DragonIsland).</summary>
        public DragonRun? Dragon;
    }

    private sealed record Participant(Role Role, string Name, int Career, RoleOwner? Owner,
        AttachmentLease? Lease, long? Epoch)
    {
        public long Id => Role.Id;
    }

    private static (long Id, string Name, int Career) Presentation(Match match, GameSession player)
    {
        var original = match.Participants[player];
        return ReferenceEquals(player.Role, original.Role) && ReferenceEquals(player.Owner, original.Owner)
            && ReferenceEquals(player.Attachment, original.Lease)
            && player.Progress is { } progress
            ? (original.Id, player.Role!.Name, progress.Career)
            : (original.Id, original.Name, original.Career);
    }

    /// <summary>TEST AID set by GM "!chicken shape" (not original behaviour): a Survivor Battle lobby size and team size
    /// instead of EatChickenMatchConfig; overrides ServerOptions.ChickenTestPlayers/TeamSize. In memory only.</summary>
    internal (int Players, int TeamSize)? ChickenTestShape { get; set; }
    internal Func<Task>? AfterSettlementAdmissionForTest { get; set; }
    internal Action? SettlementCompletedForTest { get; set; }
    internal int ActiveMatchCountForTest => matches.Count;

    public TimedBattleService(GameData data, TimedTodoSchedule schedule, TimeProvider time, RoleStore roles, AccountStore accounts,
        CharacterService character, FunctionService functions, AwardService awards, TaskService tasks, InstanceService instances, SceneService scene, WorldData worldData,
        MonsterService monsters, CombatService combat, SessionRegistry sessions, TeamService teams, LifeJobService life, GameLoop loop,
        BuffService buffs, SkillService skills, World world, IOptions<ServerOptions> options, ILogger<TimedBattleService> log,
        GmCommands gm, PraiseService praise)
    {
        this.praise = praise;
        gm.Register("rift", "| new | party [min] | <star 1-6> [boss name] (Rift Crisis test aid: status, new round, "
            + "smallest party, or a stone next to you)", RiftGmAsync);
        this.teams = teams; this.life = life;
        life.ProjectedRestrictions.Add(RiftRestriction);
        this.data = data; this.schedule = schedule; this.time = time; this.roles = roles; this.accounts = accounts;
        this.character = character;
        this.functions = functions; this.awards = awards; this.tasks = tasks; this.instances = instances; this.scene = scene; this.worldData = worldData; this.monsters = monsters;
        this.sessions = sessions; this.loop = loop; this.log = log;
        this.combat = combat; this.buffs = buffs; this.skills = skills; this.world = world; this.options = options.Value;
        scene.Loaded.Add(LoadedAsync);
        scene.Leaving.Add(LeavingAsync);
        combat.Killed.Add(OnePieceKilledAsync);
        combat.PlayerDied.Add(ChickenDiedAsync);
        combat.PlayerDied.Add(OnePieceDiedAsync);
        combat.ReviveInfoOwners.Add(OwnsRevive);
        combat.PvpDealt.Add(ChickenDealt);
        combat.Healed.Add(ChickenHealed);
        combat.Dealt.Add(DragonDealt);
        combat.Killed.Add(DragonBossKilledAsync);
        combat.MatchSkillRule = MatchSkillRule;
        character.MatchAttributes = MatchAttributes;
    }

    public async Task LoginAsync(GameSession session)
    {
        if (session.Role == null) return;
        session.TimedBattle = roles.LoadTimedBattle(session.Role.Id) is { } bytes
            ? JsonSerializer.Deserialize<State>(bytes) ?? new State() : new State();
        await DeliverPendingAsync(session);
        await SyncBattleStateAsync(session, login: true);
        await SendOnePieceMapAsync(session);
    }

    private async Task SaveAsync(GameSession session, State next)
    {
        if (session.Role == null) return;
        var tx = roles.NewTransaction();
        tx.RoleBlob(session.Role.Id, "timed_battle", JsonSerializer.SerializeToUtf8Bytes(next));
        await roles.CommitAsync(tx, "timed-battle/state", _ => session.TimedBattle = next);
    }

    private async Task SyncBattleStateAsync(GameSession session, bool login = false)
    {
        if (session.TimedBattle is not { } state) return;
        var chicken = new DBChickenInfo { RankId = state.ChickenRank, ChickenTimes = state.ChickenMatches };
        var dragon = new DBDragonIslandInfo { RankId = state.DragonRank };
        foreach (int id in state.DragonRankClaimed) dragon.RankAwardClaimed[id] = true;
        var onePiece = new DBOnePieceInfo();
        foreach (var (entry, elapsed) in state.OnePieceBestMilliseconds) onePiece.DungeonMinPassTime[entry] = elapsed;
        if (login)
        {
            // The decompiled Lua full handlers pass these maps to the asset layer. An absent protobuf map
            // decodes as nil; a wire-only key seeds it, and the next increment deletes the key.
            var firstChicken = chicken.Clone();
            if (firstChicken.SkillLibrary.Count == 0) firstChicken.SkillLibrary[0] = 0;
            if (firstChicken.CareerLibrary.Count == 0) firstChicken.CareerLibrary[0] = 0;
            if (firstChicken.StatisticsTime.Count == 0) firstChicken.StatisticsTime[0] = 0;
            if (firstChicken.UpdateData.Count == 0) firstChicken.UpdateData[0] = new ChickenUpdateData();
            if (firstChicken.CareerRecords.Count == 0) firstChicken.CareerRecords[0] = new DBChickenCareerRecord();
            if (firstChicken.SeasonStatistic.Count == 0) firstChicken.SeasonStatistic[0] = new ChickenSeasonStatistic();
            if (firstChicken.SoloStatisticsTime.Count == 0) firstChicken.SoloStatisticsTime[0] = 0;
            await CharacterService.SyncAsync(session, 5003, firstChicken);
            await CharacterService.SyncAsync(session, 5003, chicken);
            var firstDragon = dragon.Clone();
            if (firstDragon.RankAwardClaimed.Count == 0) firstDragon.RankAwardClaimed[0] = false;
            if (firstDragon.RecentRecords.Count == 0) firstDragon.RecentRecords[0] = new DragonIslandHistoryRecord();
            if (firstDragon.SeasonStatistic.Count == 0) firstDragon.SeasonStatistic[0] = new DragonIslandSeasonStatistic();
            await CharacterService.SyncAsync(session, 5071, firstDragon);
            await CharacterService.SyncAsync(session, 5071, dragon);
            var firstOnePiece = onePiece.Clone();
            if (firstOnePiece.DungeonMinPassTime.Count == 0) firstOnePiece.DungeonMinPassTime[0] = 0;
            if (firstOnePiece.NpcPunishTime.Count == 0) firstOnePiece.NpcPunishTime[0] = 0;
            await CharacterService.SyncAsync(session, 5038, firstOnePiece);
            await CharacterService.SyncAsync(session, 5038, onePiece);
        }
        else
        {
            await CharacterService.SyncAsync(session, 5003, chicken);
            await CharacterService.SyncAsync(session, 5071, dragon);
            await CharacterService.SyncAsync(session, 5038, onePiece);
        }
        // 5070 Mock Battle profile (CasualBattleService); RelaxGVGStartMMR is the unrated rating.
        await CharacterService.SyncAsync(session, CasualBattleService.Sync, CasualInfo(state, (int)PublicValue("RelaxGVGStartMMR")));
        await SyncRiftRestrictAsync(session);
    }

    /// <summary>The role's timed-battle state after a Mock Battle settlement (CasualBattleService), and its syncs.</summary>
    internal Task CasualSettledAsync(GameSession session) => SyncBattleStateAsync(session);

    private (int Players, int TeamSize) MatchShape(int entry)
    {
        var row = data.Row(entry == ChickenEntry ? "EatChickenMatchConfig" : "DragonIslandMatchConfig", entry);
        int players = row is { } configured ? (int)GameData.Int(configured, "_kStartQuantity") : 0;
        int teamSize = row is { } team ? (int)GameData.Int(team, "_kTeamQuantity") : 0;
        // TEST AID (not original behaviour): ServerOptions.ChickenTestPlayers/ChickenTestTeamSize or GM "!chicken shape"
        // start a Survivor Battle with a small lobby (the config asks for 100 entrants in teams of 5).
        if (entry == ChickenEntry && (ChickenTestShape ?? (options.ChickenTestPlayers, options.ChickenTestTeamSize)) is (> 0 and var testPlayers, > 0 and var testTeam))
            (players, teamSize) = (testPlayers, testTeam);
        return players > teamSize && players <= 1000 && teamSize > 0 && players % teamSize == 0
            ? (players, teamSize) : (0, 0);
    }

    private bool IsQueued(GameSession session) => queue.Values.Any(waiting => waiting.Contains(session));

    private ReservationKey[] CommandKeys(GameSession session, int entry)
    {
        var keys = new List<ReservationKey>
        { ReservationKey.Entity("timed_match_queue", entry.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
        if (session.Role != null)
        { keys.Add(ReservationKey.Role(session.Role.Id)); keys.Add(ReservationKey.Lifecycle(session.Role.Id)); }
        if (queue.TryGetValue(entry, out var waiting))
            keys.AddRange(waiting.Where(player => player.Role != null).Select(player => ReservationKey.Role(player.Role!.Id)));
        if (preparing.TryGetValue(session, out var match))
        {
            keys.Add(ReservationKey.Entity("timed_match", MatchKey(match)));
            keys.AddRange(match.Participants.Values.Select(participant => ReservationKey.Role(participant.Id)));
        }
        return keys.Distinct().OrderBy(key => key.Kind).ThenBy(key => key.Id, StringComparer.Ordinal)
            .ThenBy(key => key.Part, StringComparer.Ordinal).ToArray();
    }

    private static string MatchKey(Match match) => match.Room?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
        ?? $"prepare/{match.Entry}/{match.Open}/{string.Join(',', match.Participants.Values.Select(p => p.Id).Order())}";

    private async Task<LegacyOwnerScope?> EnterCommandAsync(GameSession session, int entry, string origin)
    {
        if (loop.Owners is not { } owners) return null;
        while (true)
        {
            var keys = CommandKeys(session, entry);
            var scope = await owners.EnterAsync(origin, keys);
            if (keys.SequenceEqual(CommandKeys(session, entry)))
            {
                if (session.Owner != null) scope.Context.Pin(session.Owner);
                return scope;
            }
            scope.Dispose();
        }
    }

    /// <summary>A session cannot simultaneously reserve this activity and a multi-dungeon match (inferred).</summary>
    public bool IsMatching(GameSession session) => IsQueued(session) || preparing.ContainsKey(session) || active.ContainsKey(session);
    public Func<GameSession, bool>? OtherMatchBusy { get; set; }

    private int Admission(GameSession session, int entry, bool alreadyQueued = false)
    {
        if (session.Role == null || session.Progress == null || session.TimedBattle == null) return ErrorCodes.Parameter;
        if (!session.ActivitiesReady) return ErrorCodes.FeatureUnavailable;
        if (entry is not (ChickenEntry or DragonEntry) || data.Row("MatchConfig", entry) == null
            || MatchShape(entry).Players == 0) return ErrorCodes.Parameter;
        int todo = entry == ChickenEntry ? 114 : 126;
        if (session.Progress.Level < 49 || !schedule.IsOpen(todo)
            || !functions.IsOpen(session, entry == ChickenEntry ? 65 : 6401)) return ErrorCodes.FeatureUnavailable;
        if (session.SceneLoading || session.InstanceId != 0 || session.Dead || active.ContainsKey(session)
            || pendingDeliveryRoles.Contains(session.Role.Id)
            || preparing.ContainsKey(session) || !alreadyQueued && IsQueued(session)
            || OtherMatchBusy?.Invoke(session) == true) return ErrorCodes.DungeonBusy;
        return 0;
    }

    public async Task StartMatchAsync(GameSession session, StartMatchReq req)
    {
        using var scope = await EnterCommandAsync(session, req.MatchEntryId, "timed battle start matchmaking");
        int result = req.OperateId != 0 || req.MatchData?.MultiDungeon != null ? ErrorCodes.Parameter
            : Admission(session, req.MatchEntryId);
        await session.SendGameAsync(StartMatch, new StartMatchResp
        { Result = result, MatchEntryId = req.MatchEntryId, MatchData = req.MatchData?.Clone(),
            // Same LC_DAL_Match.ResetMatchTimer consumer as dungeon matching: Unix seconds.
            MatchStartTime = result == 0 ? time.GetUtcNow().ToUnixTimeSeconds() : 0 });
        if (result != 0) return;
        int entry = req.MatchEntryId;
        if (!queue.TryGetValue(entry, out var waiting)) queue[entry] = waiting = [];
        waiting.RemoveAll(p => Admission(p, entry, alreadyQueued: true) != 0);
        waiting.Add(session);
        var shape = MatchShape(entry);
        if (waiting.Count < shape.Players) return;
        var players = waiting.Take(shape.Players).ToArray();
        waiting.RemoveRange(0, shape.Players);
        if (waiting.Count == 0) queue.Remove(entry);
        long now = time.GetUtcNow().ToUnixTimeSeconds();
        var match = new Match { Entry = entry, Players = players, TeamSize = shape.TeamSize, Open = now,
            // Survivor Battle's end is set when its battle stage starts (TimedBattleService.Chicken).
            Deadline = entry == ChickenEntry ? long.MaxValue : now + 300 };
        for (int i = 0; i < players.Length; i++)
        {
            var player = players[i];
            match.Faction[player] = entry == DragonEntry ? DragonCamp(i, shape.TeamSize) : i / shape.TeamSize + 1;
            match.Participants[player] = new Participant(player.Role!, player.Role!.Name,
                player.Progress!.Career, player.Owner, player.Attachment, player.Owner?.LifecycleEpoch);
        }
        foreach (var player in players) preparing[player] = match;
        await BroadcastReadyAsync(match);
    }

    public async Task<bool> CancelMatchAsync(GameSession session, CancelMatchReq req)
    {
        using var scope = await EnterCommandAsync(session, req.MatchEntryId, "timed battle cancel matchmaking");
        int result = !session.ActivitiesReady ? ErrorCodes.FeatureUnavailable
            : queue.GetValueOrDefault(req.MatchEntryId)?.Contains(session) == true
            || preparing.TryGetValue(session, out var awaiting) && awaiting.Entry == req.MatchEntryId
            ? 0 : ErrorCodes.Parameter;
        await session.SendGameAsync(CancelMatch, new CancelMatchResp { Result = result, MatchEntryId = req.MatchEntryId });
        if (result != 0) return false;
        if (queue.TryGetValue(req.MatchEntryId, out var waiting) && waiting.Remove(session) && waiting.Count == 0)
            queue.Remove(req.MatchEntryId);
        if (preparing.TryGetValue(session, out var match))
            await CancelReadyAsync(match, true);
        return true;
    }

    private async Task CancelReadyAsync(Match match, bool manual, GameSession? disconnected = null)
    {
        if (!match.Players.Any(preparing.ContainsKey)) return;
        foreach (var player in match.Players) preparing.Remove(player);
        await RoleFanout.EachAsync(match.Players.Where(player => player != disconnected), player =>
            player.SendGameAsync(82639,
                new CancelMatchNotify { MatchEntryId = match.Entry, ManualCancel = manual }));
    }

    /// <summary>
    /// 82643, as DungeonMatchService.FailReadyAsync: the refusing or late player is the penalty member. (inferred) No lockout
    /// (unlock time 0): PublicParamConfig PenaltyTimeChicken [5,10,30,60] and DragonIslandPenaltyTime 90 name no unit and
    /// MatchRefuseConfig no durations, so the Survivor and Dragon panels show no countdown.
    /// </summary>
    private async Task FailReadyAsync(Match match, GameSession failed)
    {
        if (!match.Players.Any(preparing.ContainsKey)) return;
        foreach (var player in match.Players) preparing.Remove(player);
        long failedId = match.Participants[failed].Id;
        var notify = new FailMatchNtf { Result = ErrorCodes.DungeonDeclined, MatchEntryId = match.Entry,
            PenaltyMemberGuid = failedId, FailPlayer = failedId, MatchStartTime = match.Open };
        await RoleFanout.EachAsync(match.Players, player => player.SendGameAsync(DungeonMatchService.FailNotify, notify));
    }

    private async Task BroadcastReadyAsync(Match match)
    {
        var notify = new MatchReadyNotify { MatchEntryId = match.Entry };
        foreach (var player in match.Players)
            notify.ReadyInfos.Add(new MatchRoleReadyInfo { PlayerId = match.Participants[player].Id,
                Status = match.Ready.Contains(player) ? MatchReadyStatus.Ready : MatchReadyStatus.None });
        notify.EndTime = (match.Open + 30) * 1000;
        if (match.Entry == DragonEntry) notify.DragonIslandReadyProgress = new DragonIslandReadyProgress
        { ReadyCount = match.Ready.Count, ExpectedCount = match.Players.Length };
        await RoleFanout.EachAsync(match.Players, player => player.SendGameAsync(ReadyNotify, notify));
    }

    public async Task ReadyAsync(GameSession session, MatchReadyReq req)
    {
        Match? settle = null;
        using (var scope = await EnterCommandAsync(session, req.MatchEntryId, "timed battle ready"))
        {
        var match = preparing.GetValueOrDefault(session);
        int result = !session.ActivitiesReady ? ErrorCodes.FeatureUnavailable
            : match == null || match.Entry != req.MatchEntryId || session.Role?.Id != req.PlayerId
            ? ErrorCodes.Parameter : !req.IsReady ? ErrorCodes.DungeonDeclined : 0;
        await session.SendGameAsync(Ready, new MatchReadyResp
        { Result = result, MatchEntryId = req.MatchEntryId, PlayerId = session.Role?.Id ?? 0, IsReady = req.IsReady });
        if (match == null || result == ErrorCodes.Parameter) return;
        if (result != 0)
        {
            await FailReadyAsync(match, session);
            return;
        }
        match.Ready.Add(session);
        await BroadcastReadyAsync(match);
        if (match.Ready.Count != match.Players.Length) return;
        int sceneEntry = match.Entry == ChickenEntry ? 2240101 : 3010303;
        try { match.Room = instances.Create(sceneEntry, match.Players); }
        catch (Exception e) { log.LogWarning(e, "Timed battle room create failed"); await CancelReadyAsync(match, false); return; }
        foreach (var player in match.Players) preparing.Remove(player);
        matches[match.Room.Id] = match;
        praise.Remember(match.Room.Id, sceneEntry, match.Participants.Values.Select(p => p.Id)); // the result screens' likes
        if (match.Entry == ChickenEntry) match.Chicken = NewRun(match);
        else if (match.Entry == DragonEntry) { match.Open = time.GetUtcNow().ToUnixTimeSeconds(); match.Deadline = match.Open + 300; match.Dragon = NewDragonRun(match); }
        await RoleFanout.EachAsync(match.Players, async player =>
        {
            active[player] = match;
            player.EventFaction = match.Faction[player];
            bool entered = false;
            try { entered = await instances.EnterAsync(match.Room, player, 5); }
            finally
            {
                if (!entered) { active.Remove(player); player.EventFaction = 0; }
            }
        });
        await RoleFanout.EachAsync(match.Players.Where(active.ContainsKey), player =>
            player.SendGameAsync(MatchStartNotify, new StartMatchNotify { MatchEntryId = match.Entry }));
        if (match.Players.Any(p => !active.ContainsKey(p))) settle = match;
        }
        if (settle != null) await SettleMatchAsync(settle);
    }

    /// <summary>Called by the PvP combat extension before applying player-target damage.</summary>
    public bool CanFight(GameSession attacker, GameSession target) => CanParticipate(attacker, target, requireAlive: true);

    private bool CanParticipate(GameSession attacker, GameSession target, bool requireAlive) => active.TryGetValue(attacker, out var a)
        && active.GetValueOrDefault(target) == a && !a.Settled && a.Room is { Closed: false } room
        && attacker.InstanceId == room.Id && target.InstanceId == room.Id
        && a.Loaded.Contains(attacker) && a.Loaded.Contains(target)
        && a.Faction[attacker] != a.Faction[target] && !a.Fallen.Contains(attacker) && !a.Fallen.Contains(target)
        && !a.Absent.Contains(attacker) && !a.Absent.Contains(target)
        && attacker.ActivitiesReady && target.ActivitiesReady
        && !attacker.Dead && (!requireAlive || !target.Dead)
        && (a.Chicken is not { } run || run.Stage == ChickenRules.StageStartGame)
        && (a.Dragon is not { } dragon || dragon.Stage == 1)
        && time.GetUtcNow().ToUnixTimeSeconds() < a.Deadline;

    /// <summary>Actual CombatService-confirmed PvP death; no client request can set a winner.</summary>
    public async Task PlayerKilledAsync(GameSession killer, GameSession victim)
    {
        // Survivor Battle: the death itself (and whether the victim is out) is ChickenDiedAsync's; this is the credit.
        if (active.TryGetValue(killer, out var chicken) && chicken.Chicken != null && active.GetValueOrDefault(victim) == chicken)
        { await ChickenKillAsync(chicken, killer, victim); return; }
        if (!CanParticipate(killer, victim, requireAlive: false)) return;
        var match = active[killer];
        // Dragon Coffin Island is a score battle: a death costs nothing but the kill's points (no elimination).
        if (match.Dragon != null) { await DragonKillAsync(match, killer, victim); return; }
        match.Kills[killer] = match.Kills.GetValueOrDefault(killer) + 1;
        match.Fallen.Add(victim);
        if (RemainingFactions(match).Count <= 1)
            await SettleMatchAsync(match);
    }

    private static HashSet<int> RemainingFactions(Match match) => match.Players
        .Where(p => !match.Absent.Contains(p) && !match.Fallen.Contains(p))
        .Select(p => match.Faction[p]).ToHashSet();

    private Task SettleMatchAsync(Match match) => SettleMatchCoreAsync(match);


    /// <summary>The recipient's own rank-score change in its 82633 (UI_Sys_Chicken_RankPanl_FlashAniCom.lua:76-120).</summary>
    private sealed record ChickenScoreChange(long Added, long Previous, int PreviousRank, int CurrentRank);

    /// <summary>
    /// 82633: LC_SceneCom_ChickenSettlement.lua:198-353 reads the extraData maps (keys enum_ChickenSettlementAttrDefine,
    /// Lua_GameDefine.lua:14610-14693). battleStartTime goes to GetRoundStartTimeStr without a
    /// /1000 (LC_ChickenHelper.lua:3806-3826): seconds. The rescue/revive keys' exact meaning is (inferred).
    /// </summary>
    private ChickenResultNotify ChickenSnapshot(Match match, GameSession recipient, bool actualVictory,
        ChickenScoreChange? score = null)
    {
        var run = match.Chicken;
        var ranks = ChickenRanks(match);
        long start = run is { BattleStartMs: > 0 } ? run.BattleStartMs / 1000 : match.Open;
        long end = Math.Min(time.GetUtcNow().ToUnixTimeMilliseconds(), run is { BattleEndMs: > 0 } ? run.BattleEndMs : long.MaxValue);
        int faction = match.Faction[recipient];
        int first = ranks.MinBy(r => r.Value).Key;
        var mvp = match.Players.Where(p => match.Faction[p] == first)
            .OrderByDescending(p => match.Kills.GetValueOrDefault(p)).ThenByDescending(p => run?.Players[p].Damage ?? 0)
            .ThenBy(p => match.Participants[p].Id).FirstOrDefault();
        long mvpId = actualVictory && mvp != null ? match.Participants[mvp].Id : 0;
        var result = new ChickenResultNotify { HasAborted = !actualVictory, BattleStartTime = start,
            TeamStatistics = new TeamStatisticsInfo { TeamId = faction } };
        result.TeamStatistics.MemberIds.AddRange(match.Players
            .Where(p => match.Faction[p] == faction).Select(p => match.Participants[p].Id));
        result.TeamStatistics.ExtraData[1012001] = new ExtraValueData { LongValue = ranks.GetValueOrDefault(faction) };
        result.TeamStatistics.ExtraData[1012002] = new ExtraValueData { LongValue = mvpId };
        result.SceneStatistics[1015001] = new ExtraValueData { LongValue = match.Players.Length };
        result.SceneStatistics[1015002] = new ExtraValueData { LongValue = match.TeamCount };
        foreach (var player in match.Players)
        {
            var (id, name, career) = Presentation(match, player);
            var role = new RoleStatisticsInfo
            { BaseInfo = new StatisticsRoleBaseInfo { Id = id, Name = name,
                Job = career, CampId = match.Faction[player], TeamId = match.Faction[player] },
                Totalscore = match.Kills.GetValueOrDefault(player) };
            if (run?.Players.GetValueOrDefault(player) is { } me)
            {
                role.TotalDamage = me.Damage; role.TotalBeDamage = me.Injury; role.TotalCure = me.Cure;
                long survived = run.BattleStartMs == 0 ? 0
                    : Math.Max(0, (me.EliminatedMs > 0 && match.Fallen.Contains(player) ? me.EliminatedMs : end) - run.BattleStartMs) / 1000;
                foreach (var (key, value) in new (int, long)[]
                {
                    (1001001, me.Damage), (1001002, me.Injury), (1001003, me.Cure), (1001004, match.Kills.GetValueOrDefault(player)),
                    (1001005, me.Assists), (1001006, me.Deaths), (1001007, 0), (1001008, 0), (1001009, me.Rescued),
                    (1001010, me.Help), (1001011, me.Resurrections), (1001012, me.Revived),
                    (1011001, survived), (1011003, ranks.GetValueOrDefault(match.Faction[player])),
                    (1011004, id == mvpId && mvpId != 0 ? 1 : 0), (1011005, me.Career),
                })
                    role.ExtraData[key] = new ExtraValueData { LongValue = value };
            }
            if (player == recipient && score != null)
                foreach (var (key, value) in new (int, long)[]
                {
                    (1011006, score.Added), (1011007, 0), (1011008, score.Previous), (1011009, 0),
                    (1011010, score.PreviousRank), (1011011, score.CurrentRank),
                })
                    role.ExtraData[key] = new ExtraValueData { LongValue = value };
            result.RoleStatistics[id] = role;
        }
        return result;
    }

    private long ChickenRankFloor(int rank)
    {
        long floor = 0;
        for (int previous = 1; previous < rank; previous++)
            if (data.Row("EatChickenLevelConfig", previous) is { } row && GameData.Int(row, "_iUpgradesClear") == 1)
                floor = checked(floor + GameData.Int(row, "_iPointLimit"));
        return floor;
    }

    private static DungeonResultInfo DragonSnapshot(Match match, GameSession? winner, bool actualVictory, long settledAt)
    {
        var result = new DungeonResultInfo { Result = actualVictory ? 1 : 0,
             WinEntityId = actualVictory ? match.Participants[winner!].Id : 0,
             WinCamp = actualVictory ? match.Faction[winner!] : 0,
            DungeonId = (int)(match.Room?.SceneEntry ?? 0),
            StatisticsTime = settledAt, DungeonStartTimestamp = match.Open * 1000 };
        if (match.Room is { } room)
        {
            var scene = new SceneStatisticsInfo { SceneId = room.Id, DungeonId = (int)room.SceneEntry };
            foreach (var player in match.Players)
            {
                var (id, name, career) = Presentation(match, player);
                long score = match.Dragon?.Score.GetValueOrDefault(player) ?? match.Kills.GetValueOrDefault(player);
                var role = new RoleStatisticsInfo
                { BaseInfo = new StatisticsRoleBaseInfo { Id = id, Name = name,
                    Job = career, CampId = match.Faction[player], TeamId = match.Faction[player] },
                    Totalscore = score };
                // LC_DAL_DragonIsland sorts by extraData TotalScore 1021005, then KillPlayer 1001004.
                role.ExtraData[1021005] = new ExtraValueData { LongValue = score };
                role.ExtraData[1001004] = new ExtraValueData { LongValue = match.Kills.GetValueOrDefault(player) };
                scene.RoleStatistics[id] = role;
            }
            if (match.Dragon is { } run)
                foreach (var (camp, campScore) in run.CampScore)
                    scene.CampStatistics[camp] = new CampStatisticsInfo { CampId = camp,
                        ExtraData = { [1041003] = new ExtraValueData { LongValue = campScore } } }; // CampScore
            if (actualVictory) result.MetaData["mvpPlayerId"] = match.Participants[winner!].Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            result.SceneStatistics[room.Id] = scene;
        }
        return result;
    }

    private long PublicValue(string key) => data.Table("PublicParamConfig").TryGetValue(key, out var row)
        ? (long)(GameData.Number(row) ?? 0) : 0;

    public async Task ClaimDragonAsync(GameSession session, DragonIslandClaimRankRewardReq _)
    {
        long roleId = session.Role?.Id ?? 0;
        int selectedRank = session.TimedBattle?.DragonRank ?? 0;
        using var scope = roleId > 0 && loop.Owners is { } owners
            ? await owners.EnterAsync("timed battle dragon rank claim", [ReservationKey.Role(roleId),
                ReservationKey.Lifecycle(roleId),
                ReservationKey.Entity("timed_battle_rank_claim", $"{roleId}/{selectedRank}")]) : null;
        if (session.Role?.Id != roleId || session.TimedBattle?.DragonRank != selectedRank)
            throw new OperationCanceledException("Dragon rank changed before claim admission.");
        if (session.Owner != null) scope?.Context.Pin(session.Owner);
        int result = !session.ActivitiesReady || session.Role == null || session.TimedBattle == null || session.TimedBattle.DragonSettled.Count == 0
            ? ErrorCodes.FeatureUnavailable : 0;
        int rank = session.TimedBattle?.DragonRank ?? 0;
        if (result == 0 && session.TimedBattle!.DragonRankClaimed.Contains(rank)) result = ErrorCodes.TaskRequirements;
        var row = data.Row("DragonIslandLevelConfig", rank);
        long award = row is { } r ? GameData.Int(r, "_iAward") : 0;
        if (result == 0 && award <= 0) result = ErrorCodes.ActivityReward;
        AwardPlan? plan = null;
        if (result == 0) try { plan = awards.ResolveDirect(session, award); }
            catch (AwardResolutionException e) { log.LogWarning(e, "Dragon rank award {Award} rejected", award); result = ErrorCodes.RewardMissing; }
        if (result == 0 && plan?.Grants.Count == 0) result = ErrorCodes.ActivityReward;
        if (result == 0)
        {
            var next = JsonSerializer.Deserialize<State>(JsonSerializer.SerializeToUtf8Bytes(session.TimedBattle))!;
            next.DragonRankClaimed.Add(rank);
            if (!await character.ApplyAwardAsync(session, plan!, persist: tx =>
                {
                    tx.RoleBlob(session.Role!.Id, "timed_battle", JsonSerializer.SerializeToUtf8Bytes(next));
                    if (plan!.Grants.Any(grant => grant.Entry is not (100 or 101)))
                        tx.WithEventEconomy(EventEconomy.ForRole(data, session, "timed-battle/dragon-rank",
                            $"timed-battle/dragon-rank/{session.Role.Id}/{rank}", [], plan.Grants,
                            new { Rank = rank, Award = award }));
                }, committed: () => session.TimedBattle = next))
                result = ErrorCodes.Unknown;
        }
        if (result == 0) await SyncBattleStateAsync(session);
        await session.SendGameAsync(DragonClaim, new DragonIslandClaimRankRewardRes { ErrorCode = result });
    }

    private async Task LoadedAsync(GameSession session)
    {
        if (active.TryGetValue(session, out var chickenMatch) && chickenMatch.Chicken != null && !chickenMatch.Settled
            && chickenMatch.Room?.Id == session.InstanceId)
        {
            // Every load of the match scene, a reload too: the whole round state and the current stage.
            chickenMatch.Loaded.Add(session);
            await ChickenLoadedAsync(chickenMatch, session);
        }
        else if (active.TryGetValue(session, out var match) && match.Room?.Id == session.InstanceId && match.Loaded.Add(session)
            && match.Dragon != null)
            await DragonLoadedAsync(match, session);
        await OnePieceLoadedAsync(session);
    }

    private async Task LeavingAsync(GameSession session)
    {
        if (active.TryGetValue(session, out var match) && match.Room?.Id == session.InstanceId && !match.Settled)
            await LeaveRoleAsync(session);
        else await OnePieceLeavingAsync(session);
    }

    /// <summary>C/D 15: what the handover cleanup's leave writes - the leave keys and, when the leaver can end its
    /// match, the match settlement keys.</summary>
    internal ReservationKey[] CleanupKeys(GameSession session) =>
        [.. LeaveKeys(session), .. active.TryGetValue(session, out var match) && match is { Settled: false, Room: { } room }
            ? SettlementKeys(match, room.Id) : []];

    private ReservationKey[] LeaveKeys(GameSession session)
    {
        var keys = new List<ReservationKey>();
        if (session.Role != null)
        { keys.Add(ReservationKey.Role(session.Role.Id)); keys.Add(ReservationKey.Lifecycle(session.Role.Id)); }
        foreach (int entry in queue.Keys.Where(entry => queue[entry].Contains(session)))
            keys.AddRange(CommandKeys(session, entry));
        if (preparing.TryGetValue(session, out var preparingMatch))
        {
            keys.Add(ReservationKey.Entity("timed_match", MatchKey(preparingMatch)));
            keys.AddRange(preparingMatch.Participants.Values.Select(p => ReservationKey.Role(p.Id)));
        }
        if (active.TryGetValue(session, out var activeMatch))
        {
            keys.Add(ReservationKey.Entity("timed_match", MatchKey(activeMatch)));
            keys.AddRange(activeMatch.Participants.Values.Select(p => ReservationKey.Role(p.Id)));
        }
        if (treasure.TryGetValue(session, out var run))
            keys.Add(ReservationKey.Entity("timed_onepiece", run.Room.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return keys.Distinct().OrderBy(key => key.Kind).ThenBy(key => key.Id, StringComparer.Ordinal)
            .ThenBy(key => key.Part, StringComparer.Ordinal).ToArray();
    }

    private async Task<LegacyOwnerScope?> EnterLeaveAsync(GameSession session)
    {
        if (loop.Owners is not { } owners) return null;
        while (true)
        {
            var keys = LeaveKeys(session);
            var scope = await owners.EnterAsync("timed battle leave", keys);
            if (keys.SequenceEqual(LeaveKeys(session)))
            {
                if (session.Owner != null) scope.Context.Pin(session.Owner);
                return scope;
            }
            scope.Dispose();
        }
    }

    /// <summary>Called before role logout/connection cleanup; a departed role cannot retain matchmaking or a reward.</summary>
    public async Task LeaveRoleAsync(GameSession session)
    {
        Match? settle = null;
        using (var scope = await EnterLeaveAsync(session))
        {
        foreach (int entry in queue.Keys.ToArray())
            if (queue[entry].Remove(session) && queue[entry].Count == 0) queue.Remove(entry);
        if (preparing.TryGetValue(session, out var waiting)) await CancelReadyAsync(waiting, false, session);
        if (active.TryGetValue(session, out var match) && !match.Settled)
        {
            // Handover clears live role state after cleanup. Retain the final presentation of this
            // participant, never a later role selected on the same connection.
            var original = match.Participants[session];
            if (ReferenceEquals(session.Role, original.Role) && ReferenceEquals(session.Attachment, original.Lease))
                match.Participants[session] = original with
                { Name = session.Role!.Name, Career = session.Progress?.Career ?? original.Career };
            match.Absent.Add(session);
            match.Fallen.Add(session);
            session.EventFaction = 0;
            if (match.Chicken is { } chicken)
            {
                // Survivor Battle: a departure is that player's forfeit; the others play on (inferred). Before the
                // battle stage a lobby left with one team is cancelled rather than won.
                chicken.Players[session].State = RoundRoleStatus.Leave;
                RecordEliminations(match, chicken);
                active.Remove(session);
                await ChickenDetachAsync(match, session);
                if (chicken.Stage < ChickenRules.StageStartGame)
                {
                    if (match.Players.Where(p => !match.Absent.Contains(p)).Select(p => match.Faction[p]).Distinct().Count() < 2)
                        await AbortMatchAsync(match, session);
                }
                else if (RemainingFactions(match).Count <= 1) settle = match;
                else
                {
                    await SyncTeamAsync(match, match.Faction[session]);
                    await BroadcastHudAsync(match);
                }
            }
            // A fully loaded live match treats departure as a forfeit; before both entered, cancel without prizes.
            // Both are inferred small-realm policies; the departed role never receives settlement rewards.
            else if (match.Loaded.Count == match.Players.Length)
            {
                active.Remove(session);
                if (RemainingFactions(match).Count <= 1) settle = match;
            }
            else await AbortMatchAsync(match, session);
        }
        if (treasure.TryGetValue(session, out var run))
        { run.Settled = true; treasure.Remove(session); }
        }
        if (settle != null) await SettleMatchAsync(settle);
    }

    private async Task AbortMatchAsync(Match match, GameSession? departed)
    {
        if (match.Settled) return;
        match.Settled = true;
        foreach (var player in match.Players) { active.Remove(player); player.EventFaction = 0; }
        await RoleFanout.EachAsync(match.Players.Where(player => player != departed), async player =>
        {
            if (match.Entry == ChickenEntry)
            {
                await player.SendGameAsync(ChickenResult, ChickenSnapshot(match, player, false));
                await ChickenDetachAsync(match, player);
            }
            else await player.SendGameAsync(DragonSettle, new DragonIslandSettleNtf { ResultType = 0 });
            if (match.Room != null && (player.InstanceId != match.Room.Id || !await instances.LeaveAsync(player, recoverDeadOnReturn: match.Chicken != null)))
                await instances.AbandonAsync(match.Room, player);
        });
        if (match.Room != null) matches.Remove(match.Room.Id);
    }

    private async Task RunMatchTransitionAsync(Match match, string origin, Func<Task> work)
    {
        var keys = new[] { ReservationKey.Entity("timed_match", MatchKey(match)) }
            .Concat(match.Participants.Values.Select(participant => ReservationKey.Role(participant.Id)))
            .Distinct().ToArray();
        using var scope = loop.Owners is { } owners ? await owners.EnterAsync(origin, keys) : null;
        await work();
    }

    public void Tick(GameTime _)
    {
        long now = time.GetUtcNow().ToUnixTimeSeconds();
        if (now == lastSecond) return;
        lastSecond = now;
        foreach (var match in preparing.Values.Distinct().ToArray())
            if (!schedule.IsOpen(match.Entry == ChickenEntry ? 114 : 126) || match.Players.Any(p => p.Role == null))
                loop.PostRuntime(() => RunMatchTransitionAsync(match, "timed match ready timeout",
                    () => CancelReadyAsync(match, false)));
            else if (now >= match.Open + 30)
                loop.PostRuntime(() => RunMatchTransitionAsync(match, "timed match ready timeout",
                    () => match.Players.FirstOrDefault(p => !match.Ready.Contains(p)) is { } late
                        ? FailReadyAsync(match, late) : CancelReadyAsync(match, false)));
        foreach (int entry in queue.Keys.ToArray())
            loop.PostRuntime(async () =>
            {
                var keys = new[] { ReservationKey.Entity("timed_match_queue", entry.ToString(System.Globalization.CultureInfo.InvariantCulture)) }
                    .Concat(queue.GetValueOrDefault(entry)?.Where(p => p.Role != null)
                        .Select(p => ReservationKey.Role(p.Role!.Id)) ?? []).Distinct().ToArray();
                using var scope = loop.Owners is { } owners ? await owners.EnterAsync("timed match queue expiry", keys) : null;
                if (!queue.TryGetValue(entry, out var waiting)) return;
                waiting.RemoveAll(p => Admission(p, entry, alreadyQueued: true) != 0);
                if (waiting.Count == 0) queue.Remove(entry);
            });
        foreach (var match in matches.Values.Distinct().ToArray())
            if (!match.Settled && match.Chicken != null && now < match.Deadline)
                loop.PostRuntime(() => ChickenTickAsync(match)); // its own loading rule: AdvanceStageAsync
            else if (!match.Settled && match.Chicken == null && match.Loaded.Count < match.Players.Length && now >= match.Open + 60)
                loop.PostRuntime(() => RunMatchTransitionAsync(match, "timed match entry timeout",
                    () => AbortMatchAsync(match, null)));
            else if (!match.Settled && now >= match.Deadline) loop.PostRuntime(() => SettleMatchAsync(match));
            else if (!match.Settled && match.Dragon != null)
                loop.PostRuntime(() => RunMatchTransitionAsync(match, "dragon island timeline", () => DragonTickAsync(match)));
        TickOnePiece(now);
    }
}
