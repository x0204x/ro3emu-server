using System.Runtime.CompilerServices;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Spirit Tower: 5065 progress, 32805 info, 32801 private floor entry,
/// 32802 authoritative settlement, 32807 server first passes and 32808 milestone claims. World/Combat own actors
/// and damage; CharacterService owns the real 5009 rewards. No sweep payout or seasonal progress reset is invented.
/// </summary>
public sealed partial class TowerService
{
    public const uint TowerSync = 5065, Start = 32801, End = 32802, SetPets = 32803, Sweep = 32804, Info = 32805;
    public const uint PetDeadState = 32809;
    public const uint FirstPassRange = 32807, ClaimFirstPass = 32808, DungeonInfo = 30401, LeaveDungeon = 30402;

    private readonly GameData _data;
    private long _nextPetUid = TowerPetUnit.UidBase;
    private readonly RoleStore _roles;
    private readonly AccountStore _accounts;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly SeasonService _seasons;
    private readonly SceneService _scene;
    private readonly WorldData _worldData;
    private readonly World _world;
    private readonly InstanceService _instances;
    private readonly MonsterService _monsters;
    private readonly CombatService _combat;
    private readonly BuffService _buffs;
    private readonly GameLoop _loop;
    private readonly TimeProvider _clock;
    private readonly ILogger<TowerService> _log;
    public TowerService(GameData data, RoleStore roles, AccountStore accounts, CharacterService character, FunctionService functions,
        SeasonService seasons, SceneService scene, WorldData worldData, World world, InstanceService instances, MonsterService monsters,
        CombatService combat, BuffService buffs, GameLoop loop, PetFollowerService followers, TimeProvider clock, ILogger<TowerService> log)
    {
        this._buffs = buffs;
        this._data = data;
        this._roles = roles;
        this._accounts = accounts;
        this._character = character;
        this._functions = functions;
        this._seasons = seasons;
        this._scene = scene;
        this._worldData = worldData;
        this._world = world;
        this._instances = instances;
        this._monsters = monsters;
        this._combat = combat;
        this._loop = loop;
        this._clock = clock;
        this._log = log;
        // Owner-bound pets must leave before InstanceService removes their owner (LC_Unit_Pet.Destroy).
        scene.Leaving.Insert(scene.Leaving.IndexOf(instances.AbandonAsync), AbandonAsync);
        scene.Loaded.Add(followers.RefreshAsync); // suppress the passive projection in tower; restore it on return
        scene.Loaded.Add(LoadedAsync);
        RegisterSuppression();
        combat.Killed.Add(KilledAsync);
        combat.BeforeKilled.Insert(0, BeforeTerminalKillAsync);
        combat.PlayerDied.Add(PlayerDiedAsync);
        combat.TowerPetDied.Add(PetDiedAsync);
    }

    public sealed record Spawn(int NpcId, MapPoint Point, int DelayMs);

    /// <summary>Session-only attempt; reconnect abandons it and returns to the saved world position (inferred).</summary>
    public sealed class Attempt
    {
        public required InstanceService.Room Room { get; init; }
        public long InstanceId => Room.Id;
        public required JsonElement Floor { get; init; }
        public required int PlayBook { get; init; }
        public required int SeasonId { get; init; }
        public required IReadOnlyList<Spawn> Spawns { get; init; }
        public int HpRatio { get; init; } = 10000;
        public int DamageRatio { get; init; } = 10000;
        /// <summary>The PowerSuppressConfig buff put on every floor monster and its damage changes (TowerService.PowerSuppress).</summary>
        internal Suppression Suppressed { get; init; }
        public IReadOnlyList<MonsterUnit> Monsters => Room.Monsters;
        public List<TowerPetUnit> Pets { get; } = new();
        public HashSet<int> DeadPetIds { get; } = new();
        public HashSet<long> Killed { get; } = new();
        public bool Loaded { get; set; }
        public bool Settled { get; set; }
        public bool ClearCommitted { get; set; }
        public bool ClearRecord { get; set; }
        public int ClearGoodsCount { get; set; }
        public long ClearNow { get; set; }
        public int ClearDuration { get; set; }
        public GetRewardNotify? ClearReward { get; set; }
        public RewardSettlement? ClearSettlement { get; set; }
        public MonsterUnit? TerminalMonster { get; set; }
        public TimeSpan StartedAt { get; set; }
        public long StartTimeMs { get; set; }
    }

    private bool Open(GameSession s) => s.Role != null && s.Tower != null && s.Progress != null
        && s.SeasonState is { } state && state.SeasonId == _seasons.SeasonId && state.PlayBookId == _seasons.PlayBook
        && _functions.IsOpen(s, 89) && _functions.IsOpen(s, 5001);

    /// <summary>PetFloorConfig is an array. Lookup and sorting use its explicit _iID / _iFloor domains.</summary>
    private List<JsonElement> Floors()
    {
        int playBook = _seasons.PlayBook;
        return _data.Table("PetFloorConfig").Values.Where(r => FitsPlayBook(r, playBook))
            .OrderBy(r => GameData.Int(r, "_iFloor")).ToList();
    }

