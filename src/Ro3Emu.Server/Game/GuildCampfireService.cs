using System.Text.Json;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// DailyActivity 116 (Bonfire Party) inside the guild home 4000101. GuildHomeService owns entry and the one room per
/// guild; this service only drives the event content in that room while the event runs. The timeline is
/// CampFireTimeLine; authored point 413 is the fire, and world spawn group 22 holds seeds. Dynamic NPC
/// placement/once-per-seed collection are (inferred) because the scene editor has no static NPCs and
/// CampFirePosition is an empty export.
/// </summary>
public sealed class GuildCampfireService : IGameTicker
{
    public const uint Lucky = 83240, Stage = 83241, Info = 83242, Accelerant = 83243,
        PlayerInfo = 83244, Fruit = 83245, Hurray = 83246, BossDead = 83247, Finish = 83248;
    public const long Dungeon = 4000101;
    /// <summary>
    /// Guild seed NPC UIDs: [2^48, 2^49), below 2^53 because the client's interaction scan keeps unit ids in a double
    /// array (LC_InteractManager m_kShareMemory; see DungeonService.SceneActorUidBase). The room part is the low 24
    /// bits of the instance sequence (instance ids are 2^50 + n), which is unique among the rooms open at one time.
    /// </summary>
    internal const long UidPrefix = 1L << 48;
    private readonly GameData _data;
    private readonly AccountStore _accounts;
    private readonly CharacterService _character;
    private readonly BuffService _buffs;
    private readonly FunctionService _functions;
    private readonly TimedTodoSchedule _schedule;
    private readonly ActivityClock _clock;
    private readonly TimeProvider _time;
    private readonly InstanceService _instances;
    private readonly CombatService _combat;
    private readonly WorldData _maps;
    private readonly GameLoop _loop;
    private readonly GuildHomeService _home;
    // Transient per-room event state. A home room outlives one event day, so these are reset when a room first sees
    // a day's event and when the event finishes.
    private readonly Dictionary<long, MonsterUnit> _bosses = new();
    private readonly Dictionary<long, (long Guild, long Room, int Stage)> _fruitActors = new();
    private readonly Dictionary<(long Room, int Stage), int> _fruitCreated = new();
    private readonly Dictionary<(long Room, int Stage), int> _fruitInitialKills = new();
    private readonly Dictionary<long, SemaphoreSlim> _guildGates = new();
    private long _lastSecond = -1;
    /// <summary>Room id: the event day and the last stage pushed to it.</summary>
    private readonly Dictionary<long, (long Day, int Stage)> _sentStage = new();
    /// <summary>Room id: the event day whose fruit/boss state the transient maps hold (set after its reset).</summary>
    private readonly Dictionary<long, long> _eventDay = new();
    /// <summary>Roles already counted at an event day's bonfire (in memory: a restart may count a day again).</summary>
    private readonly HashSet<(long Role, long Day)> _joined = new();

    /// <summary>A member in the guild home while the Bonfire Party runs, once per event day (quest target 106018);
    /// listeners post their work.</summary>
    public List<Action<GameSession>> Joined { get; } = new();

    private void MarkJoined(GameSession session)
    {
        if (session.Role is { } role && _joined.Add((role.Id, Today())))
            foreach (var handler in Joined) handler(session);
    }

    public GuildCampfireService(GameData data, AccountStore accounts, CharacterService character, BuffService buffs,
        FunctionService functions, TimedTodoSchedule schedule, ActivityClock clock, TimeProvider time,
        InstanceService instances, SceneService scene, WorldData maps, CombatService combat, GameLoop loop,
        GuildHomeService home)
    {
        this._data = data; this._accounts = accounts; this._character = character; this._buffs = buffs;
        this._functions = functions; this._schedule = schedule; this._clock = clock; this._time = time;
        this._instances = instances; this._maps = maps; this._combat = combat;
        this._loop = loop; this._home = home;
        scene.Loaded.Add(LoadedAsync);
        combat.Killed.Add(KilledAsync);
    }

    private sealed class StateData
    {
        public long Day { get; set; }
        public long Exp { get; set; }
        public Dictionary<long, HashSet<int>> Seeds { get; set; } = new();
        public Dictionary<long, int> FuelCount { get; set; } = new();
        public HashSet<int> DrawnStages { get; set; } = new();
        public bool BossDead { get; set; }
        public Dictionary<int, int> FruitKills { get; set; } = new();
    }

    private StateData Read(long guildId)
    {
        var blob = _accounts.ReadSync<byte[]?>(new CampfireReadPlan(guildId));
        var state = blob is null ? null : JsonSerializer.Deserialize<StateData>(blob);
        return state is { } current && current.Day == _clock.Day(_time.GetUtcNow().ToUnixTimeSeconds())
            ? current : new StateData { Day = _clock.Day(_time.GetUtcNow().ToUnixTimeSeconds()) };
    }

