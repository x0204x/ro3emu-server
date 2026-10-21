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
/// Escort trading and cart state for MG_TerritoryWars. The request/response shapes follow the recovered
/// clientB LC_TerritoryWarsTradeServer and LC_TerritoryWarsServer senders and the msgtable. Server policy
/// that cannot be recovered from those senders is identified as inferred beside the transition.
/// </summary>
public sealed partial class TerritoryEscortService : IGameTicker
{
    public const uint UseNpcSkill = 83050, QueryOwner = 83055, Selling = 83070, History = 83071,
        Buy = 83072, BuyOrders = 83073, Sell = 83074, TakeDown = 83075, SellOrders = 83076,
        Operate = 83077, PlayTime = 83079, GuildPlay = 83080, PlayersPlay = 83081,
        Cart = 83082, Guilds = 83084, Stage = 83085, Times = 83086, Score = 83088,
        Claim = 83091, Submit = 83092, DropSubmit = 83093, Mark = 83095, Gather = 83097,
        GroupTransfer = 83099, Rank = 83100;
    public const uint CartVarNtf = 31079, NpcSkillNtf = 83051, NpcSkillSuccessNtf = 83052,
        DetectNtf = 83053, AllCartsNtf = 83054, InfoNtf = 83078, SuccessNtf = 83083,
        ToOperateNtf = 83087, CartNtf = 83089, GuildStartNtf = 83090, SceneCountNtf = 83094,
        MarkNtf = 83096, GroupTransferNtf = 83098;

    public sealed class RoleState
    {
        public long SeasonStart { get; set; }
        public int PlayBook { get; set; }
        /// <summary>The Territory War cycle (TerritoryWarsClock.Cycle): every repeat of the war block is its own escort season.</summary>
        public long Cycle { get; set; }
        public Dictionary<int, long> ScoreByRound { get; set; } = new();
        public Dictionary<int, HashSet<int>> ClaimedByRound { get; set; } = new();
        public Dictionary<int, long> LastGuildByRound { get; set; } = new();
        public Dictionary<string, RoleProgress> Archives { get; set; } = new();
    }

    public sealed class RoleProgress
    {
        public Dictionary<int, long> ScoreByRound { get; set; } = new();
        public Dictionary<int, HashSet<int>> ClaimedByRound { get; set; } = new();
        public Dictionary<int, long> LastGuildByRound { get; set; } = new();
    }

    private sealed class Ledger
    {
        public long SeasonStart { get; set; }
        public int PlayBook { get; set; }
        /// <summary>The Territory War cycle (TerritoryWarsClock.Cycle): every repeat of the war block is its own escort season.</summary>
        public long Cycle { get; set; }
        public long NextGoodsId { get; set; } = 1;
        public long NextCartId { get; set; } = 1;
        public long NextCargoId { get; set; } = 1;
        public Dictionary<long, Listing> Listings { get; set; } = new();
        public Dictionary<long, long> Goods { get; set; } = new();
        public Dictionary<long, CartState> Carts { get; set; } = new();
        public Dictionary<long, CargoState> Cargo { get; set; } = new();
        public Dictionary<long, PlayState> Play { get; set; } = new();
        public Dictionary<long, Dictionary<int, long>> ScoresByRound { get; set; } = new();
        public Dictionary<long, HashSet<int>> StockRounds { get; set; } = new();
        public Dictionary<string, long> SkillCooldowns { get; set; } = new();
        public Dictionary<string, SeasonArchive> Archives { get; set; } = new();
    }

    private sealed class SeasonArchive
    {
        public Dictionary<long, long> Goods { get; set; } = new();
        public Dictionary<long, Listing> Listings { get; set; } = new();
        public Dictionary<long, PlayState> Play { get; set; } = new();
        public Dictionary<long, Dictionary<int, long>> ScoresByRound { get; set; } = new();
        public Dictionary<long, HashSet<int>> StockRounds { get; set; } = new();
        public Dictionary<string, long> SkillCooldowns { get; set; } = new();
    }

    private sealed class Listing
    {
        public long Id { get; set; }
        public long Seller { get; set; }
        public int Count { get; set; }
        public int Price { get; set; }
        public string Message { get; set; } = "";
        public int State { get; set; }
        public long Time { get; set; }
        public long Buyer { get; set; }
        public Dictionary<long, Application> Applications { get; set; } = new();
        public HashSet<long> Rejected { get; set; } = new();
    }

    private sealed class Application
    {
        public long Guild { get; set; }
        public long Time { get; set; }
        public string Message { get; set; } = "";
    }

    private sealed class PlayState
    {
        public int Round { get; set; }
        public int Zone { get; set; }
        public long Start { get; set; }
        public long BuyNum { get; set; }
        public long SellNum { get; set; }
        public long ToTransport { get; set; }
        public long Transported { get; set; }
        public int Point { get; set; }
        public int StartedCarts { get; set; }
        public int FinishedCarts { get; set; }
        public long Score { get; set; }
    }