    /// <summary>LC_SeasonHelper.GetMultiverseFitSeason: empty/[0], explicit membership, or [minimum,-1].</summary>
    internal static bool FitsPlayBook(JsonElement row, int playBook)
    {
        if (!row.TryGetProperty("_kMultiverseArray", out var books) || books.ValueKind != JsonValueKind.Array
            || books.GetArrayLength() == 0 || books.GetArrayLength() == 1 && GameData.Number(books[0]) == 0) return true;
        if (books.GetArrayLength() > 1 && GameData.Number(books[1]) == -1) return playBook >= GameData.Number(books[0]);
        return books.EnumerateArray().Any(b => GameData.Number(b) == playBook);
    }

    private JsonElement? Floor(long id) => Floors().Where(r => GameData.Int(r, "_iID") == id).Select(r => (JsonElement?)r).FirstOrDefault();
    private int Cleared(GameSession s) => Floor(s.Tower!.LayerConfID) is { } f ? (int)GameData.Int(f, "_iFloor") : 0;

    /// <summary>The highest Spirit Tower floor this role has cleared (PetFloorConfig._iFloor of its record), 0 without one.</summary>
    public int HighestClearedFloor(GameSession s) => s.Tower == null ? 0 : Cleared(s);

    /// <summary>After a committed floor clear is applied to the session (achievement 101014); listeners post their work.</summary>
    public List<Action<GameSession>> FloorCleared { get; } = new();
    /// <summary>After a floor attempt's 32802 result, won or lost (quest targets 101007 / 101010); listeners post.</summary>
    public List<Action<GameSession, bool>> Settled { get; } = new();
    private Task SyncAsync(GameSession s) => CharacterService.SyncAsync(s, TowerSync, s.Tower!.Clone());
    private async Task SaveAsync(GameSession s, DBClimTower next)
    {
        var bytes = next.ToByteArray();
        if (_roles.BlobUnchanged(s.Role!.Id, "tower", bytes)) { s.Tower = next; return; } // a login's normalization
        var transaction = _roles.NewTransaction();
        transaction.RoleBlob(s.Role.Id, "tower", bytes);
        await _roles.CommitAsync(transaction, "tower/state", _ => s.Tower = next);
    }

    /// <summary>
    /// The client compares floor numbers after shifting Unix seconds by its fixed UTC+8 offset and the
    /// PetFloorRefreshTime (05:00) boundary. PetFloorConfig applies the daily reduction to attribute
    /// 1011/per_maxhp and 215/per_dmg. Applying those rates to server actors is (inferred).
    /// </summary>
    internal static int DifficultyRatio(JsonElement floor, int attribute, long challengeMs, DateTimeOffset now)
    {
        if (challengeMs <= 0) return 10000;
        if (!floor.TryGetProperty("_kDynamicDifficulty", out var modifiers) || modifiers.ValueKind != JsonValueKind.Array)
            return 10000;
        foreach (var tuple in modifiers.EnumerateArray())
        {
            if (tuple.ValueKind != JsonValueKind.Array || tuple.GetArrayLength() < 3
                || GameData.Number(tuple[0]) != attribute) continue;
            long step = Math.Max(0, (long)(GameData.Number(tuple[1]) ?? 0));
            long cap = Math.Clamp((long)(GameData.Number(tuple[2]) ?? 0), 0, 9999);
            // Lua_TimeHelper.GetPassDay floors (seconds + 28800)/86400; caller first subtracts 5 hours.
            long dayNow = (long)Math.Floor((now.ToUnixTimeSeconds() + 3 * 3600) / 86400d);
            long dayThen = (long)Math.Floor((challengeMs / 1000 + 3 * 3600) / 86400d);
            long days = Math.Clamp(dayNow - dayThen, 0, 9999); // future saved timestamps do not strengthen a monster
            return (int)(10000 - Math.Min(days * step, cap));
        }
        return 10000;
    }

    /// <summary>Four numeric party positions are mandatory even with no owned pets (tower DAL §1.1).</summary>
    public async Task LoginAsync(GameSession s)
    {
        using var held = await CharacterService.Lock(s);
        var next = _roles.LoadTower(s.Role!.Id) is { } saved ? DBClimTower.Parser.ParseFrom(saved) : new DBClimTower
        {
            IsNewbeeInit = true, ChallengeTime = _clock.GetUtcNow().ToUnixTimeMilliseconds(), // initialization instant (inferred)
        };
        while (next.PetIds.Count < 4) next.PetIds.Add(0);
        while (next.PetIds.Count > 4) next.PetIds.RemoveAt(4);
        next.IsCanSweep = false; // All exported _iDailyReward values are zero; entitlement is unproven.
        await SaveAsync(s, next);
        await SyncAsync(s);
    }