    private SemaphoreSlim Gate(long guildId) => _guildGates.TryGetValue(guildId, out var gate)
        ? gate : _guildGates[guildId] = new SemaphoreSlim(1, 1);

    private static ReservationKey[] Keys(long guild, IEnumerable<long> roles) =>
        [ReservationKey.Entity("guild", guild.ToString(System.Globalization.CultureInfo.InvariantCulture)),
         ReservationKey.Entity("guild_campfire", guild.ToString(System.Globalization.CultureInfo.InvariantCulture)),
         .. roles.Where(id => id > 0).Distinct().Select(id => ReservationKey.Role(id))];

    private async Task<LegacyOwnerScope> EnterWithGuildGateAsync(string origin, long guild,
        IEnumerable<long> roles)
    {
        var owners = _loop.Owners ?? throw new InvalidOperationException("Campfire owner registry is unavailable.");
        return await owners.EnterWithGateAsync(origin, Gate(guild), Keys(guild, roles));
    }

    private sealed class FuelScope(SemaphoreSlim guildGate, SemaphoreSlim characterGate,
        LegacyOwnerScope owner) : IDisposable
    {
        public void Dispose() { characterGate.Release(); guildGate.Release(); owner.Dispose(); }
    }

    private async Task<FuelScope> EnterFuelAsync(GameSession session, long guild, long role)
    {
        var owners = _loop.Owners ?? throw new InvalidOperationException("Campfire owner registry is unavailable.");
        var guildGate = Gate(guild);
        var attachment = session.Attachment;
        while (true)
        {
            // Neither old semaphore holds an owner key while the character gate is blocked.
            await guildGate.WaitAsync();
            bool characterLocked = false, handedOff = false;
            LegacyOwnerScope? admitted = null;
            Task? changed = null;
            try
            {
                await session.StateLock.WaitAsync();
                characterLocked = true;
                if (session.Role?.Id != role || !ReferenceEquals(session.Attachment, attachment))
                    throw new OperationCanceledException("Campfire actor changed while waiting for the character gate.");
                admitted = owners.TryEnter("campfire/fuel", Keys(guild, [role]));
                if (admitted == null) changed = owners.Reservations.Changed;
                else
                {
                    if (session.Owner is { } owner) admitted.Context.Pin(owner);
                    handedOff = true;
                    return new FuelScope(guildGate, session.StateLock, admitted);
                }
            }
            finally
            {
                if (!handedOff)
                {
                    admitted?.Dispose();
                    if (characterLocked) session.StateLock.Release();
                    guildGate.Release();
                }
            }
            await changed!;
        }
    }

    private async Task SaveAsync(long guildId, StateData state, GameSession actor)
    {
        var role = actor.Role ?? throw new InvalidOperationException("Campfire actor is detached.");
        string operation = Guid.NewGuid().ToString("N");
        string source = $"guild-campfire:state:{guildId}:{role.Id}:{operation}";
        var transaction = _accounts.NewTransaction();
        transaction.Add(new CampfireWritePlan(guildId, JsonSerializer.SerializeToUtf8Bytes(state)));
        transaction.WithEconomy(new EconomyEnvelope(operation, role.Account, role.Id, "guild-campfire-state",
            source, source, _time.GetUtcNow().ToUnixTimeMilliseconds(), [], []));
        await transaction.CommitAsync(_accounts, "guild-campfire/state");
    }

    private long? GuildId(GameSession session) => _home.GuildId(session);

    /// <summary>Event content only: entering the home itself is GuildHomeService's and is always open.</summary>
    private bool Open(GameSession session) => Eligible(session) && _schedule.IsOpen(116);

    /// <summary>A guild member who may take part in DailyActivity 116 (Lv, function 1321), at any hour.</summary>
    private bool Eligible(GameSession session) => session.ActivitiesReady && GuildId(session) != null && session.Progress != null
        && _data.Row("DailyActivityConfig", 116) is { } daily
        && session.Progress.Level >= GameData.Int(daily, "_iiLv")
        && _functions.IsOpen(session, 1321);

    private static long Uid(InstanceService.Room room, int point) =>
        UidPrefix | ((room.Id & 0xFFFFFFL) << 12) | (uint)point;

    private static int PointFromUid(InstanceService.Room room, long uid) =>
        (uid & ~0xFFFL) == (Uid(room, 0) & ~0xFFFL) ? (int)(uid & 0xFFF) : 0;

    private InstanceService.Room? RoomFor(GameSession session, bool requireLoaded = true)
    {
        if (GuildId(session) is not { } guildId || _home.RoomOf(guildId) is not { } room
            || session.InstanceId != room.Id || session.SceneEntry != Dungeon
            || requireLoaded && session.SceneLoading) return null;
        return room;
    }