    private sealed class CartState
    {
        public long Id { get; set; }
        public long Guild { get; set; }
        public long Owner { get; set; }
        public int Round { get; set; }
        public int Type { get; set; }
        public int Point { get; set; }
        public int Count { get; set; }
        public int OriginCount { get; set; }
        public int DamageTier { get; set; }
        public long CurrentHp { get; set; }
        public long Started { get; set; }
        public long Scene { get; set; }
    }

    private sealed class CargoState
    {
        public long Id { get; set; }
        public long Scene { get; set; }
        public long LooterGuild { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public long FreeAfter { get; set; }
        public long Expires { get; set; }
        public bool Picked { get; set; }
    }

    private readonly AccountStore _accounts;
    private readonly GuildStore _guilds;
    private readonly GuildService _guildService;
    private readonly RoleStore _roles;
    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly SessionRegistry _sessions;
    private readonly TeamService _teams;
    private readonly TerritoryWarsClock _clock;
    private readonly AwardService _awards;
    private readonly SceneService _scene;
    private readonly GameLoop _loop;
    private readonly World _world;
    private readonly WorldData _maps;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Ledger _committedLedger = new();
    private sealed class LedgerStage(Ledger value, GameLoop.WorkPermit? permit)
    {
        public Ledger Value = value;
        public readonly GameLoop.WorkPermit? Permit = permit;
        public bool Active = true;
    }
    private readonly AsyncLocal<LedgerStage?> _stagedLedger = new();
    private Ledger ledger
    {
        get => _stagedLedger.Value is { Active: true } stage
            ? ReferenceEquals(stage.Permit, _loop.CurrentWork) ? stage.Value : CopyLedger(_committedLedger)
            : _committedLedger;
        set
        {
            if (_stagedLedger.Value is { Active: true } stage)
            {
                if (!ReferenceEquals(stage.Permit, _loop.CurrentWork))
                    throw new InvalidOperationException("Escort stage escaped its game-loop work permit.");
                stage.Value = value;
            }
            else _committedLedger = value;
        }
    }
    private static Ledger CopyLedger(Ledger value) => JsonSerializer.Deserialize<Ledger>(JsonSerializer.SerializeToUtf8Bytes(value))!;
    private IDisposable StageLedger()
    {
        var prior = _stagedLedger.Value;
        var stage = new LedgerStage(CopyLedger(ledger), _loop.CurrentWork);
        _stagedLedger.Value = stage;
        return new StageLease(() => { stage.Active = false; _stagedLedger.Value = prior; });
    }
    private sealed class StageLease(Action release) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) release();
        }
    }
    private Ownership.ReservationKey[] MutationKeys(GameSession? actor, bool includeOnlineRoles,
        IReadOnlyCollection<long>? additionalRoles = null, IReadOnlyCollection<long>? additionalGuilds = null,
        bool includeAllGuilds = false)
    {
        var keys = new HashSet<Ownership.ReservationKey>
        {
            Ownership.ReservationKey.Entity("realm", nameof(RealmStateKind.TerritoryEscort)),
        };
        if (actor?.Role is { } role) keys.Add(Ownership.ReservationKey.Role(role.Id));
        foreach (long extraRole in additionalRoles ?? [])
            if (extraRole > 0) keys.Add(Ownership.ReservationKey.Role(extraRole));
        if (includeOnlineRoles)
        {
            foreach (var cart in ledger.Carts.Values)
                if (cart.Owner > 0) keys.Add(Ownership.ReservationKey.Role(cart.Owner));
            foreach (var peer in _sessions.All().Where(s => s.Role != null && s.TerritoryEscort != null))
                keys.Add(Ownership.ReservationKey.Role(peer.Role!.Id));
        }
        foreach (long guild in (includeAllGuilds ? ledger.Goods.Keys.Concat(ledger.Listings.Values.SelectMany(x =>
                     new[] { x.Seller, x.Buyer }.Concat(x.Applications.Keys)))
                 .Concat(ledger.Carts.Values.Select(x => x.Guild)).Concat(_guilds.List().Select(g => g.Id)) : [])
                 .Concat(actor == null ? [] : [Own(actor)?.Id ?? 0])
                 .Concat(additionalGuilds ?? []))
            if (guild > 0) keys.Add(Ownership.ReservationKey.Entity("guild", guild.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return keys.ToArray();
    }
    private async Task<Ownership.LegacyOwnerScope?> EnterGateAsync(string origin, GameSession? actor = null,
        bool includeOnlineRoles = false, IReadOnlyCollection<long>? additionalRoles = null,
        IReadOnlyCollection<long>? additionalGuilds = null, bool includeAllGuilds = false)
    {
        if (_loop.Owners is not { } owners) { await _gate.WaitAsync(); return null; }
        while (true)
        {
            var scope = await owners.EnterWithGateAsync(origin, _gate,
                MutationKeys(actor, includeOnlineRoles, additionalRoles, additionalGuilds, includeAllGuilds));
            var needed = MutationKeys(actor, includeOnlineRoles, additionalRoles, additionalGuilds, includeAllGuilds);
            if (owners.Reservations.Covers(scope.Context.Operation, needed) ||
                owners.AuditUncovered($"{origin} under {scope.Context.Origin} needs {string.Join(",", needed)}")) return scope;
            _gate.Release();
            scope.Dispose();
        }
    }
    private Dictionary<long, long> _goodsSnapshot = new();
    private Dictionary<(long Guild, int Round), long> _scoreSnapshot = new();
    private string _committedEpoch = "";
    private TimeSpan _lastTick;
    private bool _ticking, _reconciledOnce; // the first reconciliation runs on the loop's first tick (startup), not 1 s later
    public Func<int, long>? OwnerOf { get; set; }
    public Func<long, int>? GuildPoint { get; set; }

    public TerritoryEscortService(AccountStore accounts, GuildStore guilds, GuildService guildService, RoleStore roles, GameData data,
        CharacterService character, SessionRegistry sessions, TeamService teams, TerritoryWarsClock clock,
        AwardService awards, SceneService scene, GameLoop loop, World world, WorldData maps, CombatService combat)
    {
        this._accounts = accounts;
        this._guilds = guilds;
        this._guildService = guildService;
        this._roles = roles;
        this._data = data;
        this._character = character;
        this._sessions = sessions;
        this._teams = teams;
        this._clock = clock;
        this._awards = awards;
        this._scene = scene;
        this._loop = loop;
        this._world = world;
        this._maps = maps;
        ledger = accounts.ReadSync<byte[]?>(new TerritoryRealmReadPlan(RealmStateKind.TerritoryEscort)) is { } bytes
            ? JsonSerializer.Deserialize<Ledger>(bytes) ?? new Ledger() : new Ledger();
        foreach (var (guild, play) in ledger.Play)
            if (!ledger.ScoresByRound.TryGetValue(guild, out var rounds) || !rounds.ContainsKey(play.Round))
            {
                if (rounds == null) ledger.ScoresByRound[guild] = rounds = new Dictionary<int, long>();
                rounds[play.Round] = play.Score;
            }
        PublishSnapshots();
        scene.Loaded.Add(SceneLoadedAsync);
        scene.LoadedKeys.Add(SceneKeys);
        scene.Leaving.Add(SceneLeavingAsync);
        combat.Killed.Add(CartKilledAsync);
        combat.MonsterDamageApplied.Add(CartDamagedAsync);
        combat.PvpDealt.Add(PvpRecorded);
        combat.PlayerKilled.Add(PlayerKilledAsync);
    }

    private static long RoleId(GameSession s) => s.Role?.Id ?? 0;
    private GuildStore.State? Own(GameSession s) => s.Role == null ? null : _guilds.ForRole(s.Role.Id);
    private static bool Right(GuildStore.State? g, long role, int right) => g != null && g.Members.TryGetValue(role, out var m)
        && (m.Official == 1 || g.Rights.GetValueOrDefault(right)?.Contains(m.Official) == true);
    private static int MarketPermission(GuildStore.State? g, long role) => g == null ? ErrorCodes.NotInGuild
        : Right(g, role, 16) ? 0 : ErrorCodes.GuildPermission;
    private int Round => _clock.Current.Round;
    private bool Trading => _clock.Current.Type == 1;
    private bool Playing => _clock.Current.Type == 2;

    private void Save(RoleItemPlanBuilder transaction) => transaction.Add(
        new RealmStatePlan(RealmStateKind.TerritoryEscort, JsonSerializer.SerializeToUtf8Bytes(ledger)));

    private async Task CommitAsync(RoleItemPlanBuilder transaction, string kind = "state", string? sourceKey = null,
        GameSession? actor = null, IReadOnlyList<EventEconomyOwnerDelta>? extraDeltas = null,
        IReadOnlyList<EconomyQuantity>? extraCosts = null, IReadOnlyList<EconomyQuantity>? extraGoods = null)
    {
        Save(transaction);
        var economic = EscortEconomy(_committedLedger, ledger, kind, sourceKey ?? "state-only", actor,
            extraDeltas, extraCosts, extraGoods);
        if (economic != null)
        {
            if (sourceKey == null) throw new InvalidOperationException("Escort economic transition needs a natural source key.");
            if (actor?.Role is { } role) transaction.PinRole(role.Id);
            transaction.WithEventEconomy(economic);
        }
        await _roles.CommitAsync(transaction, "territory/escort");
        var oldGoods = Volatile.Read(ref _goodsSnapshot);
        var oldEpoch = Volatile.Read(ref _committedEpoch);
        _committedLedger = CopyLedger(ledger);
        PublishSnapshots();
        // 32231 Change_Item is how the recovered client updates SelfGuildItemData.
        foreach (var guildId in oldGoods.Keys.Union(ledger.Goods.Keys)
            .Where(id => oldEpoch != Epoch || oldGoods.GetValueOrDefault(id) != ledger.Goods.GetValueOrDefault(id)))
            foreach (var peer in _sessions.All().Where(s => s.Role != null && _guilds.ForRole(s.Role.Id)?.Id == guildId))
                _loop.PostOwned(peer, () => peer.SendGameAsync(GuildService.BaseChange,
                    new GuildBaseInfoChangeNtf
                    {
                        Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeItem,
                        Items = GuildItems(guildId)
                    }), "escort/goods notice");
    }

    private void RestoreCommittedLedger()
    {
        ledger = CopyLedger(_committedLedger);
        PublishSnapshots();
    }

    private void PublishSnapshots()
    {
        Volatile.Write(ref _goodsSnapshot, new Dictionary<long, long>(ledger.Goods));
        Volatile.Write(ref _scoreSnapshot, ledger.ScoresByRound.SelectMany(p => p.Value.Select(r =>
            new KeyValuePair<(long Guild, int Round), long>((p.Key, r.Key), r.Value))).ToDictionary(x => x.Key, x => x.Value));
        Volatile.Write(ref _committedEpoch, TerritoryWarsClock.EpochOf(ledger.SeasonStart, ledger.PlayBook, ledger.Cycle));
    }

    private string Epoch => _clock.Epoch;

    private bool AlignRole(RoleState state)
    {
        if (state.SeasonStart == _clock.SeasonStart && state.PlayBook == _clock.PlayBook && state.Cycle == _clock.Cycle) return false;
        string previous = TerritoryWarsClock.EpochOf(state.SeasonStart, state.PlayBook, state.Cycle);
        if (state.SeasonStart != 0 || state.PlayBook != 0)
            state.Archives[previous] = new RoleProgress
            {
                ScoreByRound = state.ScoreByRound,
                ClaimedByRound = state.ClaimedByRound, LastGuildByRound = state.LastGuildByRound
            };
        if (state.Archives.Remove(Epoch, out var restored))
        {
            state.ScoreByRound = restored.ScoreByRound;
            state.ClaimedByRound = restored.ClaimedByRound;
            state.LastGuildByRound = restored.LastGuildByRound;
        }
        else
        {
            state.ScoreByRound = new(); state.ClaimedByRound = new(); state.LastGuildByRound = new();
        }
        state.SeasonStart = _clock.SeasonStart;
        state.PlayBook = _clock.PlayBook;
        state.Cycle = _clock.Cycle;
        return true;
    }

    private void AddGuildScore(long guildId, long score)
    {
        var play = GetPlay(guildId);
        play.Score = checked(play.Score + score);
        if (!ledger.ScoresByRound.TryGetValue(guildId, out var rounds))
            ledger.ScoresByRound[guildId] = rounds = new Dictionary<int, long>();
        rounds[Round] = play.Score;
    }

    private async Task SaveRoleAsync(GameSession s)
    {
        var transaction = _roles.NewTransaction();
        SaveRole(s, transaction);
        await _roles.CommitAsync(transaction, "territory/escort-role");
    }
    private static void SaveRole(GameSession s, RoleItemPlanBuilder transaction) =>
        SaveRole(s.Role!.Id, s.TerritoryEscort!, transaction);
    private static void SaveRole(long roleId, RoleState state, RoleItemPlanBuilder transaction) =>
        transaction.RoleBlob(roleId, "territory_escort", JsonSerializer.SerializeToUtf8Bytes(state));
    private static RoleState CopyRole(RoleState state) =>
        JsonSerializer.Deserialize<RoleState>(JsonSerializer.SerializeToUtf8Bytes(state))!;

    public async Task LoginAsync(GameSession s)
    {
        // C/D 15: no escort reconciliation inside the login (it reserved the realm escort ledger, every guild and the
        // online roles). The one-second Tick runs it from startup and re-aligns online roles on a season change.
        var bytes = _roles.LoadTerritoryEscort(s.Role!.Id);
        var current = bytes == null ? new RoleState() : JsonSerializer.Deserialize<RoleState>(bytes) ?? new RoleState();
        if (AlignRole(current))
        {
            var transaction = _roles.NewTransaction();
            SaveRole(s.Role.Id, current, transaction);
            await _roles.CommitAsync(transaction, "territory/escort-role");
        }
        s.TerritoryEscort = current;
        await SendStageAsync(s);
        await SendCartsAsync(s);
        await SendScoreAsync(s);
    }

    /// <summary>Trusted producer for guild escort goods (item 1258000); never accepts a client-supplied amount.</summary>
    public async Task CreditGuildGoodsAsync(long guildId, int amount, string sourceKey)
    {
        if (_loop.IsWorkerActive && !_loop.IsOnLoop)
        {
            await _loop.InvokeAsync(() => CreditGuildGoodsAsync(guildId, amount, sourceKey));
            return;
        }
        if (guildId <= 0 || amount <= 0 || string.IsNullOrWhiteSpace(sourceKey) || _guilds.Get(guildId) == null)
            throw new ArgumentOutOfRangeException(nameof(amount));
        using var reservation = await EnterGateAsync("escort/goods source", additionalGuilds: [guildId]);
        using var stagedEscortLedger = StageLedger();
        try
        {
            var transaction = _accounts.NewTransaction();
            ledger.Goods[guildId] = checked(ledger.Goods.GetValueOrDefault(guildId) + amount);
            await CommitAsync(transaction, "producer-stock", sourceKey);
        }
        finally { stagedEscortLedger.Dispose(); _gate.Release(); reservation?.Dispose(); }
    }

    public long GetGuildEscortScore(long guildId, int round)
        => Volatile.Read(ref _committedEpoch) == Epoch ? Volatile.Read(ref _scoreSnapshot).GetValueOrDefault((guildId, round)) : 0;

    public long GetGuildGoods(long guildId)
        => Volatile.Read(ref _committedEpoch) == Epoch ? Volatile.Read(ref _goodsSnapshot).GetValueOrDefault(guildId) : 0;

    /// <summary>Item 1258000 is a guild item, not a character bag item; the UI reads this map's stack.</summary>
    public GuildItemData GuildItems(long guildId)
    {
        var info = new GuildItemData();
        var count = GetGuildGoods(guildId);
        info.Items[1258000] = new ItemBaseInfo
        {
            ItemGuid = guildId, ItemEntry = 1258000, OwnerGuildId = guildId,
            Stack = (int)Math.Clamp(count, 0, int.MaxValue),
            Season = _clock.SeasonStart, PlayBook = _clock.PlayBook,
            ItemBindInfo = new ItemBindInfo(),
        };
        return info;
    }

    private async Task NotifyGuildItemsAsync(long guildId)
    {
        var info = GuildItems(guildId);
        var ntf = new GuildBaseInfoChangeNtf
        {
            Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeItem, Items = info,
        };
        await RoleFanout.EachAsync(_sessions.All().Where(s => s.Role != null && _guilds.ForRole(s.Role.Id)?.Id == guildId),
            peer => peer.SendGameAsync(GuildService.BaseChange, ntf));
    }

    /// <summary>Exact escort owner request set shared with dispatcher admission.</summary>
    internal static bool IsOwnerRequest(uint id) => id is
        Selling or History or Buy or BuyOrders or Sell or TakeDown or SellOrders or Operate
        or Stage or Score or Rank or Times or GuildPlay or PlayersPlay or Guilds or PlayTime
        or Cart or Submit or DropSubmit or Claim or QueryOwner or Mark or Gather or GroupTransfer or UseNpcSkill;

    internal bool IsSharedOwnerRequest(GameSession session, uint id, ReadOnlyMemory<byte> body)
    {
        if (id == 32287) return true;
        if (id != SceneService.NpcInteraction) return false;
        long uid = NpcInteractionReq.Parser.ParseFrom(body.Span).NpcUid;
        return uid >= CargoUidBase && uid < CargoUidBase * 2;
    }

    public async Task<bool> HandleAsync(GameSession s, uint id, ReadOnlyMemory<byte> body)
    {
        if (!IsOwnerRequest(id)) return false;
        if (Volatile.Read(ref _committedEpoch) != Epoch || _lastStageType != _clock.Current.Type)
            await ReconcileAsync();
        switch (id)
        {
            case Selling: await SellingAsync(s, TerritoryWarsEscortSellingGoodsReq.Parser.ParseFrom(body.Span)); break;
            case History: await HistoryAsync(s); break;
            case Buy: await BuyAsync(s, TerritoryWarsEscortGuildBuyReq.Parser.ParseFrom(body.Span)); break;
            case BuyOrders: await OrdersAsync(s, false); break;
            case Sell: await SellAsync(s, TerritoryWarsEscortGuildSellReq.Parser.ParseFrom(body.Span)); break;
            case TakeDown: await TakeDownAsync(s, TerritoryWarsEscortGuildTakeDownReq.Parser.ParseFrom(body.Span)); break;
            case SellOrders: await OrdersAsync(s, true); break;
            case Operate: await OperateAsync(s, TerritoryWarsEscortOperateApplyReq.Parser.ParseFrom(body.Span)); break;
            case Stage: await SendStageAsync(s); break;
            case Score: await SendScoreAsync(s); break;
            case Rank: await RankAsync(s); break;
            case Times: await TimesAsync(s); break;
            case GuildPlay: await GuildPlayAsync(s, TerritoryWarsEscortGuildPlayInfoReq.Parser.ParseFrom(body.Span)); break;
            case PlayersPlay: await PlayersPlayAsync(s, TerritoryWarsEscortGuildPlayersPlayInfoReq.Parser.ParseFrom(body.Span)); break;
            case Guilds: await GuildsAsync(s, TerritoryWarsEscortGuildsReq.Parser.ParseFrom(body.Span)); break;
            case PlayTime: await PlayTimeAsync(s, TerritoryWarsEscortPlayTimeReq.Parser.ParseFrom(body.Span)); break;
            case Cart: await CartAsync(s, TerritoryWarsEscortCartReq.Parser.ParseFrom(body.Span)); break;
            case Submit: await SubmitAsync(s, TerritoryWarsEscortSubmitReq.Parser.ParseFrom(body.Span)); break;
            case DropSubmit: await DropSubmitAsync(s); break;
            case Claim: await ClaimAsync(s, TerritoryWarsEscortGuildPlayerGetScoreAwardReq.Parser.ParseFrom(body.Span)); break;
            case QueryOwner: await QueryOwnerAsync(s, EscortQueryGuildByOwnerReq.Parser.ParseFrom(body.Span)); break;
            case Mark: await MarkAsync(s, EscortSetMarkPosReq.Parser.ParseFrom(body.Span)); break;
            case Gather: await GatherAsync(s, EscortGatherReq.Parser.ParseFrom(body.Span)); break;
            case GroupTransfer: await GroupTransferAsync(s, EscortGroupTransferReq.Parser.ParseFrom(body.Span)); break;
            case UseNpcSkill: await UseNpcSkillAsync(s, EscortUseNpcSkillReq.Parser.ParseFrom(body.Span)); break;
            default: return false;
        }
        return true;
    }

    private static TerritoryWarsEscortGuildSimpleInfo Simple(GuildStore.State g)
    {
        var leader = g.Members.Values.FirstOrDefault(m => m.Official == 1);
        return new TerritoryWarsEscortGuildSimpleInfo
        {
            GuildId = g.Id, GuildName = g.Name,
            LeaderId = leader?.RoleId ?? 0, LeaderName = leader?.Name ?? "",
            HeadIcon = new GuildHeadIcon { BackgroundType = g.Background, IconType = g.Icon }
        };
    }

    private static TerritoryWarsEscortGuildSellingGoods Wire(Listing x)
    {
        var w = new TerritoryWarsEscortGuildSellingGoods
        {
            GoodsUid = x.Id, GuildId = x.Seller,
            SellingNum = x.Count, UnitPrice = x.Price, SellingMessage = x.Message,
            State = (TerritoryWarsEscortGoodsState)x.State, Timestamp = x.Time,
            StateData = new TerritoryWarsEscortGuildSellingGoodsOrderData()
        };
        foreach (var a in x.Applications.Values.OrderBy(a => a.Time))
            w.StateData.ApplyBuyers.Add(new TerritoryWarsEscortGuildApplyBuyer { GuildId = a.Guild, Timestamp = a.Time, Message = a.Message });
        foreach (var id in x.Rejected)
            w.StateData.RejectBuyers.Add(new TerritoryWarsEscortGuildRejectBuyer
            {
                GuildId = id,
                Timestamp = x.Time, Reason = (TerritoryWarsEscortTradeRejectReason)4
            });
        if (x.Buyer != 0) w.StateData.SuccessBuyer = new TerritoryWarsEscortGuildSuccessBuyer { GuildId = x.Buyer, Timestamp = x.Time };
        return w;
    }

    private static void AddSimple(IDictionary<long, TerritoryWarsEscortGuildSimpleInfo> target,
        IEnumerable<Listing> listings, GuildStore store)
    {
        foreach (var id in listings.SelectMany(x => new[] { x.Seller, x.Buyer }.Concat(x.Applications.Keys)).Where(x => x > 0).Distinct())
            if (store.Get(id) is { } g) target[id] = Simple(g);
    }

    private async Task SellingAsync(GameSession s, TerritoryWarsEscortSellingGoodsReq req)
    {
        TerritoryWarsEscortSellingGoodsRes resp;
        using var reservation = await EnterGateAsync("escort/request", s);
        using var stagedEscortLedger = StageLedger();
        try
        {
            var sorted = ledger.Listings.Values.Where(x => x.State == 0).OrderByDescending(x => x.Time).ToList();
            resp = new TerritoryWarsEscortSellingGoodsRes
            {
                TotalNum = sorted.Count,
                StartIdx = Math.Clamp(req.StartIdx, 0, sorted.Count), Num = Math.Clamp(req.Num, 1, 100)
            };
            var page = sorted.Skip(resp.StartIdx).Take(resp.Num).ToList();
            resp.SellingGoods.AddRange(page.Select(Wire));
            AddSimple(resp.GuildSimpleInfos, page, _guilds);
        }
        finally { stagedEscortLedger.Dispose(); _gate.Release(); reservation?.Dispose(); }
        await s.SendGameAsync(Selling, resp);
    }

    private async Task HistoryAsync(GameSession s)
    {
        var own = Own(s)?.Id ?? 0;
        TerritoryWarsEscortGuildTradeHistoryRes resp;
        using var reservation = await EnterGateAsync("escort/request", s);
        using var stagedEscortLedger = StageLedger();
        try
        {
            var list = ledger.Listings.Values.Where(x => x.State != 0).OrderByDescending(x => x.Time).Take(100).ToList();
            resp = new TerritoryWarsEscortGuildTradeHistoryRes();
            resp.BuyList.AddRange(list.Where(x => x.Buyer == own).Select(Wire));
            resp.SellList.AddRange(list.Where(x => x.Seller == own).Select(Wire));
            AddSimple(resp.GuildSimpleInfos, list, _guilds);
        }
        finally { stagedEscortLedger.Dispose(); _gate.Release(); reservation?.Dispose(); }
        await s.SendGameAsync(History, resp);
    }

    private async Task OrdersAsync(GameSession s, bool seller)
    {
        var own = Own(s)?.Id ?? 0;
        Google.Protobuf.IMessage resp;
        using var reservation = await EnterGateAsync("escort/request", s);
        using var stagedEscortLedger = StageLedger();
        try
        {
            var list = ledger.Listings.Values.Where(x => seller ? x.Seller == own : x.Applications.ContainsKey(own))
                .OrderByDescending(x => x.Time).Take(100).ToList();
            if (seller)
            {
                var result = new TerritoryWarsEscortGuildSellOrderListRes();
                result.Orders.AddRange(list.Select(Wire)); AddSimple(result.GuildSimpleInfos, list, _guilds);
                resp = result;
            }
            else
            {
                var result = new TerritoryWarsEscortGuildOrderListRes();
                result.Orders.AddRange(list.Select(Wire)); AddSimple(result.GuildSimpleInfos, list, _guilds);
                resp = result;
            }
        }
        finally { stagedEscortLedger.Dispose(); _gate.Release(); reservation?.Dispose(); }
        await s.SendGameAsync(seller ? SellOrders : BuyOrders, resp);
    }

    private async Task SellAsync(GameSession s, TerritoryWarsEscortGuildSellReq req)
    {
        var guild = Own(s);
        int result = MarketPermission(guild, RoleId(s));
        bool isNew = req.GoodsUid == 0;
        long id = req.GoodsUid;
        if (result == 0 && !Trading) result = ErrorCodes.FeatureUnavailable;
        if (result == 0 && (req.SellingNum < 10 || req.UnitPrice is < 5 or > 100 || req.SellingMessage.Length > 100)) result = ErrorCodes.Parameter;
        using var reservation = await EnterGateAsync("escort/request", s);
        using var stagedEscortLedger = StageLedger();
        try
        {
            Listing? old = null;
            if (result == 0 && !isNew && (!ledger.Listings.TryGetValue(id, out old) || old.Seller != guild!.Id || old.State != 0))
                result = ErrorCodes.Parameter;
            long available = ledger.Goods.GetValueOrDefault(guild?.Id ?? 0) + (old?.Count ?? 0);
            if (result == 0 && available < req.SellingNum) result = ErrorCodes.ItemsShort;
            if (result == 0)
            {
                var transaction = _accounts.NewTransaction();
                if (isNew) id = ledger.NextGoodsId++;
                ledger.Goods[guild!.Id] = available - req.SellingNum;
                ledger.Listings[id] = new Listing
                {
                    Id = id, Seller = guild.Id, Count = req.SellingNum,
                    Price = req.UnitPrice, Message = req.SellingMessage, Time = _clock.Now, State = 0
                };
                await CommitAsync(transaction, "listing", isNew
                    ? $"escort-list:{Epoch}:{id}:open"
                    : $"escort-list:{Epoch}:{id}:amend:{old!.Count}:{req.SellingNum}:{_clock.Now}", s);
            }
        }
        finally { stagedEscortLedger.Dispose(); _gate.Release(); reservation?.Dispose(); }
        await s.SendGameAsync(Sell, new TerritoryWarsEscortGuildSellRes { GoodsUid = id, Result = result, IsNew = isNew && result == 0 });
    }

    private async Task TakeDownAsync(GameSession s, TerritoryWarsEscortGuildTakeDownReq req)
    {
        var guild = Own(s);
        int result = MarketPermission(guild, RoleId(s));
        if (result == 0 && !Trading) result = ErrorCodes.FeatureUnavailable;
        using var reservation = await EnterGateAsync("escort/request", s);
        using var stagedEscortLedger = StageLedger();
        try
        {
            if (result == 0 && (!ledger.Listings.TryGetValue(req.GoodsUid, out var probe) || probe.Seller != guild!.Id || probe.State != 0))
                result = ErrorCodes.Parameter;
            if (result == 0)
            {
                var x = ledger.Listings[req.GoodsUid];
                var transaction = _accounts.NewTransaction();
                ledger.Goods[guild!.Id] = checked(ledger.Goods.GetValueOrDefault(guild.Id) + x.Count);
                x.State = 2; x.Applications.Clear(); x.Time = _clock.Now;
                await CommitAsync(transaction, "listing-takedown",
                    $"escort-list:{Epoch}:{req.GoodsUid}:take-down", s);
            }
        }
        finally { stagedEscortLedger.Dispose(); _gate.Release(); reservation?.Dispose(); }
        await s.SendGameAsync(TakeDown, new TerritoryWarsEscortGuildTakeDownRes { GoodsUid = req.GoodsUid, Result = result });
    }

    private async Task BuyAsync(GameSession s, TerritoryWarsEscortGuildBuyReq req)
    {
        var guild = Own(s);
        int result = MarketPermission(guild, RoleId(s));
        if (result == 0 && !Trading) result = ErrorCodes.FeatureUnavailable;
        if (result == 0 && req.Message.Length > 100) result = ErrorCodes.Parameter;
        long seller = 0;
        using var reservation = await EnterGateAsync("escort/request", s);
        using var stagedEscortLedger = StageLedger();
        try
        {
            if (result == 0 && (!ledger.Listings.TryGetValue(req.GoodsUid, out var probe) || probe.State != 0 || probe.Seller == guild!.Id
                || probe.Applications.ContainsKey(guild.Id))) result = ErrorCodes.Parameter;
            if (result == 0)
            {
                var x = ledger.Listings[req.GoodsUid]; seller = x.Seller;
                var transaction = _accounts.NewTransaction();
                x.Applications[guild!.Id] = new Application { Guild = guild.Id, Time = _clock.Now, Message = req.Message };
                await CommitAsync(transaction, "application", actor: s);
            }
        }
        finally { stagedEscortLedger.Dispose(); _gate.Release(); reservation?.Dispose(); }
        await s.SendGameAsync(Buy, new TerritoryWarsEscortGuildBuyRes { GoodsUid = req.GoodsUid, Result = result });
        if (result == 0) await NotifySellerAsync(seller);
    }

    private async Task NotifySellerAsync(long guildId)
    {
        await RoleFanout.EachAsync(_sessions.All().Where(x => x.Role != null && _guilds.ForRole(x.Role.Id)?.Id == guildId),
            target => target.SendGameAsync(ToOperateNtf,
                new TerritoryWarsEscortToOperateApplyNtf { HasUnProcessedApply = true }));
    }

    private async Task OperateAsync(GameSession s, TerritoryWarsEscortOperateApplyReq req)
    {
        var seller = Own(s);
        int result = MarketPermission(seller, RoleId(s));
        if (result == 0 && !Trading) result = ErrorCodes.FeatureUnavailable;
        using var reservation = await EnterGateAsync("escort/request", s,
            additionalGuilds: [req.TargetGuildId]);
        using var stagedEscortLedger = StageLedger();
        try
        {
            // The first Own() preceded an await; membership and rights may have changed while
            // another escort transition held gate. Refresh before the no-await SQL mutation.
            seller = Own(s);
            result = MarketPermission(seller, RoleId(s));
            if (result == 0 && !Trading) result = ErrorCodes.FeatureUnavailable;
            if (result == 0 && (!ledger.Listings.TryGetValue(req.GoodsUid, out var probe) || probe.State != 0 ||
                probe.Seller != seller!.Id || !probe.Applications.ContainsKey(req.TargetGuildId))) result = ErrorCodes.Parameter;
            GuildStore.State? buyer = result == 0 && req.IsAccept ? _guilds.Get(req.TargetGuildId) : null;
            if (result == 0 && req.IsAccept && buyer == null) result = ErrorCodes.GuildMissing;
            long amount = 0;
            if (result == 0 && req.IsAccept)
            {
                try { amount = checked((long)ledger.Listings[req.GoodsUid].Count * ledger.Listings[req.GoodsUid].Price); }
                catch (OverflowException) { result = ErrorCodes.Parameter; }
                if (result == 0 && buyer!.Money < amount) result = ErrorCodes.GuildFundsShort;
            }
            if (result == 0)
            {
                var x = ledger.Listings[req.GoodsUid];
                var transaction = _accounts.NewTransaction();
                x.Applications.Remove(req.TargetGuildId);
                if (req.IsAccept)
                {
                    long buyerMoney = buyer!.Money, sellerMoney = seller!.Money;
                    try
                    {
                        buyer.Money -= amount; seller.Money = checked(seller.Money + amount);
                        ledger.Goods[buyer.Id] = checked(ledger.Goods.GetValueOrDefault(buyer.Id) + x.Count);
                        x.Buyer = buyer.Id; x.State = 1; x.Applications.Clear();
                        _guilds.EnlistSave(buyer, transaction, "Escort trade guild vanished");
                        _guilds.EnlistSave(seller, transaction, "Escort trade guild vanished");
                        x.Time = _clock.Now;
                        var money = new EconomyAsset(EconomyAssetKind.GuildResource, 1);
                        await CommitAsync(transaction, "trade", $"escort-list:{Epoch}:{x.Id}:trade:{buyer.Id}", s, extraDeltas:
                        [new(new(EconomyOwnerKind.Guild, buyer.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)), money, -amount),
                         new(new(EconomyOwnerKind.Guild, seller.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)), money, amount)]);
                    }
                    catch
                    {
                        buyer.Money = buyerMoney; seller.Money = sellerMoney;
                        RestoreCommittedLedger();
                        throw;
                    }
                }
                else { x.Rejected.Add(req.TargetGuildId); x.Time = _clock.Now; await CommitAsync(transaction, "application-reject", actor: s); }
            }
        }
        finally { stagedEscortLedger.Dispose(); _gate.Release(); reservation?.Dispose(); }
        await s.SendGameAsync(Operate, new TerritoryWarsEscortOperateApplyRes
        {
            GoodsUid = req.GoodsUid,
            TargetGuildId = req.TargetGuildId, IsAccept = req.IsAccept, Result = result
        });
        if (result == 0 && req.IsAccept)
        {
            await _guildService.RefreshResourceAsync(seller!.Id);
            await _guildService.RefreshResourceAsync(req.TargetGuildId);
        }
    }
}