    public Task InfoAsync(GameSession s)
    {
        var info = new ClimTowerInfoRsp { Result = Open(s) ? 0 : ErrorCodes.FeatureUnavailable };
        if (s.Tower is { } t)
        {
            info.Layer = Cleared(s);
            info.PetIds.Add(t.PetIds);
            info.CurDay = t.CurDay;
            info.PassedMaxLayerConfID = t.LayerConfID;
            info.LastLayerConfID = t.LastLayerConfID;
            info.DurationTime = Floor(t.LayerConfID) is { } f ? Math.Max(0, (int)GameData.Int(f, "_iTimeLimit") - t.RemainDuration) : 0;
        }
        return s.SendGameAsync(Info, info);
    }

    /// <summary>32803 is a full four-position replacement, independent of the ordinary 5017 pet plan.
    /// Tower slots use PetFightFieldNum, PublicParam levels and PetSetLimitConfig._iCondition as the battle UI does.
    /// Refusal returns the saved formation, never an uncommitted client echo (inferred policy).</summary>
    public async Task SetPetsAsync(GameSession s, SetClimTowerPetsReq req)
    {
        using var held = await CharacterService.Lock(s);
        // Mid-fight edits would otherwise despawn/recreate an injured actor at full HP. The UI permits
        // formation requests, but a combat-time swap contract is unproven; reject until settlement (inferred).
        int code = s.TowerAttempt is { Settled: false } ? ErrorCodes.PetOperation
            : FormationCode(s, req.Pets.ToArray());
        if (code == 0)
        {
            var next = s.Tower!.Clone();
            next.PetIds.Clear();
            next.PetIds.Add(req.Pets);
            await SaveAsync(s, next);
        }
        var reply = new SetClimTowerPetsRes { Result = code };
        if (s.Tower != null) reply.Pets.Add(s.Tower.PetIds);
        await s.SendGameAsync(SetPets, reply);
        if (code == 0)
        {
            await SyncAsync(s);
        }
    }

    private int FormationCode(GameSession s, IReadOnlyList<int> ids)
    {
        if (!Open(s) || s.Pets == null) return ErrorCodes.FeatureUnavailable;
        if (ids.Count != 4) return ErrorCodes.Parameter;
        if (ids.Where(id => id != 0).Distinct().Count() != ids.Count(id => id != 0))
            return ErrorCodes.PetAlreadyAssigned;
        for (int slot = 0; slot < ids.Count; slot++)
        {
            int id = ids[slot];
            if (id == 0) continue;
            if (_data.Row("PetBaseConfig", id) is not { } pet || _data.Row("NPCConfig", GameData.Int(pet, "_iMonster")) is not { } npc
                || GameData.Int(npc, "_iNPCType") != 7) return ErrorCodes.PetNoConfig;
            if (!s.Pets.ListPet.ContainsKey(id)) return ErrorCodes.PetNotOwned;
            if (!TowerPetUnit.CanSpawn(_data, s.Pets, id)) return ErrorCodes.PetNoConfig;
            if (!TowerSlotOpen(s, slot + 1)) return ErrorCodes.PetSetupSlot;
        }
        return 0;
    }

    private bool TowerSlotOpen(GameSession s, int slot)
    {
        int[] levels = [33, 33, 45, 50]; // PublicParamConfig.PetFightFieldUnlockLv.
        return slot >= 1 && slot <= levels.Length && s.Progress!.Level >= levels[slot - 1]
            && _data.Row("PetSetLimitConfig", slot) is { } row
            && _functions.ConditionMet(s, GameData.Int(row, "_iCondition"));
    }

    /// <summary>First two living chosen IDs fight; later positions replace a dead combatant in order (inferred).
    /// The DAL's four saved positions remain unchanged when a substitute enters.</summary>
    private async Task ReconcilePetsAsync(GameSession s, Attempt a)
    {
        if (!Active(s, a) || s.Pets == null || s.Tower == null) return;
        int capacity = Math.Clamp((int)GameData.Int(a.Floor, "_iPetOnBattle"), 0, 4);
        var desired = s.Tower.PetIds.Where(id => id != 0 && !a.DeadPetIds.Contains(id)
            && s.Pets.ListPet.ContainsKey(id)).Take(capacity).ToArray();
        foreach (var pet in a.Pets.Where(p => !desired.Contains(p.PetId)).ToArray())
        {
            await _world.RemoveUnitAsync(pet);
            a.Pets.Remove(pet);
        }
        foreach (int id in desired)
        {
            if (a.Pets.Any(p => p.PetId == id)) continue;
            int position = Array.IndexOf(desired, id);
            var actor = TowerPetUnit.Create(_data, s.Pets, s, _world, _monsters, _combat,
                Interlocked.Increment(ref _nextPetUid), id, a.InstanceId,
                s.Position.X + (position == 0 ? -2f : 2f), s.Position.Y, s.Position.Z + 1f);
            if (actor == null) continue; // Saved roster may predate the current pet table; validation is at 32803.
            a.Pets.Add(actor);
            await _world.AddUnitAsync(actor);
        }
    }