    private MapPoint? FirePoint() => _maps.Map(Dungeon)?.DungeonGroups.GetValueOrDefault(414)?
        .FirstOrDefault(p => p.Id == 413) is { Id: 413 } point
            ? new MapPoint(point.X, point.Y, point.Z, point.Face) : null;

    private IReadOnlyList<SpawnPoint> SeedPoints() => _maps.Map(Dungeon)?.SpawnGroups
        .FirstOrDefault(g => g.Id == 22)?.Points.OrderBy(p => p.Id).ToArray() ?? [];

    private int MaxSeeds() => _data.Table("PublicParamConfig").TryGetValue("CampfireMaxSeedNum", out var value)
        ? Math.Clamp((int)(GameData.Number(value) ?? 20), 1, 20) : 20;

    private (int Template, MapPoint Position)? SeedAt(int pointId)
    {
        int index = pointId - 1001;
        if (index < 0 || index >= MaxSeeds()) return null;
        var points = SeedPoints();
        if (points.Count == 0) return null;
        var source = points[index % points.Count];
        // (inferred) The group requests 5-10 random spawns in a 21 m radius. Reuse its authored positions
        // with a one-metre offset, keeping the client's 20-seed personal cap reachable without forged points.
        float angle = index * 2.3999632f;
        float radius = index < points.Count ? 0 : 1f;
        var point = new MapPoint(source.X + radius * MathF.Cos(angle), source.Y,
            source.Z + radius * MathF.Sin(angle), source.Face);
        return (7020001 + index % 3, point);
    }

    private static bool Near(GameSession session, MapPoint point, float distance) =>
        MonsterService.Distance(session.Position.X, session.Position.Z, point.X, point.Z) <= distance;

    private long StartTime()
    {
        // Testing AlwaysOpen (TEST AID, not original): the 25 minutes run from the start of the repeating occurrence.
        if (_schedule.TestingOpens(116) && _schedule.CurrentOrNext(116) is { } test
            && test.Contains(_time.GetUtcNow().ToUnixTimeSeconds())) return test.Start;
        var local = _clock.LocalNow;
        var start = local.Date.AddHours(20);
        return new DateTimeOffset(start, _clock.LocalTimeZone.GetUtcOffset(start)).ToUnixTimeSeconds();
    }

    private int CurrentStage()
    {
        long elapsed = (_time.GetUtcNow().ToUnixTimeSeconds() - StartTime()) * 1000;
        if (elapsed < 0 || elapsed >= 1_500_000) return 5;
        return _data.Table("CampFireTimeLine").Values
            .Where(r => GameData.Int(r, "_iTimeLine") <= elapsed)
            .Select(r => (int)GameData.Int(r, "_iId")).DefaultIfEmpty(0).Max();
    }

    private long MaxExp() => _data.Table("CampFireLevel").Values
        .Select(r => GameData.Int(r, "_iExp")).DefaultIfEmpty(300).Max();

    private int Level(long exp) => _data.Table("CampFireLevel").Values
        .Where(r => exp >= GameData.Int(r, "_iExp"))
        .Select(r => (int)GameData.Int(r, "_iLevel")).DefaultIfEmpty(0).Max();

    private int Show(long guild, int stage)
    {
        var shows = _data.Table("CampFireShow").Values.Select(r => (int)GameData.Int(r, "_iShowId"))
            .Where(id => id > 0).OrderBy(id => id).ToArray();
        if (shows.Length == 0) return 0;
        // (inferred) Stable equal-weight choice; the corresponding CampFireProbability entries are empty.
        long day = _clock.Day(_time.GetUtcNow().ToUnixTimeSeconds());
        return shows[(int)((guild + day + stage) % shows.Length)];
    }

    private GuildCampFireStageInfo StageInfo(long guild)
    {
        int stage = CurrentStage();
        var result = new GuildCampFireStageInfo
        {
            NowStage = stage, StartTime = StartTime(), BossDead = Read(guild).BossDead,
        };
        int show = stage is >= 2 and <= 4 ? Show(guild, stage) : 0;
        if (show > 0)
            result.NowShowInfo = new GuildCampFireShowInfo
            {
                Type = (GuildCampFireShowType)1, Param = show,
                StartTimeStamp = StartTime() + (stage switch
                {
                    2 => 30,
                    3 => 300,
                    4 => 900,
                    _ => 0,
                }),
            };
        return result;
    }

    private long Today() => _clock.Day(_time.GetUtcNow().ToUnixTimeSeconds());

