using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// MG_TerritoryWars/MG_TerritoryAuction: guild qualification, scheduled war instances and durable ownership.
/// client Logic/TerritoryWars, LC_ActivityParser_TerritoryWar and the auction DAL.
/// Reconstructed server choices are marked (inferred) and collected in docs/systems/territory-wars.md.
/// </summary>
public sealed partial class TerritoryWarsService : IGameTicker
{
    public const uint MapQuery = 83001, MapNotify = 83002, GuildNotify = 83003, Camp = 83007, GuildQuery = 83008,
        LegacyBid = 83010, LegacyQuery = 83011, StageQuery = 83012, StageNotify = 83013, History = 83014,
        ResultNotify = 83015, AuctionTimeNotify = 83016, MarkerNotify = 83017, Minimap = 83018,
        FatigueNotify = 83019, QueueNotify = 83020, QueueEnter = 83021, QueueCancel = 83022, QueueArea = 83023,
        SubmitDrop = 83024, Bid = 84031, AuctionQuery = 84032, AutoSet = 84033, AutoCancel = 84034,
        AutoQuery = 84035, AutoNotify = 84036, RankNotify = 84037, WarHall = 84081, ActivitySync = 5013;

    private readonly GameData _data;
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly GuildStore _guildStore;
    private readonly GuildService _guilds;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly TerritoryWarsClock _clock;
    private readonly TimedTodoSchedule _siegeSchedule;
    private readonly SessionRegistry _sessions;
    private readonly InstanceService _instances;
    private readonly SceneService _scene;
    private readonly WorldData _maps;
    private readonly World _world;
    private readonly MonsterService _monsters;
    private readonly BuffService _buffs;
    private readonly RewardService _rewards;
    private readonly MailService _mail;
    private readonly NewActivityService _activities;
    private readonly GameLoop _loop;
    private readonly ILogger<TerritoryWarsService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WorldState _committedState = new();
    private sealed class StateStage(WorldState value, GameLoop.WorkPermit? permit)
    {
        public WorldState Value = value;
        public readonly GameLoop.WorkPermit? Permit = permit;
        public bool Active = true;
    }
    private readonly AsyncLocal<StateStage?> _stagedState = new();
    private bool RoleRequiresRecovery(long roleId) =>
        _loop.Owners?.Cached(roleId)?.State == Ownership.RoleOwnerState.RecoveryRequired;
    private WorldState state
    {
        get => _stagedState.Value is { Active: true } stage
            ? ReferenceEquals(stage.Permit, _loop.CurrentWork) ? stage.Value : Copy(_committedState)
            : _committedState;
        set
        {
            if (_stagedState.Value is { Active: true } stage)
            {
                if (!ReferenceEquals(stage.Permit, _loop.CurrentWork))
                    throw new InvalidOperationException("Territory stage escaped its game-loop work permit.");
                stage.Value = value;
            }
            else _committedState = value;
        }
    }
    private IDisposable StageState()
    {
        var prior = _stagedState.Value;
        var stage = new StateStage(Copy(state), _loop.CurrentWork);
        _stagedState.Value = stage;
        // AsyncLocal assignments made by an awaited child do not flow back to its caller.
        // Establish the mutable fanout batch here, before any child publishes to recipients.
        if (_pendingActivity.Value is not { Active: true } batch || !ReferenceEquals(batch.Permit, _loop.CurrentWork))
            _pendingActivity.Value = new ActivityBatch(_loop.CurrentWork);
        return new StageLease(() => { stage.Active = false; _stagedState.Value = prior; });
    }
    private sealed class StageLease(Action release) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) release();
        }
    }
    private Ownership.ReservationKey[] MutationKeys(GameSession? actor, IReadOnlyCollection<long>? additionalGuilds = null,
        bool includeParticipants = false)
    {
        var keys = new HashSet<Ownership.ReservationKey>
        {
            Ownership.ReservationKey.Entity("realm", nameof(RealmStateKind.TerritoryWars)),
        };
        if (state.Epoch.Length > 0) keys.Add(Ownership.ReservationKey.Entity("territory_season", state.Epoch));
        if (Epoch.Length > 0) keys.Add(Ownership.ReservationKey.Entity("territory_season", Epoch));
        if (_seasonCleanupState is { Epoch.Length: > 0 } retiring)
            keys.Add(Ownership.ReservationKey.Entity("territory_season", retiring.Epoch));
        if (actor?.Role is { } role) keys.Add(Ownership.ReservationKey.Role(role.Id));
        if (includeParticipants)
        {
            foreach (var session in _sessions.All().Where(s => s.Role != null && !RoleRequiresRecovery(s.Role.Id) && s.TerritoryWars != null &&
                (state.Epoch != Epoch || s.TerritoryBattle == null && s.TerritoryWars.Fatigue > 0
                 || _seasonCleanupState != null && (s.TerritoryBattle?.Epoch == _seasonCleanupState.Epoch
                     || s.SiegeBattle?.Epoch == _seasonCleanupState.Epoch))))
                keys.Add(Ownership.ReservationKey.Role(session.Role!.Id));
            foreach (var player in state.Battles.Values.SelectMany(b => b.Players.Keys)
                .Concat(state.SiegeRuns.Values.SelectMany(b => b.Players.Keys))
                .Concat(_seasonCleanupState?.Battles.Values.SelectMany(b => b.Players.Keys) ?? [])
                .Concat(_seasonCleanupState?.SiegeRuns.Values.SelectMany(b => b.Players.Keys) ?? [])
                .Concat(_seasonRoleProjectionPending.Where(id => !RoleRequiresRecovery(id))))
                keys.Add(Ownership.ReservationKey.Role(player));
        }
        foreach (var guild in state.Rounds.Values.SelectMany(r => r.Zones.Values)
            .SelectMany(z => z.Entries).Select(e => e.GuildId)
            .Concat(state.Rounds.Values.SelectMany(r => r.Auto.Keys))
            .Concat(actor == null ? [] : [Own(actor)?.Id ?? 0])
            .Concat(additionalGuilds ?? []).Where(id => id > 0))
            keys.Add(Ownership.ReservationKey.Entity("guild", guild.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return keys.ToArray();
    }
    private async Task<Ownership.LegacyOwnerScope?> EnterGateAsync(string origin, GameSession? actor = null,
        IReadOnlyCollection<long>? additionalGuilds = null, bool includeParticipants = false)
    {
        if (_loop.Owners is not { } owners) { await _gate.WaitAsync(); return null; }
        if (owners.Current is { RuntimeOnly: false })
            return await owners.EnterWithGateAsync(origin, _gate, MutationKeys(actor, additionalGuilds, includeParticipants));
        // One operation across the retries: blocked only by logins' shared season holds, it marks the season so new
        // logins queue behind it instead of starving it.
        using var retry = owners.Retries();
        while (true)
        {
            await _gate.WaitAsync();
            try
            {
                // A preceding pass may finish a season or quarantine one projection while this
                // caller waits. Select participants under the gate, never retain that old key set.
                if (owners.TryEnter(origin, MutationKeys(actor, additionalGuilds, includeParticipants), retry) is { } scope)
                    return scope;
            }
            catch { _gate.Release(); throw; }
            var changed = owners.Reservations.Changed;
            _gate.Release();
            await changed;
        }
    }
    private readonly Dictionary<int, Battle> _battles = new();
    private readonly Dictionary<string, SiegeRun> _siegeBattles = new();
    private readonly Dictionary<GameSession, QueueEntry> _queue = new();
    private TimeSpan _lastTick;
    private bool _ticking, _reconciledOnce; // the first reconciliation runs on the loop's first tick (startup), not 1 s later
    private string _siegePublishedState = "";
    /// <summary>The guild each session was last sent the siege panel for (weak: dies with the session).</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameSession, System.Runtime.CompilerServices.StrongBox<long>> _siegePublishedGuild = new();
    private sealed class ActivityBatch(GameLoop.WorkPermit? permit)
    {
        public readonly GameLoop.WorkPermit? Permit = permit;
        public readonly Dictionary<GameSession, (long RoleId, Ownership.AttachmentLease? Attachment)> Peers = [];
        public bool Active = true;
    }
    private readonly AsyncLocal<ActivityBatch?> _pendingActivity = new();

    public TerritoryWarsService(GameData data, AccountStore accounts, RoleStore roles, GuildStore guildStore,
        GuildService guilds, CharacterService character, FunctionService functions, TerritoryWarsClock clock, TimedTodoSchedule siegeSchedule,
        SessionRegistry sessions, InstanceService instances, SceneService scene, WorldData maps, World world,
        MonsterService monsters, CombatService combat, BuffService buffs, RewardService rewards, MailService mail, NewActivityService activities,
        GameLoop loop, ILogger<TerritoryWarsService> log)
    {
        this._data = data; this._accounts = accounts; this._roles = roles; this._guildStore = guildStore;
        this._guilds = guilds; this._character = character; this._functions = functions; this._clock = clock;
        this._siegeSchedule = siegeSchedule;
        this._sessions = sessions; this._instances = instances; this._scene = scene; this._maps = maps;
        this._world = world; this._monsters = monsters; this._buffs = buffs; this._rewards = rewards; this._mail = mail;
        this._activities = activities;
        activities.TerritoryWarState = BuildActivityState;
        this._loop = loop; this._log = log;
        state = accounts.ReadSync<byte[]?>(new TerritoryRealmReadPlan(RealmStateKind.TerritoryWars)) is { } saved
            ? JsonSerializer.Deserialize<WorldState>(saved)! : new();
        scene.Loaded.Add(LoadedAsync);
        scene.Loaded.Add(SiegeLoadedAsync);
        scene.Loaded.Add(WorldLoadedAsync);
        scene.LoadedKeys.Add(SceneKeys);
        scene.Leaving.Add(LeavingAsync);
        scene.Leaving.Add(SiegeLeavingAsync);
        combat.Killed.Add(KilledAsync);
        combat.Killed.Add(SiegeKilledAsync);
        combat.Killed.Add(WorldKilledAsync);
        combat.PlayerDied.Add(DiedAsync);
        // DiedAsync publishes the battle's own countdown for a recorded participant.
        combat.ReviveInfoOwners.Add(s => s.TerritoryBattle is { } battle && s.Role is { } role && battle.State.Players.ContainsKey(role.Id));
        combat.PlayerDied.Add(WorldDiedAsync);
        combat.EventDamageRecorded.Add(DamageRecordedAsync);
        combat.EventDamageRecorded.Add(SiegeDamageRecordedAsync);
        combat.PlayerHostile = Hostile;
        combat.MonsterTargetAllowed = CanHitObjective;
        combat.BlockedMonsterEndpointAllowed = SiegeCrystalBlockedEndpointAllowed;
        combat.PvpDealtAsync.Add(PvpRecordedAsync);
        combat.PvpDealtAsync.Add(SiegePvpRecordedAsync);
        combat.HealedAsync.Add(BattleHealRecordedAsync);
        combat.HealedAsync.Add(SiegeHealRecordedAsync);
    }

    public sealed class RoleState
    {
        public string Epoch { get; set; } = "";
        public Dictionary<string, RoleSeason> Seasons { get; set; } = new();
        public int Camp { get; set; } = 1;
        public long LastCampChange { get; set; }
        public long Honor { get; set; }
        public long Valor { get; set; }
        public int Fatigue { get; set; }
        public int FatigueKills { get; set; }
        public long LastFatigue { get; set; }
        public long Donated { get; set; }
        public Dictionary<long, long> Donations { get; set; } = new();
        public Dictionary<string, int> WorldLandClaims { get; set; } = new();
        public Dictionary<string, List<Grant>> Pending { get; set; } = new();
        public HashSet<string> Paid { get; set; } = new();
    }
    public sealed record Grant(int Entry, int Count, int Bind = 0);
    public sealed record RoleSeason(long Honor, long Valor, long Donated, Dictionary<long, long> Donations);
    public sealed class WorldState
    {
        public string Epoch { get; set; } = "";
        public int LastStage { get; set; }
        public Dictionary<int, long> Owners { get; set; } = new();
        public Dictionary<int, AuctionRound> Rounds { get; set; } = new();
        public Dictionary<int, BattleState> Battles { get; set; } = new();
        public Dictionary<int, List<byte[]>> Results { get; set; } = new();
        public Dictionary<int, List<byte[]>> SiegeHistory { get; set; } = new();
        public Dictionary<string, SiegeRunState> SiegeRuns { get; set; } = new();
        public Dictionary<long, long> Donations { get; set; } = new();
        public Dictionary<int, List<long>> RuleResults { get; set; } = new();
        public Dictionary<long, WorldDrop> WorldDrops { get; set; } = new();
        public long NextWorldDropId { get; set; } = 1;
        public Dictionary<int, long> WorldActivityRespawns { get; set; } = new();
        public Dictionary<int, long> WorldActivityOccurrences { get; set; } = new();
        public Dictionary<string, WorldPickupOccurrence> WorldPickupOccurrences { get; set; } = new();
    }
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value))!;
    private async Task SaveAsync(WorldState value, string kind = "state", GameSession? actor = null,
        string? sourceKey = null)
    {
        var transaction = _roles.NewTransaction();
        Save(value, transaction);
        if (WarsEconomy(_committedState, value, [], [], kind, sourceKey ?? "state-only", actor) is { } economy)
        {
            if (sourceKey == null) throw new InvalidOperationException("Territory economic state save needs a natural source key.");
            if (actor?.Role is { } role) transaction.PinRole(role.Id);
            transaction.WithEventEconomy(economy);
        }
        await _roles.CommitAsync(transaction, "territory/save");
        _committedState = Copy(value);
    }
    private async Task CommitAsync(WorldState next, IEnumerable<GuildStore.State>? changed = null,
        string kind = "transition", GameSession? actor = null, string? sourceKey = null)
    {
        var transaction = _roles.NewTransaction();
        var changedGuilds = (changed ?? []).DistinctBy(g => g.Id).ToArray();
        var oldGuilds = changedGuilds.Select(g => _guildStore.Get(g.Id))
            .Where(g => g != null).Cast<GuildStore.State>().ToArray();
        foreach (var g in changedGuilds) _guildStore.EnlistSave(g, transaction, "Guild changed during territory transaction");
        Save(next, transaction);
        if (WarsEconomy(_committedState, next, oldGuilds, changedGuilds, kind, sourceKey ?? "state-only", actor) is { } economy)
        {
            if (sourceKey == null) throw new InvalidOperationException("Territory economic transition needs a natural source key.");
            if (actor?.Role is { } role) transaction.PinRole(role.Id);
            transaction.WithEventEconomy(economy);
        }
        await _roles.CommitAsync(transaction, "territory/commit");
        PublishCommitted(next);
    }
    private void PublishCommitted(WorldState value)
    {
        _committedState = Copy(value);
        state = value;
    }
    private string Epoch => _clock.Epoch;
    private RoleState LoadRole(long id)
    {
        var value = _roles.LoadTerritoryWars(id) is { } bytes ? JsonSerializer.Deserialize<RoleState>(bytes)! : new();
        if (value.Epoch == Epoch) return value;
        if (value.Epoch.Length > 0) value.Seasons[value.Epoch] = new(value.Honor, value.Valor, value.Donated, value.Donations);
        var season = value.Seasons.GetValueOrDefault(Epoch);
        value.Epoch = Epoch;
        value.Honor = season?.Honor ?? 0; value.Valor = season?.Valor ?? 0; value.Donated = season?.Donated ?? 0;
        value.Donations = season?.Donations ?? new();
        value.Fatigue = 0; value.FatigueKills = 0; value.LastFatigue = _clock.Now;
        return value;
    }
    private async Task SaveRoleAsync(long id, RoleState value)
    {
        var transaction = _roles.NewTransaction();
        SaveRole(id, value, transaction);
        await _roles.CommitAsync(transaction, "territory/role");
    }
    private async Task<long> AllocateStatisticsIdAsync()
    {
        var transaction = _accounts.NewTransaction();
        int index = transaction.Add(new RoleIdentityWritePlan(RoleIdentityWriteKind.AllocateInstance));
        var receipts = await transaction.CommitAsync(_accounts, "territory/statistics-id");
        return (long)receipts[index]!;
    }
    private static void SaveRole(long id, RoleState value, RoleItemPlanBuilder tx) =>
        tx.RoleBlob(id, "territory_wars", JsonSerializer.SerializeToUtf8Bytes(value));
    private static void Save(WorldState value, RoleItemPlanBuilder tx) =>
        tx.Add(new RealmStatePlan(RealmStateKind.TerritoryWars, JsonSerializer.SerializeToUtf8Bytes(value)));
    private GuildStore.State? Own(GameSession s) => s.Role == null ? null : _guildStore.ForRole(s.Role.Id);
    /// <summary>
    /// The role's guild id for per-hit checks. GuildService keeps <see cref="GameSession.Guild"/> current on login,
    /// join, leave and kick; <see cref="Own"/> joins and deserializes the whole guild row, which combat called
    /// ~90 times per player per second in a siege.
    /// </summary>
    private long OwnId(GameSession s) => s.Role == null ? 0 : s.Guild is { } g ? g.GuildId : Own(s)?.Id ?? 0;
    private static bool Right(GuildStore.State g, long role, int right) => g.Members.TryGetValue(role, out var m)
        && (m.Official == 1 || g.Rights.GetValueOrDefault(right)?.Contains(m.Official) == true);
    private long Param(string name, long fallback = 0) => _data.Table("PublicParamConfig").TryGetValue(name, out var v) ? (long)(GameData.Number(v) ?? fallback) : fallback;
    private long Gvg(string name, long fallback) => _data.Table("GVGConfig").TryGetValue(name, out var v) ? (long)(GameData.Number(v) ?? fallback) : fallback;
    private long[] ParamArray(string name) => _data.Table("PublicParamConfig").TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Array
        ? v.EnumerateArray().Select(x => (long)(GameData.Number(x) ?? 0)).ToArray() : [];
    private IEnumerable<JsonElement> Zones => _data.Table("GVGWarZoneConfig").Values;
    private JsonElement? Zone(int id) => _data.Row("GVGWarZoneConfig", id);
    private int Parent(int id) => Zone(id) is { } row ? (int)GameData.Int(row, "_iBelongID") : 0;
    private int ZoneType(int id) => Zone(id) is { } row ? (int)GameData.Int(row, "_iType") : 0;
    public long OwnerOf(int id) => state.Owners.GetValueOrDefault(id);
    private void AssignTerritoryOwner(WorldState next, int zone, long winner)
    {
        if (zone <= 0 || winner <= 0) return;
        next.Owners[zone] = winner;
        // Connecting lands inherit their capital; independent strongholds retain their own owners.
        if (ZoneType(zone) == 2)
            foreach (var z in Zones.Where(z => GameData.Int(z, "_iType") == 4 && Parent((int)Id(z)) == zone))
                next.Owners[(int)Id(z)] = winner;
    }
    public int GuildPoint(long id) => state.Owners.Where(x => x.Value == id && ZoneType(x.Key) == 3).Select(x => x.Key).Order().FirstOrDefault();
    public Func<long, int, long> GuildEscortScore { get; set; } = (_, _) => 0;
    private int RootOf(int id) { while (Parent(id) != 0) id = Parent(id); return id; }
    private static long Id(JsonElement row) => GameData.Int(row, "_iID");
    private async Task Send(GameSession s, uint id, IMessage msg)
    {
        try { await s.SendGameAsync(id, msg); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
        { _log.LogDebug(e, "Territory notification {Id} to closed role {Role}", id, s.Role?.Id); }
    }
    private Task Error(GameSession s, uint request, int error) => s.SendGameAsync(2,
        new CommonErrorNtf { ErrorCode = error });

    /// <summary>Exact territory owner request set shared with dispatcher admission.</summary>
    internal static bool IsOwnerRequest(uint id) => id is
        MapQuery or MapNotify or GuildNotify or Camp or GuildQuery or LegacyBid or LegacyQuery or StageQuery
            or StageNotify or History or ResultNotify or AuctionTimeNotify or MarkerNotify or Minimap or FatigueNotify
            or QueueNotify or QueueEnter or QueueCancel or QueueArea or SubmitDrop or Bid or AuctionQuery or AutoSet
            or AutoCancel or AutoQuery or AutoNotify or RankNotify or WarHall or SiegeBaseQuery or SiegeHistoryQuery or SiegeResultNotify
            or SiegeToolAssign or SiegeToolExchange or SiegeToolList or SiegeToolPermissionNotify or SiegeToolChangeNotify
            or SiegeToolPrepay or SiegeToolBuild or SiegeToolCancel or SiegeToolAdd or SiegeVehicleUse;

    /// <summary>Route only shared packets that this territory family can actually consume.</summary>
    internal bool IsSharedOwnerRequest(GameSession session, uint id, ReadOnlyMemory<byte> body)
    {
        if (id == SceneService.NpcInteraction)
        {
            long uid = NpcInteractionReq.Parser.ParseFrom(body.Span).NpcUid;
            return uid >= WorldPickupUidBase && uid < WorldPickupUidEnd;
        }
        if (id == 30400)
        {
            var request = EnterDungeonReq.Parser.ParseFrom(body.Span);
            return request.SceneEntry == SiegeScene || request.Param?.Territory != null ||
                Zones.Any(zone => GameData.Int(zone, "_iDungeonID") == request.SceneEntry);
        }
        if (id == 30114)
        {
            var ids = DungeonStatisticsInfoWithDungeonReq.Parser.ParseFrom(body.Span).StaisticsIds;
            return ids.Any(stat => state.Battles.Values.Any(b => b.StatisticsId == stat)
                || state.SiegeRuns.Values.Any(b => b.StatisticsId == stat));
        }
        return (id is 30106 or 30110 or 30116 or 30117) &&
            (session.TerritoryBattle != null || session.SiegeBattle != null);
    }

    /// <summary>Only declared requests mutate state; inbound notification IDs cannot author results or ownership.</summary>
    public async Task<bool> HandleAsync(GameSession s, uint id, ReadOnlyMemory<byte> body)
    {
        if (!IsOwnerRequest(id)) return false;
        if (s.Role == null) return true;
        using var reservation = await EnterGateAsync("territory/request", s);
        using var stagedTerritoryState = StageState();
        try
        {
            if (_seasonCleanupState != null)
                throw new InvalidOperationException("Territory season runtime cleanup is pending.");
            switch (id)
            {
                case MapQuery: await Send(s, id, new TerritoryWarsGetMapDataResp { Data = MapData(TerritoryWarsGetMapDataReq.Parser.ParseFrom(body.Span)) }); break;
                case GuildQuery: await Send(s, id, new TerritoryWarsGetGuildDataResp { GuildData = GuildData(Own(s)?.Id ?? 0) }); break;
                case StageQuery: await Send(s, id, StageData()); break;
                case Camp: await SetCampAsync(s, PvpSetRoleCampReq.Parser.ParseFrom(body.Span)); break;
                case LegacyBid: await LegacyBidAsync(s, TerritoryWarsAuctionReq.Parser.ParseFrom(body.Span)); break;
                case LegacyQuery: await LegacyQueryAsync(s, GetTerritoryWarsAuctionInfoReq.Parser.ParseFrom(body.Span)); break;
                case Bid: await BidAsync(s, TerritoryAuctionBidReq.Parser.ParseFrom(body.Span)); break;
                case AuctionQuery: await SendRanksAsync(s); break;
                case AutoSet: await AutoSetAsync(s, TerritoryAutoBidSetReq.Parser.ParseFrom(body.Span)); break;
                case AutoCancel: await AutoCancelAsync(s, TerritoryAutoBidCancelReq.Parser.ParseFrom(body.Span)); break;
                case AutoQuery: await SendAutoAsync(s); break;
                case History: await HistoryAsync(s, GetTerritoryWarsHistoryStatisticsReq.Parser.ParseFrom(body.Span)); break;
                case Minimap: s.TerritoryMinimapOpen = TerritoryWarsMapOpenMinimapStatus.Parser.ParseFrom(body.Span).IsOpen; if (s.TerritoryMinimapOpen) await SendMarkersAsync(s); break;
                case QueueCancel: await CancelQueueAsync(s, TerritoryWarQueueExitReason.Cancel); await Send(s, id, new TerritoryWarQueueCancelRes()); break;
                case SubmitDrop: await SubmitDropAsync(s); break;
                case WarHall: await WarHallAsync(s); break;
                case SiegeBaseQuery: await SiegeBaseAsync(s, GetSiegeBaseInfoReq.Parser.ParseFrom(body.Span)); break;
                case SiegeHistoryQuery: await SiegeHistoryAsync(s, SiegeCityHistoryInfoReq.Parser.ParseFrom(body.Span)); break;
                case SiegeToolList: await SiegeToolListAsync(s); break;
                case SiegeToolAssign: await SiegeToolAssignAsync(s, GuildWartimeToolBelongOperReq.Parser.ParseFrom(body.Span)); break;
                case SiegeToolExchange: await SiegeToolExchangeAsync(s, GuildWartimeToolExchangeOperReq.Parser.ParseFrom(body.Span)); break;
                case SiegeToolPrepay: await Send(s, id, new GuildWartimeStorePrepayResp { Result = ErrorCodes.FeatureUnavailable }); break;
                case SiegeToolBuild: await Send(s, id, new GuildAttackCityBuildResp { Result = ErrorCodes.FeatureUnavailable }); break;
                case SiegeToolCancel: await Send(s, id, new GuildWartimeStoreItemCancelUseResp { Result = ErrorCodes.FeatureUnavailable }); break;
                case SiegeToolAdd: await Send(s, id, new GuildWartimeStoreItemAddResp { Result = ErrorCodes.FeatureUnavailable }); break;
                case SiegeVehicleUse: await Send(s, id, new GuildVehicleUseRsp { Result = ErrorCodes.FeatureUnavailable }); break;
            }
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
        await FlushActivityAsync();
        return true;
    }

    /// <summary>C/D 15: the current season entity the login's own projection alignment needs, reserved up front by
    /// the role select.</summary>
    internal Ownership.ReservationKey[] LoginKeys() =>
        Epoch.Length > 0 ? [Ownership.ReservationKey.SharedEntity("territory_season", Epoch)] : [];

    public async Task LoginAsync(GameSession s)
    {
        s.TerritoryBattle = null;
        s.SiegeBattle = null;
        s.TerritoryMinimapOpen = false;
        // C/D 15: no realm reconciliation inside the login (it reserved Realm-wide territory state and every
        // participant). The one-second Tick runs it from startup; a stage change it publishes reaches this session
        // once its TerritoryWars projection is set below.
        if (s.Role is not { } role) return;
        long roleId = role.Id;
        var attachment = s.Attachment;
        using (var scope = _loop.Owners is { } owners
            ? await owners.EnterAsync("territory/login season role", Ownership.ReservationKey.Role(roleId),
                // Shared: logins only align their own projection; a season change holds the season exclusively.
                Ownership.ReservationKey.SharedEntity("territory_season", Epoch)) : null)
        {
            if (s.Role?.Id != roleId || !ReferenceEquals(s.Attachment, attachment)) return;
            var saved = _roles.LoadTerritoryWars(roleId);
            var aligned = LoadRole(roleId);
            var savedEpoch = saved is { Length: > 0 } ? JsonSerializer.Deserialize<RoleState>(saved)?.Epoch : null;
            if (savedEpoch != aligned.Epoch) await SaveRoleAsync(roleId, aligned);
            if (s.Role?.Id != roleId || !ReferenceEquals(s.Attachment, attachment)) return;
            s.TerritoryWars = aligned;
            _seasonRoleProjectionPending.Remove(roleId);
        }
        // Epoch alignment just committed this role projection. An empty outbox needs no reload
        // after that write invalidates the login snapshot; nonempty delivery still checks its source.
        if (s.TerritoryWars!.Pending.Count != 0) await DeliverAsync(s);
        await Send(s, StageNotify, StageData());
        await Send(s, MapNotify, new TerritoryWarsMapDataNotify { Data = MapData() });
        await Send(s, GuildNotify, new TerritoryWarsNotifySiegeCampChange { GuildData = GuildData(Own(s)?.Id ?? 0) });
        await SyncActivityAsync(s);
        await SyncSiegeAsync(s, committedRole: s.TerritoryWars);
        await Send(s, SiegeBaseQuery, new GetSiegeBaseInfoRsp { SiegeBaseInfo = SiegeBase(s) });
    }

    private GetTerritoryWarsStageResp StageData()
    {
        var stage = _clock.Current;
        var value = stage.Type switch
        {
            4 or 8 or 12 => TerritoryWarsBattleState.OnAuction,
            16 or 17 or 18 => TerritoryWarsBattleState.OnLastAuction,
            5 or 9 or 13 => TerritoryWarsBattleState.AuctionEnd,
            6 or 10 or 14 => TerritoryWarsBattleState.Fighting,
            15 or 99 => TerritoryWarsBattleState.End,
            _ => TerritoryWarsBattleState.NotOpen,
        };
        // Seconds: the legacy auction UI subtracts GetSystemTime directly, as activity 5013 does (BuildActivityState).
        return new() { State = value, StartTime = stage.StartTime, EndTime = stage.EndTime };
    }

    /// <summary>83001 selective hierarchy. Owners are guild identities; the neutral owner remains zero.</summary>
    public TerritoryWarsMapData MapData(TerritoryWarsGetMapDataReq? req = null)
    {
        bool all = req == null || req.ZoneIds.Count + req.StateIds.Count + req.PointIds.Count == 0;
        var result = new TerritoryWarsMapData();
        foreach (var row in Zones)
        {
            int id = (int)Id(row), type = (int)GameData.Int(row, "_iType"), parent = Parent(id), root = RootOf(id);
            long owner = OwnerOf(id);
            var snap = owner > 0 && _guildStore.Get(owner) is { } g ? _guilds.Snapshot(g) : null;
            if (type == 1 && (all || req!.ZoneIds.Contains(id)))
            {
                var z = new TerritoryWarsZoneMapData { ZoneId = id, StrategyGuildId = owner, StrategyGuildInfo = snap };
                z.StateIds.AddRange(Zones.Where(x => GameData.Int(x, "_iType") == 2 && Parent((int)Id(x)) == id).Select(Id));
                z.PointIds.AddRange(Zones.Where(x => GameData.Int(x, "_iType") >= 3 && RootOf((int)Id(x)) == id).Select(Id));
                result.ZoneDatas[id] = z;
            }
            if (type == 2 && (all || req!.StateIds.Contains(id) || req.ZoneIds.Contains(root)))
            {
                var z = new TerritoryWarsStateMapData { StateId = id, ZoneId = root, StrategyGuildId = owner, StrategyGuildInfo = snap };
                z.PointIds.AddRange(Zones.Where(x => Parent((int)Id(x)) == id).Select(Id));
                result.StateDatas[id] = z;
            }
            if (type >= 3 && (all || req!.PointIds.Contains(id) || req.StateIds.Contains(parent) || req.ZoneIds.Contains(root)))
                result.PointDatas[id] = new TerritoryWarsPointMapData { PointId = id, ZoneId = root, StateId = parent, StrategyGuildId = owner, StrategyGuildInfo = snap };
        }
        return result;
    }

    private TerritoryWarsGuildData GuildData(long guildId)
    {
        var g = new TerritoryWarsGuildData();
        foreach (var (id, owner) in state.Owners.Where(x => x.Value == guildId && guildId > 0)) g.OccupyWarzone[id] = true;
        var round = Round();
        if (round != null)
        {
            var z = round.Zones.Values.FirstOrDefault(z => z.Entries.Any(e => e.GuildId == guildId));
            if (z != null) { g.ZoneId = RootOf(z.Id); g.AuctionStateId = z.Id; g.AuctionFinishTime = round.FinalEnd * 1000; }
            var score = state.Battles.Values.Where(b => b.Round == round.Round).Sum(b => b.Scores.GetValueOrDefault(guildId));
            g.GuildScore = score;
        }
        return g;
    }

    /// <summary>5013 activity 2000 is the current UI's source of stageType and qualified/owned GVG zones.</summary>
    private Task SyncActivityAsync(GameSession s) => _activities.ReconcileAsync(s);
    private void QueueActivity(GameSession s)
    {
        var batch = _pendingActivity.Value;
        if (batch is not { Active: true } || !ReferenceEquals(batch.Permit, _loop.CurrentWork))
            throw new InvalidOperationException("Territory activity fanout lacks its command batch.");
        if (s.Role is { } role) batch.Peers[s] = (role.Id, s.Attachment);
    }
    private async Task FlushActivityAsync()
    {
        var batch = _pendingActivity.Value;
        if (batch is not { Active: true } || !ReferenceEquals(batch.Permit, _loop.CurrentWork)) return;
        _pendingActivity.Value = null;
        batch.Active = false;
        bool Current(GameSession peer)
        {
            var recipient = batch.Peers[peer];
            return peer.Role?.Id == recipient.RoleId && ReferenceEquals(peer.Attachment, recipient.Attachment)
                && peer.TerritoryWars != null && !RoleRequiresRecovery(recipient.RoleId);
        }
        await RoleFanout.EachAsync(batch.Peers.Keys.Where(Current), async peer =>
        {
            if (!Current(peer)) return;
            try { await SyncActivityAsync(peer); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
            { _log.LogDebug(e, "Territory activity delivery deferred for disconnected role {Role}", peer.Role?.Id); }
        });
    }

    /// <summary>
    /// territoryWarStage times are epoch seconds: LC_ActivityParser_TerritoryWar.lua:316-337 stores them raw as
    /// lStartTime/lEndTime, and every reader compares them with TimerManager:GetSystemTime(), which is seconds
    /// (Lua_TimerManager.lua:710-732 stores the server's ms * 0.001): LC_TerritoryWarActivityHelper.lua:300-322
    /// (remaining time), UI_Sys_TerritoryWars_BasicsMapInformationWidgetCtrl.lua:207-226 (start &lt;= now &lt; end),
    /// UI_Sys_TerritoryWars_StrongholdAuctionPanelCtrl.lua:1220-1235; LC_TerritoryWarActivityHelper_Test.lua formats
    /// them with os.date and defaults the end to start + 3600. territoryWarData.stage has no client reader and
    /// follows the same unit (inferred).
    /// </summary>
    private PlayerActivityTerritoryWar BuildActivityState(GameSession s)
    {
        var stage = _clock.Current;
        var war = new DBTerritoryWarData
        {
            ZoneData = new(), BattleZoneData = new(),
            Stage = new TerritoryStage { StageType = stage.Type, StartTime = stage.StartTime, EndTime = stage.EndTime }
        };
        foreach (var z in Zones)
        {
            int id = (int)Id(z);
            var owned = new TerritoryZoneInfo();
            if (OwnerOf(id) > 0) owned.GuildId.Add(OwnerOf(id));
            war.ZoneData.ZoneInfo[id] = owned;
            var qualified = new TerritoryZoneInfo();
            qualified.GuildId.AddRange(QualifiedFor(id));
            war.BattleZoneData.ZoneInfo[id] = qualified;
        }
        return new PlayerActivityTerritoryWar
        {
            TerritoryWarData = war, TerritoryWarStage = new TerritoryWarStage
            {
                StageType = (TerritoryWarStageType)stage.Type,
                StartTime = stage.StartTime, EndTime = stage.EndTime
            },
        };
    }

    private async Task PublishAsync()
    {
        var map = new TerritoryWarsMapDataNotify { Data = MapData() };
        await RoleFanout.EachAsync(_sessions.All().Where(s => s.Role != null && s.TerritoryWars != null), async s =>
        {
            try
            {
                await Send(s, MapNotify, map);
                await Send(s, GuildNotify, new TerritoryWarsNotifySiegeCampChange { GuildData = GuildData(Own(s)?.Id ?? 0) });
                QueueActivity(s);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
            { _log.LogDebug(e, "Territory map delivery deferred for disconnected role {Role}", s.Role?.Id); }
        });
    }

    private async Task SetCampAsync(GameSession s, PvpSetRoleCampReq req)
    {
        var role = LoadRole(s.Role!.Id);
        long remain = Math.Max(0, Param("TerritoryAttackClearCD", 120) - (_clock.Now - s.LastCombatMs / 1000));
        int error = (int)req.Type is < 1 or > 3 ? ErrorCodes.Parameter : s.TerritoryBattle != null ? ErrorCodes.TerritoryCampFixed
            : req.Type == EPvpRoleCampNum.Guild && Own(s) == null ? ErrorCodes.TerritoryCampGuild
            : _clock.Now < role.LastCampChange + Param("TerritoryChangeCampCD", 10) ? ErrorCodes.TerritoryCampCooldown
            : remain > 0 ? ErrorCodes.TerritoryCampCombat : 0;
        if (error == 0) { role.Camp = (int)req.Type; role.LastCampChange = _clock.Now; await SaveRoleAsync(s.Role!.Id, role); s.TerritoryWars = role; }
        await Send(s, Camp, new PvpSetRoleCampResp { Result = error, Type = (EPvpRoleCampNum)role.Camp, PeaceTimeInterval = remain });
    }

    private Task HistoryAsync(GameSession s, GetTerritoryWarsHistoryStatisticsReq req)
    {
        var history = new TerritoryWarsHistoryStatistics();
        foreach (var (zone, entries) in state.Results.Where(x => req.StateId.Count == 0 || req.StateId.Contains(x.Key)))
        {
            var group = new TerritoryWarsGroupStatistics();
            foreach (var bytes in entries) { var r = DungeonResultInfo.Parser.ParseFrom(bytes); group.StatisticIds[r.StatisticsId] = r.StatisticsTime; }
            history.GroupStatistic[zone] = group;
        }
        return Send(s, History, new GetTerritoryWarsHistoryStatisticsResp { History = history });
    }

    /// <summary>Pending rewards and the paid key commit with inventory, so reconnect cannot duplicate a grant.</summary>
    private async Task DeliverAsync(GameSession s)
    {
        using var ownerScope = s.Owners is { } owners
            ? await owners.EnterAsync("territory pending delivery", Ownership.ReservationKey.Role(s.Role!.Id)) : null;
        using var held = await CharacterService.Lock(s);
        var current = LoadRole(s.Role!.Id);
        foreach (var key in current.Pending.Keys.ToArray())
        {
            // Exchange sends notifications after committing and can yield to scene cleanup. Reload the
            // durable role before every write so a camp/fatigue update cannot resurrect a paid outbox entry.
            current = LoadRole(s.Role.Id);
            if (!current.Pending.TryGetValue(key, out var goods) || current.Paid.Contains(key)) continue;
            var next = Copy(current);
            next.Pending.Remove(key);
            next.Paid.Add(key);
            if (await _character.ExchangeLockedAsync(s, [], [],
                persist: tx =>
                {
                    tx.RoleBlob(s.Role.Id, "territory_wars", JsonSerializer.SerializeToUtf8Bytes(next));
                    if (goods.Any(g => g.Count > 0 && g.Entry is not (100 or 101)))
                        tx.WithEventEconomy(new EventEconomyMutation(EventEconomy.Catalog(_data),
                            Guid.NewGuid().ToString("N"), s.Account!, s.Role.Id, "event/territory-pending-deliver",
                            $"territory-pending-deliver:{s.Role.Id}:{key}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            [], [], [], JsonSerializer.SerializeToUtf8Bytes(new { Role = s.Role.Id, Key = key })));
                },
                boundGoods: goods.Select(g => (g.Entry, g.Count, g.Bind)).ToArray(), asyncCommit: true) == null) continue;
            current = next;
        }
        s.TerritoryWars = LoadRole(s.Role.Id);
    }

    public async Task ReconcileAsync()
    {
        if (_loop.IsWorkerActive && !_loop.IsOnLoop)
        {
            await _loop.InvokeAsync(ReconcileAsync);
            return;
        }
        using var reservation = await EnterGateAsync("territory/reconciliation", includeParticipants: true);
        using var stagedTerritoryState = StageState();
        try { await ReconcileLockedAsync(); }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
        await FlushActivityAsync();
    }
    private async Task ReconcileLockedAsync()
    {
        await ReconcileSeasonAsync();
        if (_seasonCleanupState != null) return;
        await AuctionClockAsync();
        await BattleClockAsync();
        await ReconcileWorldActorsLockedAsync();
        await ApplyBattleResultRulesAsync();
        await QueueClockAsync();
        await ClearFatigueAsync();
        await SiegeClockAsync();
        await PublishSiegeIfChangedAsync();
        if (state.LastStage != _clock.Current.Id)
        {
            state.LastStage = _clock.Current.Id;
            await SaveAsync(state);
            await RoleFanout.EachAsync(_sessions.All().Where(s => s.Role != null && s.TerritoryWars != null), async s =>
            { await Send(s, StageNotify, StageData()); QueueActivity(s); });
        }
    }
    public void Tick(GameTime time)
    {
        if (_ticking || _reconciledOnce && time.Now - _lastTick < TimeSpan.FromSeconds(1)) return;
        _reconciledOnce = true;
        _lastTick = time.Now; _ticking = true;
        _loop.PostRuntime(async () => { try { await ReconcileAsync(); } finally { _ticking = false; } });
    }
}