    private async Task PetDiedAsync(TowerPetUnit pet)
    {
        var s = pet.Owner;
        if (s.TowerAttempt is not { } a || !Active(s, a) || !a.Pets.Contains(pet)) return;
        a.DeadPetIds.Add(pet.PetId);
        await s.SendGameAsync(PetDeadState, new ClimbTowerSyncPetDeadStateRes
        {
            PetDeadState = { [pet.PetId] = 1 },
        });
        await _world.RemoveUnitAsync(pet);
        a.Pets.Remove(pet);
        await ReconcilePetsAsync(s, a);
    }

    /// <summary>
    /// A configured floor up to highest-clear + 1 may be entered/replayed (inferred accessibility policy).
    /// Continuation requires a settled attempt and allocates another isolated instance (inferred), with the
    /// isInClimTower flag matching the session. Success follows the shared 70010/30005 pipeline (inferred ordering).
    /// </summary>
    public async Task StartAsync(GameSession s, ClimTowerStartReq req)
    {
        var floor = Floor(req.Layer);
        int error = !Open(s) ? ErrorCodes.FeatureUnavailable
            : s.SceneLoading ? ErrorCodes.SwitchingScenes
            : s.Dead || s.InstanceId != 0 && s.TowerAttempt == null ? ErrorCodes.FeatureUnavailable
            : floor == null ? ErrorCodes.Parameter
            : s.Progress!.Level < GameData.Int(floor.Value, "_iLevelLimit") ? ErrorCodes.FeatureUnavailable
            : GameData.Int(floor.Value, "_iFloor") > Cleared(s) + 1 ? ErrorCodes.Parameter
            : req.IsInClimTower != (s.TowerAttempt != null) || s.TowerAttempt is { Settled: false } ? ErrorCodes.Parameter : 0;
        // A saved formation can outlive ownership/level changes; entry must apply the same slot rules as 32803.
        if (error == 0) error = FormationCode(s, s.Tower!.PetIds.ToArray());
        long mapId = floor is { } f ? GameData.Int(f, "_iMapID") : 0;
        var map = error == 0 ? _worldData.Map(mapId) : null;
        var spawns = error == 0 && map != null ? Expand(floor!.Value, map) : null;
        if (error == 0 && (map?.Born == null || spawns == null || spawns.Count == 0)) error = ErrorCodes.SceneMissing;
        // Validate the real payout before permitting a clear; previews are never a fallback grant.
        if (error == 0 && Award(GameData.Int(floor!.Value, "_iFirstReward")) == null) error = ErrorCodes.FeatureUnavailable;
        await s.SendGameAsync(Start, new ClimTowerStartRes { Result = error });
        if (error != 0) return;

        var room = _instances.Create(mapId, [s]);
        bool nextFloor = GameData.Int(floor!.Value, "_iFloor") == Cleared(s) + 1;
        long challengeMs = nextFloor ? s.Tower!.ChallengeTime : 0;
        var now = _clock.GetUtcNow();
        int hpRatio = DifficultyRatio(floor.Value, 1011, challengeMs, now);
        var attempt = new Attempt
        {
            Room = room, Floor = floor.Value, Spawns = spawns!, PlayBook = _seasons.PlayBook, SeasonId = _seasons.SeasonId,
            HpRatio = hpRatio,
            DamageRatio = DifficultyRatio(floor.Value, 215, challengeMs, now),
            Suppressed = Suppress(s, floor.Value, hpRatio),
        };
        if (!await _instances.EnterAsync(room, s, reason: 4)) return;
        s.TowerAttempt = attempt;
        _log.LogInformation("{Name} starts tower config {Floor} instance {Instance}", s.Role!.Name, req.Layer, attempt.InstanceId);
    }

    /// <summary>Tuple delay is milliseconds from scene readiness; each occurrence expands all group points (inferred).</summary>
    internal IReadOnlyList<Spawn>? Expand(JsonElement floor, MapWorld map)
    {
        var result = new List<Spawn>();
        if (!floor.TryGetProperty("_kMonsterGroup", out var groups) || groups.ValueKind != JsonValueKind.Array) return null;
        foreach (var tuple in groups.EnumerateArray())
        {
            if (tuple.ValueKind != JsonValueKind.Array || tuple.GetArrayLength() < 3) return null;
            int group = (int)(GameData.Number(tuple[0]) ?? 0), npc = (int)(GameData.Number(tuple[1]) ?? 0);
            int delay = (int)(GameData.Number(tuple[2]) ?? -1);
            if (delay < 0 || _monsters.Template(npc) == null || !map.DungeonGroups.TryGetValue(group, out var points) || points.Count == 0) return null;
            result.AddRange(points.Select(p => new Spawn(npc, new MapPoint(p.X, p.Y, p.Z, p.Face), delay)));
        }
        return result;
    }

    private bool Active(GameSession s, Attempt a) => ReferenceEquals(s.TowerAttempt, a) && !a.Settled && !a.Room.Closed
        && a.Loaded && !s.SceneLoading && s.InstanceId == a.InstanceId && s.Role != null;