    /// <summary>Outside the event (or without its map data) a visitor gets an empty home.</summary>
    private async Task LoadedAsync(GameSession session)
    {
        if (RoomFor(session) is not { } room || !Open(session) || FirePoint() is not { } fire) return;
        int stage = CurrentStage();
        if (stage is < 1 or > 4) return;
        MarkJoined(session);
        var view = new AoIViewChangeData();
        AddNpc(view, room, 413, 7020004, fire);
        if (stage == 1) AddSeeds(view, room);
        await session.SendGameAsync(GameAoiSink.SyncAoiView, view);
        await session.SendGameAsync(Stage, StageInfo(GuildId(session)!.Value));
        await PlayerInfoAsync(session);
        // Until the room's Tick has reset it for today's event, its stage push spawns the actors instead.
        if (_eventDay.GetValueOrDefault(room.Id) != Today()) return;
        if (stage is >= 2 and <= 4)
            await SpawnFruitCohortAsync(GuildId(session)!.Value, room, stage);
        if (stage == 4) await SpawnBossAsync(GuildId(session)!.Value, room);
    }

    private void AddSeeds(AoIViewChangeData view, InstanceService.Room room)
    {
        for (int i = 0; i < MaxSeeds(); i++)
            if (SeedAt(1001 + i) is { } seed) AddNpc(view, room, 1001 + i, seed.Template, seed.Position);
    }

    private void AddNpc(AoIViewChangeData view, InstanceService.Room room, int pointId, int configId, MapPoint point)
    {
        if (_data.Row("NPCConfig", configId) is not { } row) return;
        var npc = new WorldNpc(Uid(room, pointId), Dungeon, WorldNpcKind.Npc, configId,
            _data.ClientTextOf(row, "_kName"), (int)GameData.Int(row, "_iCamp"),
            point.X, point.Y, point.Z, point.Face, default);
        view.Bases.Add(SceneService.NpcEntity(npc));
    }