    private async Task LoadedAsync(GameSession s)
    {
        if (s.TowerAttempt is not { Loaded: false, Settled: false } a || s.InstanceId != a.InstanceId) return;
        a.Loaded = true;
        a.StartedAt = _loop.Now;
        a.StartTimeMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        int seconds = (int)GameData.Int(a.Floor, "_iTimeLimit");
        // Start stage 1 / Running at the accepted 30005 (inferred: region22 has no recovered server start action).
        await s.SendGameAsync(DungeonInfo, new DungeonBaseInfoNotify
        {
            DungeonEntry = s.SceneEntry, DungeonId = a.InstanceId, Stage = 1, Level = 1,
            InitData = new DungeonInitData { ClimbTowerInit = new ClimbTowerInit { LayerConfID = GameData.Int(a.Floor, "_iID") } },
            ChangeType = (DungeonBaseInfoNotify.Types.eChangeType)2,
            // Actual participant snapshot, not a dummy: LC_DunGeon_VarDefine.lua:236-240 defines 145 as
            // CDungeonPlayerUidList, consumed by LC_DunGeon_Base. Including it at tower startup is (inferred).
            Vars = { new DungeonVar { VarId = 145, Vi64 = { s.Role!.Id } } },
            StageModuleData = new DungeonStageModuleData
            {
                MapState = 1, Stage = 1, MapStateStartTime = a.StartTimeMs, MapStateEndTime = a.StartTimeMs + seconds * 1000L,
                StageStartTime = a.StartTimeMs, StageEndTime = a.StartTimeMs + seconds * 1000L,
                NextStageStartTime = 0, NextStageEndTime = 0,
            },
        });
        // 32809 empty map clears the previous floor's client death flags; this does not revive an actor.
        await s.SendGameAsync(PetDeadState, new ClimbTowerSyncPetDeadStateRes());
        await ReconcilePetsAsync(s, a);
        foreach (var spawn in a.Spawns)
        {
            if (spawn.DelayMs == 0) await SpawnAsync(s, a, spawn);
            else _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(spawn.DelayMs), () => SpawnAsync(s, a, spawn));
        }
        _loop.ScheduleLegacy(TimeSpan.FromSeconds(seconds), () => Active(s, a) ? SettleAsync(s, a, false) : Task.CompletedTask);
    }

    private async Task SpawnAsync(GameSession s, Attempt a, Spawn spawn)
    {
        if (!Active(s, a)) return;
        var monster = await _instances.SpawnAsync(a.Room, spawn.NpcId, spawn.Point, a.HpRatio, a.DamageRatio);
        if (monster == null) { await SettleAsync(s, a, false); return; }
        if (!Active(s, a)) return; // A close may finish while the actor's AOI publication awaits a viewer.
        if (a.Suppressed.Buff > 0)
        {
            _suppressed.AddOrUpdate(monster, new StrongBox<Suppression>(a.Suppressed));
            await _buffs.AddPermanentAsync(monster, a.Suppressed.Buff, monster.Uid);
            if (!Active(s, a)) return;
        }
        // Tower opponents engage the challenger at their scheduled spawn (inferred); shared AI/skills execute combat.
        _monsters.Engage(monster, s);
    }

    private Task KilledAsync(GameSession s, MonsterUnit monster)
    {
        if (s.TowerAttempt is not { } a || !Active(s, a) || monster.InstanceId != a.InstanceId
            || !a.Monsters.Contains(monster) || !a.Killed.Add(monster.Uid)) return Task.CompletedTask;
        if (_loop.Now - a.StartedAt >= TimeSpan.FromSeconds(GameData.Int(a.Floor, "_iTimeLimit"))) return SettleAsync(s, a, false);
        return a.Killed.Count == a.Spawns.Count ? SettleAsync(s, a, true) : Task.CompletedTask;
    }

    private Task PlayerDiedAsync(GameSession s) => s.TowerAttempt is { } a && Active(s, a) ? SettleAsync(s, a, false) : Task.CompletedTask;

    /// <summary>The final floor outcome shares its source receipt with goods, tower state and first-pass record
    /// before the monster's death can be published. Presentation waits for the ordinary killed callback.</summary>
    private Task BeforeTerminalKillAsync(GameSession s, MonsterUnit monster)
    {
        if (s.TowerAttempt is not { } a || !Active(s, a) || a.ClearCommitted
            || monster.InstanceId != a.InstanceId || !a.Monsters.Contains(monster)
            || a.Killed.Contains(monster.Uid)
            || a.Killed.Count + a.Monsters.Count(m => !a.Killed.Contains(m.Uid)
                && (ReferenceEquals(m, monster) || m.DeathDurable)) != a.Spawns.Count
            || _loop.Now - a.StartedAt >= TimeSpan.FromSeconds(GameData.Int(a.Floor, "_iTimeLimit"))) return Task.CompletedTask;
        if (a.PlayBook != _seasons.PlayBook || a.SeasonId != _seasons.SeasonId) return Task.CompletedTask;
        if (monster.RewardCommit != null) return Task.CompletedTask;
        int floor = (int)GameData.Int(a.Floor, "_iFloor"), limit = (int)GameData.Int(a.Floor, "_iTimeLimit");
        long config = GameData.Int(a.Floor, "_iID"), now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        int duration = Math.Clamp((int)(_loop.Now - a.StartedAt).TotalSeconds, 0, limit);
        var previous = s.Tower!;
        bool record = floor > Cleared(s);
        var goods = !previous.RewardRecord.TryGetValue(config, out bool paid) || !paid
            ? Award(GameData.Int(a.Floor, "_iFirstReward"))! : [];
        var next = previous.Clone();
        next.RewardRecord[config] = true;
        if (record)
        {
            next.LastLayerConfID = previous.LayerConfID;
            next.LayerConfID = config;
            next.RemainDuration = limit - duration;
            next.CompleteTime = now;
            next.ChallengeTime = now;
        }
        var first = new ClimbTowerFirstPassData
        {
            Layer = floor, HasFirstPass = true,
            PlayerId = s.Role!.Id, PlayerName = s.Role.Name, PassTime = now,
            Job = s.Progress!.Career, Gender = s.Role.Sex
        };
        monster.RewardSource ??= new RewardSourceId(_character.RewardRealmId,
            RewardSourceId.ProcessRun, monster.SpawnGeneration);
        var capturedTower = _accounts.ReadSync<RoleBlobSnapshot>(new RoleBlobSnapshotRead(s.Role.Id, "tower"));
        if (capturedTower.Value == null || !capturedTower.Value.AsSpan().SequenceEqual(previous.ToByteArray()))
            throw new OwnerRevisionConflictException();
        var settlement = monster.RewardSettlement = new RewardSettlement(
            monster.RewardSource.Key, "monster-kill", autoDecompose: true)
        { DeferPresentation = true };
        (long Guid, InvItem Item, int Stack)[] before = [];
        // Capture after ordinary loot is staged under the same owner lock. A new ordinary stack
        // still has GUID 0 here, so its object identity is retained until ApplyGuids publishes it.
        settlement.BeforeAdditionalGoods = () => before = s.Items
            .Select(item => (item.Guid, item, item.Stack)).ToArray();
        settlement.AdditionalGoods.AddRange(goods);
        settlement.Enlist = tx =>
        {
            tx.Guard(new RoleBlobGuardPlan(s.Role.Id, "tower", capturedTower));
            tx.RoleBlob(s.Role.Id, "tower", next.ToByteArray());
            tx.Add(new Ro3Emu.Server.Accounts.Persistence.TowerFirstPassWritePlan(floor, first.ToByteArray()));
        };
        settlement.Applied = () =>
        {
            s.Tower = next;
            foreach (var handler in FloorCleared)
                try { handler(s); }
                catch (Exception e) { _log.LogError(e, "Tower clear hook failed for {Role}", s.Role.Id); }
            var reward = new GetRewardNotify { TargetUid = s.Role.Id };
            var byGuid = before.Where(item => item.Guid > 0).ToDictionary(item => item.Guid, item => item.Stack);
            var byReference = new Dictionary<InvItem, int>(ReferenceEqualityComparer.Instance);
            foreach (var item in before) byReference[item.Item] = item.Stack;
            int PreviousStack(InvItem item) => byGuid.TryGetValue(item.Guid, out int old)
                ? old : byReference.GetValueOrDefault(item);
            foreach (var group in goods.GroupBy(g => g.Entry))
            {
                int remaining = group.Sum(g => g.Count);
                foreach (var item in s.Items.Where(item => item.Entry == group.Key &&
                    item.Stack > PreviousStack(item)))
                {
                    if (remaining == 0) break;
                    int count = Math.Min(remaining, item.Stack - PreviousStack(item));
                    var shown = _character.ItemInfoOf(item);
                    shown.Stack = count;
                    reward.Rewards.Add(shown);
                    remaining -= count;
                }
                if (remaining > 0 && _data.Row("ItemConfig", group.Key) is { } row
                    && GameData.Int(row, "_iType") == 1)
                    reward.Rewards.Add(_character.ItemInfoOf(new InvItem { Entry = group.Key, Stack = remaining }));
            }
            a.ClearCommitted = true;
            a.ClearRecord = record;
            a.ClearGoodsCount = goods.Count;
            a.ClearNow = now;
            a.ClearDuration = duration;
            a.ClearReward = reward;
        };
        a.ClearSettlement = settlement;
        a.TerminalMonster = monster;
        return Task.CompletedTask;
    }

    /// <summary>All scheduled actors dead before the deadline wins; death/timeout loses (inferred clear predicate).</summary>
    private async Task SettleAsync(GameSession s, Attempt a, bool won)
    {
        if (!Active(s, a)) return;
        if (!won && a.TerminalMonster is { } terminal && !a.Killed.Contains(terminal.Uid)
            && terminal.RewardCommit?.IsCompleted == false)
        {
            // The deadline can fire while the final lethal source is being committed. Let that
            // resolution finish before deciding whether the floor timed out.
            _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(50), () => Active(s, a)
                ? SettleAsync(s, a, false) : Task.CompletedTask);
            return;
        }
        if (!won && a.TerminalMonster is { } committedTerminal && a.ClearCommitted
            && !a.Killed.Contains(committedTerminal.Uid))
        {
            // A failed postcommit Apply retires its attachment; never send a timeout loss for
            // an already durable clear if the ordinary killed callback could not run.
            await AbandonAsync(s);
            return;
        }
        won &= a.PlayBook == _seasons.PlayBook && a.SeasonId == _seasons.SeasonId;
        a.Settled = true; // Reserve settlement before any await: duplicate kills/timeouts cannot grant twice.
        s.Attack = null;
        int floor = (int)GameData.Int(a.Floor, "_iFloor"), limit = (int)GameData.Int(a.Floor, "_iTimeLimit");
        // curTime/completeTime use Unix milliseconds (inferred); challengeTime's millisecond units are client-proven.
        long now = a.ClearCommitted ? a.ClearNow : _clock.GetUtcNow().ToUnixTimeMilliseconds();
        int duration = a.ClearCommitted ? a.ClearDuration : Math.Clamp((int)(_loop.Now - a.StartedAt).TotalSeconds, 0, limit);
        var t = s.Tower!;
        bool record = won && (a.ClearCommitted ? a.ClearRecord : floor > Cleared(s));
        var reward = a.ClearCommitted ? a.ClearReward! : new GetRewardNotify { TargetUid = s.Role!.Id };
        int result = 0;
        if (won && a.ClearCommitted)
        {
            // The shared kill source owns reward presentation; this handler emits the tower-specific
            // 32802 and 5065 packets after the committed source.
            if (a.ClearGoodsCount > 0)
                try { await _character.ItemsChangedAsync(s, stats: false); }
                catch (Exception e) { _log.LogWarning(e, "Tower item sync failed after commit for {Role}", s.Role!.Id); }
        }
        else if (won)
        {
            // A direct or stale killed callback cannot bypass the pre-death source receipt.
            _log.LogError("Tower final kill lacked a committed floor outcome for role {Role}", s.Role!.Id);
            won = record = false;
            result = ErrorCodes.Unknown;
        }
        await _instances.ClearMonstersAsync(a.Room);
        await RemovePetsAsync(a);
        // Result before 5065 preserves the DAL comparison with the previous floor (inferred order).
        await s.SendGameAsync(End, new ClimTowerEndNotify
        {
            Result = result, Layer = floor, CurTime = now, Duration = duration, RewardNotify = reward,
            ClimTowerResult = won, IsNewRecord = record, PassedMaxLayerConfID = t.LayerConfID,
        });
        if (won)
        {
            await SyncAsync(s);
            // A new cleared floor: quest targets read it (growth-manual 101014 CheckClimbTowerLayer).
            foreach (var handler in _character.StateChanged) handler(s);
        }
        _log.LogInformation("{Name} tower floor {Floor}: {Won}, {Duration}s", s.Role!.Name, floor, won, duration);
        if (result == 0)
            foreach (var handler in Settled)
                try { handler(s, won); }
                catch (Exception e) { _log.LogError(e, "Tower settlement hook failed for {Role}", s.Role.Id); }
    }

    /// <summary>Inclusive bounded range of actual catalog floor numbers, including genuine uncleared records.</summary>
    public Task FirstPassRangeAsync(GameSession s, QueryClimbTowerFirstPassRangeReq req)
    {
        var floors = Floors();
        int error = !Open(s) ? ErrorCodes.FeatureUnavailable : floors.Count == 0 || req.MinLayer < 1
            || req.MaxLayer < req.MinLayer || req.MaxLayer > GameData.Int(floors[^1], "_iFloor") ? ErrorCodes.Parameter : 0;
        var response = new QueryClimbTowerFirstPassRangeResp { Result = error };
        if (error == 0)
        {
            var records = _roles.LoadTowerFirstPasses(req.MinLayer, req.MaxLayer);
            foreach (var f in floors)
            {
                long n = GameData.Int(f, "_iFloor");
                if (n < req.MinLayer || n > req.MaxLayer) continue;
                response.Results.Add(records.TryGetValue(n, out var bytes) ? ClimbTowerFirstPassData.Parser.ParseFrom(bytes)
                    : new ClimbTowerFirstPassData { Layer = n, HasFirstPass = false });
            }
            if (response.Results.Count == 0) response.Result = ErrorCodes.Parameter;
        }
        return s.SendGameAsync(FirstPassRange, response);
    }

    /// <summary>
    /// Claim all contiguous recorded boss milestones above the watermark, including other players' wins (inferred
    /// eligibility/batching from the DAL red dot). Stop at a gap so a later first clear is never skipped forever.
    /// </summary>
    public async Task ClaimFirstPassAsync(GameSession s)
    {
        using var held = await CharacterService.Lock(s);
        int error = Open(s) ? 0 : ErrorCodes.FeatureUnavailable;
        long watermark = s.Tower?.ServerFirstPassClaimedLayer ?? 0;
        long previousWatermark = watermark;
        var goods = new List<(int Entry, int Count)>();
        if (error == 0)
        {
            var records = _roles.LoadTowerFirstPasses(watermark + 1, long.MaxValue);
            foreach (var f in Floors().Where(f => GameData.Int(f, "_iFloor") > watermark && GameData.Int(f, "_iServerFirstReward") > 0))
            {
                long floor = GameData.Int(f, "_iFloor");
                if (!records.ContainsKey(floor)) break;
                if (Award(GameData.Int(f, "_iServerFirstReward")) is not { } award) { error = ErrorCodes.FeatureUnavailable; break; }
                goods.AddRange(award);
                watermark = floor;
            }
            if (error == 0 && goods.Count == 0) error = ErrorCodes.Parameter;
        }
        if (error == 0)
        {
            var next = s.Tower!.Clone();
            next.ServerFirstPassClaimedLayer = watermark;
            try
            {
                var economy = _character.EconomyForOwnedExchange(s, "cd13e/tower-milestone",
                    $"tower-milestone/{s.Role!.Id}/{previousWatermark}/{watermark}", [], goods,
                    new { Previous = previousWatermark, Claimed = watermark, Goods = goods.Select(g => new { g.Entry, g.Count }).ToArray() });
                var granted = await _character.ExchangeLockedAsync(s, [], goods, popup: false, sync: false,
                    persist: tx => tx.RoleBlob(s.Role.Id, "tower", next.ToByteArray()),
                    committed: () => s.Tower = next, economy: economy);
                if (granted == null) error = ErrorCodes.ItemsShort;
                else
                {
                    await _character.ItemsChangedAsync(s, stats: false);
                    await SyncAsync(s);
                }
            }
            catch (SqliteException e)
            {
                _log.LogError(e, "Tower milestone claim could not be saved for role {Role}", s.Role!.Id);
                error = ErrorCodes.Unknown;
            }
        }
        await s.SendGameAsync(ClaimFirstPass, new ClimbTowerClaimFirstPassRewardRes
        {
            Result = error, ClaimedUpToLayer = s.Tower?.ServerFirstPassClaimedLayer ?? 0,
        });
    }

    /// <summary>Fixed _kAward only, resolved through the active playbook; no rewards from _kPreview.</summary>
    private List<(int Entry, int Count)>? Award(long group)
    {
        if (group == 0) return [];
        if (_data.Row("MultiverseGroupConfig", group) is not { } mg) return null;
        long id = GameData.Int(mg, $"_iValue_{_seasons.PlayBook}");
        if (id == 0) id = GameData.Int(mg, "_iValue_default");
        if (_data.Row("AwardConfig", id) is not { } award) return null;
        var rows = award.ValueKind == JsonValueKind.Array ? award.EnumerateArray().ToArray() : [award];
        var result = new List<(int, int)>();
        foreach (var row in rows)
        {
            if (!row.TryGetProperty("_kAward", out var entries) || entries.ValueKind != JsonValueKind.Array) return null;
            foreach (var tuple in entries.EnumerateArray())
            {
                if (tuple.ValueKind != JsonValueKind.Array || tuple.GetArrayLength() < 2) return null;
                int entry = (int)(GameData.Number(tuple[0]) ?? 0), count = (int)(GameData.Number(tuple[1]) ?? 0);
                if (count <= 0 || _data.Row("ItemConfig", entry) == null) return null;
                result.Add((entry, count));
            }
        }
        return result;
    }

    public async Task LeaveAsync(GameSession s)
    {
        var a = s.TowerAttempt;
        var member = a?.Room.MemberFor(s);
        var role = s.Role;
        bool valid = a != null && member != null && s.Role != null && !s.SceneLoading
            && s.InstanceId == a.InstanceId && s.SceneEntry == a.Room.SceneEntry
            && ReferenceEquals(_instances.Find(a.InstanceId), a.Room);
        await s.SendGameAsync(LeaveDungeon, new LeaveDungeonRsp { Result = valid ? 0 : ErrorCodes.SceneMissing });
        if (!valid || !ReferenceEquals(s.TowerAttempt, a) || !ReferenceEquals(s.Role, role)
            || s.InstanceId != a!.InstanceId || s.SceneEntry != a.Room.SceneEntry || s.SceneLoading
            || !ReferenceEquals(_instances.Find(a.InstanceId), a.Room) || a.Room.MemberFor(s) != member) return;
        // (inferred) Return recovery is armed before 70010 by the shared instance owner.
        await _instances.LeaveAsync(s, recoverDeadOnReturn: true);
    }

    /// <summary>Exit/disconnect/role switch cancels scheduled waves and clears the private map (inferred abandonment).</summary>
    public async Task AbandonAsync(GameSession s)
    {
        if (s.TowerAttempt is not { } a) return;
        a.Settled = true;
        s.TowerAttempt = null;
        s.Attack = null;
        await RemovePetsAsync(a);
        await _instances.AbandonAsync(a.Room, s);
    }

    private async Task RemovePetsAsync(Attempt a)
    {
        foreach (var pet in a.Pets.ToArray()) await _world.RemoveUnitAsync(pet);
        a.Pets.Clear();
    }
}