    /// <summary>Guild seed NPCs are dynamic, so WorldData's static UID lookup cannot validate their interactions.</summary>
    public async Task<bool> TryInteractAsync(GameSession session, NpcInteractionReq req)
    {
        if (req.NpcUid < UidPrefix || req.NpcUid >= UidPrefix << 1) return false;
        var room = RoomFor(session);
        int pointId = room == null ? 0 : PointFromUid(room, req.NpcUid);
        if (pointId == 0) return false;
        int error;
        if (!Open(session)) error = ErrorCodes.FeatureUnavailable;
        else if (pointId == 413)
            error = req.InteractionId == 41002101 && FirePoint() is { } fire && Near(session, fire, 10)
                ? 0 : ErrorCodes.NpcParameter;
        else
        {
            var seed = SeedAt(pointId);
            if (seed == null) error = ErrorCodes.NpcMissing;
            else if (CurrentStage() != 1) error = ErrorCodes.FeatureUnavailable;
            else if (req.InteractionId != 41002104 + (pointId - 1001) % 3) error = ErrorCodes.NpcParameter;
            else if (!Near(session, seed.Value.Position, 3.2f))
                error = ErrorCodes.NpcTooFar;
            else if (_loop.Owners?.Current is { RuntimeOnly: false })
            {
                // C/D 16: NPC talk reserves only its sender; the seed admits its guild gate as its own operation right
                // after, and answers the interaction then.
                int seedKind = 1 + (pointId - 1001) % 3;
                _loop.TrackRuntimeDetached("campfire seed", async () => await session.SendGameAsync(SceneService.NpcInteraction,
                    new NpcInteractionRsp { Result = await AddSeedAsync(session, pointId, seedKind), Req = req }));
                return true;
            }
            else error = await AddSeedAsync(session, pointId, 1 + (pointId - 1001) % 3);
        }
        await session.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionRsp { Result = error, Req = req });
        return true;
    }

    private async Task<int> AddSeedAsync(GameSession session, int pointId, int seedId)
    {
        long guild = GuildId(session)!.Value, role = session.Role!.Id;
        using var scope = await EnterWithGuildGateAsync("campfire/seed", guild, [role]);
        try
        {
            if (session.Role?.Id != role || GuildId(session) != guild) return ErrorCodes.NotInGuild;
            // A detached seed (C/D 16) is admitted later than its NPC talk: the seed stage may have ended meanwhile.
            if (!Open(session) || CurrentStage() != 1) return ErrorCodes.FeatureUnavailable;
            var next = Read(guild);
            if (next.Seeds.GetValueOrDefault(role)?.Contains(pointId) == true) return ErrorCodes.RewardClaimed;
            if (next.Seeds.GetValueOrDefault(role)?.Count >= MaxSeeds() || next.Exp >= MaxExp()) return ErrorCodes.RewardClaimed;
            int exp = _data.Row("CampFireSeed", seedId) is { } row ? (int)GameData.Int(row, "_iExp") : 0;
            if (exp <= 0) return ErrorCodes.RewardMissing;
            if (!next.Seeds.TryGetValue(role, out var collected)) next.Seeds[role] = collected = new HashSet<int>();
            collected.Add(pointId);
            next.Exp = Math.Min(MaxExp(), checked(next.Exp + exp));
            await SaveAsync(guild, next, session);
            await PlayerInfoAsync(session);
            return 0;
        }
        finally { Gate(guild).Release(); }
    }

    public async Task InfoAsync(GameSession session)
    {
        var rsp = new GetGuildCampFireRsp();
        if (RoomFor(session) != null && Open(session))
        {
            long guild = GuildId(session)!.Value;
            rsp.NowStageInfo = StageInfo(guild);
            rsp.ShowList.AddRange(_data.Table("CampFireShow").Values
                .Select(r => GameData.Int(r, "_iShowId")).Where(id => id > 0).OrderBy(id => id));
            // Affix pool entries are empty in both Lua and JSON. No arbitrary client modifier is advertised.
        }
        await session.SendGameAsync(Info, rsp);
    }

    public async Task PlayerInfoAsync(GameSession session)
    {
        var state = GuildId(session) is { } guildId ? Read(guildId) : null;
        await session.SendGameAsync(PlayerInfo, new CampFirePlayerInfo
        {
            AddSeedCount = state?.Seeds.GetValueOrDefault(session.Role?.Id ?? 0)?.Count ?? 0,
            BubblesRewardCount = 0, FireBlessingEndTime = 0,
        });
    }

    public async Task AccelerantAsync(GameSession session, GuildCampFireAddAccelerantReq req)
    {
        int error = await AddAccelerantAsync(session, req);
        await session.SendGameAsync(Accelerant, new GuildCampFireAddAccelerantRsp { Result = error });
        if (error == 0 && CurrentStage() is >= 2 and <= 4 && RoomFor(session) is { } room
            && GuildId(session) is { } guild) await DrawAsync(guild, room, CurrentStage());
    }

    private async Task<int> AddAccelerantAsync(GameSession session, GuildCampFireAddAccelerantReq req)
    {
        var room = RoomFor(session);
        if (room == null || !Open(session) || CurrentStage() is < 1 or > 4) return ErrorCodes.FeatureUnavailable;
        if (req.CampFireId != Uid(room, 413)) return ErrorCodes.NpcMissing;
        if (FirePoint() is not { } fire || !Near(session, fire, 10f)) return ErrorCodes.NpcTooFar;
        if (_data.Row("CampFireFuel", req.AcceleantCfgId) is not { } fuel) return ErrorCodes.Parameter;
        int item = (int)GameData.Int(fuel, "_iItemId"), quality = (int)GameData.Int(fuel, "_iQuality");
        if (item <= 0 || quality <= 0 || _data.Row("ItemConfig", item) == null) return ErrorCodes.RewardMissing;
        long guild = GuildId(session)!.Value;
        long role = session.Role!.Id;
        using var scope = await EnterFuelAsync(session, guild, role);
        if (session.Role?.Id != role || GuildId(session) != guild || RoomFor(session)?.Id != room.Id)
            return ErrorCodes.NotInGuild;
        var next = Read(guild);
        if (next.Exp >= MaxExp()) return ErrorCodes.RewardClaimed;
        // (inferred) Fuel quality advances the same 1/4/10 EXP scale as CampFireSeed.
        int exp = _data.Row("CampFireSeed", quality) is { } seed ? (int)GameData.Int(seed, "_iExp") : 0;
        if (exp <= 0) return ErrorCodes.RewardMissing;
        next.Exp = Math.Min(MaxExp(), checked(next.Exp + exp));
        next.FuelCount[session.Role!.Id] = next.FuelCount.GetValueOrDefault(session.Role.Id) + 1;
        var cost = new List<(int Entry, int Count)> { (item, 1) };
        string operation = Guid.NewGuid().ToString("N");
        var asset = new EconomyAsset(GameData.Int(_data.Row("ItemConfig", item)!.Value, "_iType") == 1
            ? EconomyAssetKind.Currency : EconomyAssetKind.Item, item);
        var effect = new EconomyEffect(asset,
            new EconomyOwner(EconomyOwnerKind.Role, session.Role.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            asset.Kind == EconomyAssetKind.Currency ? EconomyCompartment.Currency : EconomyCompartment.Inventory,
            -1, EconomyLegKind.Sink, EconomyReason.GuildActivityCost);
        var economy = new EconomyEnvelope(operation, session.Role.Account, session.Role.Id,
            "guild-campfire-fuel", $"{guild}:{req.AcceleantCfgId}:{next.FuelCount[session.Role.Id]}",
            $"guild-campfire:fuel:{guild}:{session.Role.Id}:{operation}",
            _time.GetUtcNow().ToUnixTimeMilliseconds(), [effect], []);
        if (!await _character.ApplyAwardAsync(session, new AwardPlan(0, 0, []), costs: cost,
                persist: tx =>
                {
                    tx.Add(new GuildCampfireStatePlan(guild, JsonSerializer.SerializeToUtf8Bytes(next)));
                    tx.WithEconomy(economy);
                }, stateLockHeld: true))
            return _character.ShortageOf(session, [(item, 1L)]);
        return 0;
    }

    public void Tick(GameTime gameTime)
    {
        long second = _time.GetUtcNow().ToUnixTimeSeconds();
        if (_lastSecond == second) return;
        _lastSecond = second;
        var live = _home.Rooms;
        var liveIds = live.Select(r => r.Room.Id).ToHashSet();
        foreach (long closed in _sentStage.Keys.Concat(_eventDay.Keys).Where(id => !liveIds.Contains(id)).Distinct().ToArray())
        {
            _sentStage.Remove(closed);
            Forget(closed);
        }
        if (live.Count == 0) return;
        long day = Today();
        // Pitfall 1: home rooms exist at any hour. Outside the event window nothing is pushed, except the Finish of
        // an event this room actually saw today (stages 1-4).
        int stage = _schedule.IsOpen(116) ? CurrentStage() : 5;
        if (stage is < 1 or > 5) return;
        foreach (var (guild, room) in live)
        {
            var (seenDay, seen) = _sentStage.GetValueOrDefault(room.Id);
            if (seenDay != day) seen = 0;
            if (stage == seen || stage == 5 && seen == 0) continue;
            _sentStage[room.Id] = (day, stage);
            bool fresh = seen == 0;
            _loop.TrackLegacyDetached("campfire stage", () => AdvanceAsync(guild, room, stage, day, fresh));
        }
    }

    /// <summary>Drop a room's transient fruit/boss bookkeeping (its actors are cleared separately).</summary>
    private void Forget(long roomId)
    {
        _eventDay.Remove(roomId);
        _bosses.Remove(roomId);
        foreach (var key in _fruitCreated.Keys.Where(k => k.Room == roomId).ToArray())
        { _fruitCreated.Remove(key); _fruitInitialKills.Remove(key); }
        foreach (var id in _fruitActors.Where(p => p.Value.Room == roomId).Select(p => p.Key).ToArray()) _fruitActors.Remove(id);
    }

    /// <summary>Pitfall 2: a long-lived home room starts each event day (and ends each event) without the previous
    /// event's fruit, boss or their actors (clearing the actors is inferred).</summary>
    private async Task ResetAsync(InstanceService.Room room, long? day)
    {
        Forget(room.Id);
        await _instances.ClearMonstersAsync(room);
        if (day is { } current && !room.Closed) _eventDay[room.Id] = current;
    }

    private async Task AdvanceAsync(long guild, InstanceService.Room room, int stage, long day, bool fresh)
    {
        if (room.Closed) return;
        if (stage == 5) await ResetAsync(room, null);
        else if (fresh) await ResetAsync(room, day);
        var info = StageInfo(guild);
        var seeds = new AoIViewChangeData();
        // Members already inside when the event starts get the fire with its first stage.
        if (fresh && stage < 5 && FirePoint() is { } fire) AddNpc(seeds, room, 413, 7020004, fire);
        if (stage == 1) AddSeeds(seeds, room);
        else if (stage is 2 or 5)
            for (int i = 0; i < MaxSeeds(); i++) seeds.LeaveAoi.Add((ulong)Uid(room, 1001 + i));
        // (inferred) The fire belongs to the event: after Finish the home is empty again, as for a later visitor.
        if (stage == 5) seeds.LeaveAoi.Add((ulong)Uid(room, 413));
        foreach (var member in room.ActiveMembers)
        {
            var session = member.Session;
            // Visitors below the event's level see an empty home, as on entry (LoadedAsync).
            if (session.SceneEntry != Dungeon || session.SceneLoading || !Eligible(session)) continue;
            if (seeds.Bases.Count > 0 || seeds.LeaveAoi.Count > 0)
                await session.SendGameAsync(GameAoiSink.SyncAoiView, seeds);
            await session.SendGameAsync(Stage, info);
            if (stage < 5) MarkJoined(session);
            if (stage == 5) await session.SendGameAsync(Finish, new GuildCampFireFinishNotify());
        }
        if (stage is >= 2 and <= 4) await DrawAsync(guild, room, stage);
        if (stage is >= 2 and <= 4) await SpawnFruitCohortAsync(guild, room, stage);
        if (stage == 4) await SpawnBossAsync(guild, room);
    }

    private (int Template, int Count)? FruitPlan(int stage)
    {
        var row = _data.Table("CampFireAffixNewConfig").Values.FirstOrDefault(r =>
            GameData.Int(r, "_iPhase") == stage && GameData.Int(r, "_iActionType") == 25);
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("_iActionParams", out var args)
            || args.ValueKind != JsonValueKind.Array || args.GetArrayLength() < 3
            || args[0].ValueKind != JsonValueKind.Array || args[0].GetArrayLength() == 0
            || args[1].ValueKind != JsonValueKind.Array || args[1].GetArrayLength() == 0
            || args[2].ValueKind != JsonValueKind.Array || args[2].GetArrayLength() == 0
            || GameData.Number(args[0][0]) != 22) return null;
        int template = (int)(GameData.Number(args[1][0]) ?? 0), count = (int)(GameData.Number(args[2][0]) ?? 0);
        return template > 0 && count > 0 && _data.Row("NPCConfig", template) != null ? (template, count) : null;
    }

    private async Task SpawnFruitCohortAsync(long guild, InstanceService.Room room, int stage)
    {
        if (room.Closed || _fruitCreated.ContainsKey((room.Id, stage)) || FruitPlan(stage) is not { } plan) return;
        var points = SeedPoints();
        if (points.Count == 0) return;
        int killed = Read(guild).FruitKills.GetValueOrDefault(stage);
        int cap = _data.Table("PublicParamConfig").TryGetValue("CampfireMonsterLimit", out var limit)
            ? Math.Clamp((int)(GameData.Number(limit) ?? 10), 1, plan.Count) : Math.Min(10, plan.Count);
        _fruitCreated[(room.Id, stage)] = 0;
        _fruitInitialKills[(room.Id, stage)] = killed;
        for (int i = 0; i < Math.Min(cap, plan.Count - killed); i++)
            await SpawnOneFruitAsync(guild, room, stage, plan);
    }

    private async Task<MonsterUnit?> SpawnOneFruitAsync(long guild, InstanceService.Room room, int stage,
        (int Template, int Count) plan)
    {
        int created = _fruitCreated.GetValueOrDefault((room.Id, stage));
        if (created >= plan.Count - _fruitInitialKills.GetValueOrDefault((room.Id, stage))) return null;
        var points = SeedPoints();
        if (points.Count == 0) return null;
        var source = points[created % points.Count];
        // (inferred) Action 25's 40 actors use authored group 22; small offsets separate repeated cycles.
        float angle = created * 2.3999632f, radius = created < points.Count ? 0 : 1f;
        var at = new MapPoint(source.X + radius * MathF.Cos(angle), source.Y,
            source.Z + radius * MathF.Sin(angle), source.Face);
        var actor = await _instances.SpawnAsync(room, plan.Template, at);
        if (actor == null) return null;
        _fruitCreated[(room.Id, stage)] = created + 1;
        _fruitActors[actor.Uid] = (guild, room.Id, stage);
        return actor;
    }

    private async Task SpawnBossAsync(long guild, InstanceService.Room room)
    {
        if (room.Closed || _bosses.ContainsKey(room.Id) || Read(guild).BossDead || FirePoint() is not { } point)
            return;
        // CampFireAffixNewConfig action 15 replaces the fire-side 7020007 actor with Angeling 7020012.
        // Its phase=2 conflicts with client PublicParamConfig.CampfireFireBossStage=4; use the client-visible
        // boss stage for timing, and the action's configured attackable NPC for identity (inferred reconciliation).
        if (_data.Table("CampFireAffixNewConfig").Values.All(r =>
                GameData.Int(r, "_iActionType") != 15 || !r.TryGetProperty("_iActionParams", out var args)
                || args.ValueKind != JsonValueKind.Array || args.GetArrayLength() < 2
                || args[1].ValueKind != JsonValueKind.Array || args[1].GetArrayLength() == 0
                || GameData.Number(args[1][0]) != 7020012)) return;
        if (await _instances.SpawnAsync(room, 7020012, point) is { } monster) _bosses[room.Id] = monster;
    }

    private async Task KilledAsync(GameSession killer, MonsterUnit monster)
    {
        if (_fruitActors.TryGetValue(monster.Uid, out var fruit))
        {
            if (killer.Role == null || GuildId(killer) != fruit.Guild || RoomFor(killer) is not { } fruitRoom
                || fruitRoom.Id != fruit.Room || fruitRoom.Id != monster.InstanceId) return;
            var role = killer.Role;
            var attachment = killer.Attachment;
            using var ownerScope = await EnterWithGuildGateAsync("campfire/fruit kill", fruit.Guild, [role.Id]);
            try
            {
                if (!ReferenceEquals(killer.Role, role) || !ReferenceEquals(killer.Attachment, attachment)
                    || GuildId(killer) != fruit.Guild || RoomFor(killer)?.Id != fruit.Room) return;
                if (!_fruitActors.TryGetValue(monster.Uid, out var current) || current != fruit) return;
                var state = Read(fruit.Guild);
                state.FruitKills[fruit.Stage] = state.FruitKills.GetValueOrDefault(fruit.Stage) + 1;
                await SaveAsync(fruit.Guild, state, killer);
                _fruitActors.Remove(monster.Uid);
                if (Open(killer) && CurrentStage() == fruit.Stage && FruitPlan(fruit.Stage) is { } plan
                    && await SpawnOneFruitAsync(fruit.Guild, fruitRoom, fruit.Stage, plan) is { } child)
                {
                    var notice = new GuildCampFireCreateMonsterFruitNotify { DeadPosiiton = monster.Pos };
                    notice.Infos.Add(new CreateMonsterFruitInfo { NpcGuid = child.Uid, CreatePosition = child.Pos });
                    foreach (var member in fruitRoom.ActiveMembers)
                    {
                        var session = member.Session;
                        if (session.SceneEntry == Dungeon && !session.SceneLoading)
                            await session.SendGameAsync(Fruit, notice);
                    }
                }
            }
            finally { Gate(fruit.Guild).Release(); }
            return;
        }
        if (killer.Role == null || RoomFor(killer) is not { } room || room.Id != monster.InstanceId
            || !_bosses.TryGetValue(room.Id, out var boss) || boss.Uid != monster.Uid || !Open(killer)
            || GuildId(killer) is not { } guild) return;
        var bossRole = killer.Role;
        var bossAttachment = killer.Attachment;
        using var bossScope = await EnterWithGuildGateAsync("campfire/boss kill", guild,
            room.ActiveMembers.Select(m => m.Session.Role?.Id ?? 0));
        try
        {
            if (!ReferenceEquals(killer.Role, bossRole) || !ReferenceEquals(killer.Attachment, bossAttachment)
                || GuildId(killer) != guild || RoomFor(killer)?.Id != room.Id) return;
            var state = Read(guild);
            if (state.BossDead) return;
            state.BossDead = true;
            await SaveAsync(guild, state, killer);
            _bosses.Remove(room.Id);
            foreach (var member in room.ActiveMembers)
            {
                var session = member.Session;
                if (session.SceneEntry == Dungeon && !session.SceneLoading)
                {
                    await session.SendGameAsync(BossDead, new GuildCampFireBossDeadNotify());
                    // (inferred) Fireworks accompany the configured boss-clear celebration.
                    await session.SendGameAsync(Hurray, new GuildCampFireHurrayNotify
                    { HurrayType = (GuildCampFireHurrayType)3 });
                }
            }
        }
        finally { Gate(guild).Release(); }
    }

    private async Task DrawAsync(long guild, InstanceService.Room room, int stage)
    {
        using var scope = await EnterWithGuildGateAsync("campfire/draw", guild,
            room.ActiveMembers.Select(m => m.Session.Role?.Id ?? 0));
        try
        {
            var state = Read(guild);
            if (state.DrawnStages.Contains(stage)) return;
            var present = room.ActiveMembers.Select(m => m.Session)
                .Where(s => s.Role != null && s.InstanceId == room.Id && !s.SceneLoading
                    && (state.Seeds.ContainsKey(s.Role.Id) || state.FuelCount.ContainsKey(s.Role.Id)))
                .ToArray();
            if (present.Length == 0) return;
            // CampFireAffixNewConfig action 34 at phases 2/3/4 names three BuffConfig rewards and 2500/10000.
            // With its probability table empty in the original Lua, this is an (inferred) single eligible draw.
            var row = _data.Table("CampFireAffixNewConfig").Values.FirstOrDefault(r =>
                GameData.Int(r, "_iPhase") == stage && GameData.Int(r, "_iActionType") == 34);
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("_iActionParams", out var args)
                || args.ValueKind != JsonValueKind.Array || args.GetArrayLength() < 2) return;
            var buffIds = args.EnumerateArray().Skip(1).Where(a => a.ValueKind == JsonValueKind.Array && a.GetArrayLength() >= 1)
                .Select(a => (int)(GameData.Number(a[0]) ?? 0)).Where(id => _data.Row("BuffConfig", id) != null).ToArray();
            if (buffIds.Length == 0) return;
            var winner = present[Random.Shared.Next(present.Length)];
            int buff = buffIds[Random.Shared.Next(buffIds.Length)];
            state.DrawnStages.Add(stage);
            await SaveAsync(guild, state, winner);
            await _buffs.AddAsync(winner, buff, 0, winner.Role!.Id);
            var notice = new GuildCampFireLuckyPlayerNotify { AwardLevel = Level(state.Exp) };
            notice.PlayerList.Add(new GuildCampFireLuckyPlayerInfo { PlayerId = winner.Role.Id, BuffId = buff });
            foreach (var member in room.ActiveMembers)
            {
                var session = member.Session;
                if (session.SceneEntry == Dungeon && !session.SceneLoading) await session.SendGameAsync(Lucky, notice);
            }
        }
        finally { Gate(guild).Release(); }
    }
}
