using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class TerritoryWarsService
{
    private const long WorldPickupUidBase = 1L << 45;
    private const long WorldPickupUidEnd = 1L << 46;
    private const int SupplyCurrency = 133;
    private readonly SemaphoreSlim _worldGate = new(1, 1);
    private readonly HashSet<long> _worldMapsLoaded = new();
    private readonly Dictionary<long, WorldPickupUnit> _worldPickups = new();
    private readonly Dictionary<int, MonsterUnit> _worldActivities = new();
    private readonly Dictionary<int, long> _worldActivityOrdinals = new();
    private long _nextWorldPickupUid;
    private long _worldGeneration;
    internal Action? BeforeWorldPickupApplyForTest;
    internal Action? BeforeWorldDeathApplyForTest;

    /// <summary>Persisted together with the player's currency deduction; a dropped supply actor survives a restart.</summary>
    public sealed class WorldDrop
    {
        public long Id { get; set; }
        public long Scene { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public int Amount { get; set; }
    }

    /// <summary>Durable spawn-cycle identity and earliest configured respawn for a static supply slot.</summary>
    public sealed class WorldPickupOccurrence
    {
        public long Ordinal { get; set; }
        public long NextEligible { get; set; }
    }

    private sealed class WorldPickupUnit : WorldUnit
    {
        public required int NpcId { get; init; }
        public required int Amount { get; init; }
        public required int InteractionId { get; init; }
        public long DropId { get; init; }
        public string Slot { get; init; } = "";
        public long Ordinal { get; init; }
        public int RespawnSeconds { get; init; }
        public bool Taken { get; set; }

        public override EntityBase Entity() => new()
        {
            Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = 2,
            State = EntityState.Alive, NotifyType = EntityBase.Types.NotifyType.All,
            MovementInfo = new MovementInfo
            {
                Pos = Pos, Face = Face, ServerTime = MonsterService.NowMs,
                MoveType = MovementType.MoveTypeNone
            },
        };
    }

    private bool IsWorldTerritory(long sceneId) => _data.Row("MapConfig", sceneId) is { } map
        && GameData.Int(map, "_iSceneType") == 8 && Zones.Any(z => GameData.Int(z, "_iMapID") == sceneId);

    /// <summary>
    /// Public-world camp combat is confined to configured territory PvP maps. The attacker chooses Free or Guild;
    /// Peace cannot initiate, and guildmates cannot damage each other (inferred allegiance rule).
    /// </summary>
    public bool WorldHostile(GameSession a, GameSession b)
    {
        if (a.Role == null || b.Role == null || a.Role.Id == b.Role.Id || a.SceneLoading || b.SceneLoading
            || a.InstanceId != 0 || b.InstanceId != 0 || a.LineNumber != 1 || b.LineNumber != 1
            || a.WorldScene != b.WorldScene || !IsWorldTerritory(a.SceneEntry)
            || _data.Row("MapConfig", a.SceneEntry) is not { } map || !map.TryGetProperty("_kPVPMod", out var modes)
            || modes.ValueKind != JsonValueKind.Array || modes.GetArrayLength() == 0) return false;
        long ag = Own(a)?.Id ?? 0, bg = Own(b)?.Id ?? 0;
        if (ag != 0 && ag == bg) return false;
        int camp = a.TerritoryWars?.Camp ?? LoadRole(a.Role.Id).Camp;
        return camp == (int)EPvpRoleCampNum.Free || camp == (int)EPvpRoleCampNum.Guild && ag != 0;
    }

    /// <summary>Scene-editor supply groups use NPCType 2, so MonsterService's NPCType 1 spawner cannot represent them.</summary>
    // Persisted territory pickups/drops have only a config scene; the shared event runs on public line 1.
    private bool WorldScene(GameSession s) => s.Role != null && !s.SceneLoading && s.InstanceId == 0 && s.LineNumber == 1
        && IsWorldTerritory(s.SceneEntry);

    /// <summary>The territory gate's keys, for a scene load whose hooks write territory state (C/D 15): a world
    /// territory scene, or the loaded instance of the role's territory or siege battle (participant loaded flag).</summary>
    private Ownership.ReservationKey[] SceneKeys(GameSession s) =>
        WorldScene(s) || s.TerritoryBattle is { } battle && s.InstanceId == battle.Room.Id
            || s.SiegeBattle is { } siege && s.InstanceId == siege.Room.Id ? MutationKeys(s) : [];

    public async Task WorldLoadedAsync(GameSession s)
    {
        if (!WorldScene(s)) return;
        using var reservation = await EnterGateAsync("territory/world loaded", s);
        using var stagedTerritoryState = StageState();
        try
        {
            await _worldGate.WaitAsync();
            try
            {
                if (_worldMapsLoaded.Add(s.SceneEntry))
                {
                    if (_maps.Map(s.SceneEntry) is { } map)
                        foreach (var group in map.SpawnGroups.Where(g => g.Pool && g.Limits.Any(l => SupplyAmount(l.NpcId) > 0)))
                        {
                            // The group minima are its configured finite initial population (inferred selection).
                            var points = group.Points.OrderBy(_ => Random.Shared.Next()).ToArray();
                            int cursor = 0;
                            for (int limitIndex = 0; limitIndex < group.Limits.Count; limitIndex++)
                            {
                                var (npcId, min, _) = group.Limits[limitIndex];
                                if (SupplyAmount(npcId) <= 0) continue;
                                for (int i = 0; i < min && cursor < points.Length && cursor < group.Total; i++)
                                {
                                    var point = points[cursor++];
                                    var at = map.Grounded(new MapPoint(point.X, point.Y, point.Z, point.Face));
                                    string slot = $"{s.SceneEntry}:{group.Id}:{limitIndex}:{i}";
                                    var occurrence = state.WorldPickupOccurrences.GetValueOrDefault(slot);
                                    var unit = new WorldPickupUnit
                                    {
                                        Uid = WorldPickupUidBase | ++_nextWorldPickupUid, SceneEntry = s.SceneEntry,
                                        X = at.X, Y = at.Y, Z = at.Z, Face = at.Face,
                                        NpcId = npcId, Amount = SupplyAmount(npcId), InteractionId = 7000004,
                                        Slot = slot, Ordinal = occurrence?.Ordinal ?? 0,
                                        RespawnSeconds = Math.Max(1, group.RespawnSeconds),
                                    };
                                    if (occurrence?.NextEligible > _clock.Now)
                                    {
                                        long generation = _worldGeneration;
                                        _loop.ScheduleRuntime(TimeSpan.FromSeconds(occurrence.NextEligible - _clock.Now),
                                            () => RespawnWorldPickupAsync(unit, generation));
                                    }
                                    else await AddWorldPickupAsync(unit);
                                }
                            }
                        }
                    foreach (var drop in state.WorldDrops.Values.Where(d => d.Scene == s.SceneEntry))
                        await AddWorldDropActorAsync(drop);
                }
                foreach (var row in WorldLandRows(s.SceneEntry)) await SpawnWorldActivityAsync(row);
            }
            finally { _worldGate.Release(); }
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
    }

    /// <summary>Called by season reconciliation while the territory gate is held, before replacing the epoch state.</summary>
    private async Task ResetWorldAsync()
    {
        await _worldGate.WaitAsync();
        try
        {
            _worldGeneration++;
            foreach (var unit in _worldPickups.Values.ToArray()) await _world.RemoveUnitAsync(unit);
            foreach (var actor in _worldActivities.Values.ToArray()) await _world.RemoveUnitAsync(actor);
            _worldPickups.Clear(); _worldActivities.Clear(); _worldActivityOrdinals.Clear(); _worldMapsLoaded.Clear();
        }
        finally { _worldGate.Release(); }
        // Recreate the active maps against the new epoch after ReconcileSeasonAsync releases its gate.
        var present = _sessions.All().Where(s => !s.SceneLoading && s.InstanceId == 0 && s.LineNumber == 1
            && IsWorldTerritory(s.SceneEntry)).ToArray();
        foreach (var peer in present)
            _loop.PostOwned(peer, MutationKeys(peer),
                () => WorldLoadedAsync(peer), "territory/world reset");
    }

    private IEnumerable<JsonElement> WorldLandRows(long sceneId) => _data.Table("GVGLandActivityConfig").Values
        .Where(r => GameData.Int(r, "_iGamePlayType") == 1 && GameData.Int(r, "_iMapResourceID") == sceneId);

    private async Task SpawnWorldActivityAsync(JsonElement row)
    {
        int id = (int)Id(row);
        if (_worldActivities.ContainsKey(id)) return;
        long due = state.WorldActivityRespawns.GetValueOrDefault(id);
        if (due > _clock.Now)
        {
            long generation = _worldGeneration;
            _loop.ScheduleRuntime(TimeSpan.FromSeconds(due - _clock.Now), () => RespawnWorldActivityAsync(id, generation));
            return;
        }
        long sceneId = GameData.Int(row, "_iMapResourceID"), pointId = GameData.Int(row, "_iPointID");
        if (_maps.Map(sceneId) is not { } map) return;
        if (!map.Points.TryGetValue(pointId, out var point))
        {
            // (inferred) GVGLandActivityConfig requests 100001 on both maps; the exported editors
            // name the equivalent second monster point 1000001 (11: trap) and 10001 (36: spawn),
            // beside their black-hole point 100000/10000. Prefer an exact binding if data is corrected.
            long alias = (sceneId, pointId) switch { (30311, 100001) => 1000001, (30336, 100001) => 10001, _ => 0 };
            if (alias == 0 || !map.Points.TryGetValue(alias, out point)) return;
        }
        if (await _monsters.SpawnSingleAsync(sceneId, (int)GameData.Int(row, "_iNPCid"), point) is { } actor)
        {
            _worldActivities[id] = actor;
            _worldActivityOrdinals[id] = state.WorldActivityOccurrences.GetValueOrDefault(id);
        }
    }

    private async Task RespawnWorldActivityAsync(int id, long generation)
    {
        using var reservation = await EnterGateAsync("territory/world activity respawn");
        using var stagedTerritoryState = StageState();
        try
        {
            await _worldGate.WaitAsync();
            try
            {
                if (generation == _worldGeneration && _data.Row("GVGLandActivityConfig", id) is { } row
                    && _worldMapsLoaded.Contains(GameData.Int(row, "_iMapResourceID")))
                    await SpawnWorldActivityAsync(row);
            }
            finally { _worldGate.Release(); }
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
    }

    /// <summary>
    /// If the attacker leaves during an awaited damage hook, combat still kills the actor but cannot safely
    /// attribute its reward. Reconcile its configured respawn after the corpse has left the map (inferred).
    /// Caller holds the territory gate; waiting for corpse removal lets the ordinary Killed callback claim first.
    /// </summary>
    private async Task ReconcileWorldActorsLockedAsync()
    {
        await _worldGate.WaitAsync();
        try
        {
            foreach (var (id, actor) in _worldActivities.ToArray())
            {
                if (!actor.Dead || _world.Map(actor.WorldScene).Contains(actor)
                    || _data.Row("GVGLandActivityConfig", id) is not { } row) continue;
                long respawn = Math.Max(1, GameData.Int(row, "_iRespawnCD") / 1000);
                long ordinal = _worldActivityOrdinals.GetValueOrDefault(id);
                if (state.WorldActivityOccurrences.GetValueOrDefault(id) != ordinal)
                {
                    _worldActivities.Remove(id); _worldActivityOrdinals.Remove(id);
                    long wait = Math.Max(0, state.WorldActivityRespawns.GetValueOrDefault(id) - _clock.Now);
                    long knownGeneration = _worldGeneration;
                    _loop.ScheduleRuntime(TimeSpan.FromSeconds(wait), () => RespawnWorldActivityAsync(id, knownGeneration));
                    continue;
                }
                var next = Copy(state);
                next.WorldActivityRespawns[id] = _clock.Now + respawn;
                next.WorldActivityOccurrences[id] = checked(ordinal + 1);
                await SaveAsync(next);
                PublishCommitted(next);
                _worldActivities.Remove(id); _worldActivityOrdinals.Remove(id);
                long generation = _worldGeneration;
                _loop.ScheduleRuntime(TimeSpan.FromSeconds(respawn), () => RespawnWorldActivityAsync(id, generation));
            }
        }
        finally { _worldGate.Release(); }
    }

    private int SupplyAmount(int npcId)
    {
        if (npcId is not (4910101 or 4910102 or 4910103) || _data.Row("NPCConfig", npcId) is not { } npc) return 0;
        long group = GameData.Int(npc, "_iCAward");
        if (_data.Row("MultiverseGroupConfig", group) is not { } mv) return 0;
        long awardId = GameData.Int(mv, $"_iValue_{_clock.PlayBook}");
        if (awardId == 0) awardId = GameData.Int(mv, "_iValue_default");
        if (_data.Row("AwardConfig", awardId) is not { } raw) return 0;
        var award = raw.ValueKind == JsonValueKind.Array ? raw[0] : raw;
        if (!award.TryGetProperty("_kAward", out var goods) || goods.ValueKind != JsonValueKind.Array) return 0;
        return goods.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.Array && g.GetArrayLength() >= 2
            && GameData.Number(g[0]) == SupplyCurrency).Sum(g => (int)(GameData.Number(g[1]) ?? 0));
    }

    private async Task AddWorldPickupAsync(WorldPickupUnit unit)
    {
        _worldPickups[unit.Uid] = unit;
        await _world.AddUnitAsync(unit);
    }

    private Task AddWorldDropActorAsync(WorldDrop drop) => AddWorldPickupAsync(new WorldPickupUnit
    {
        Uid = WorldPickupUidBase | ++_nextWorldPickupUid, SceneEntry = drop.Scene,
        X = drop.X, Y = drop.Y, Z = drop.Z,
        NpcId = (int)Param("TerritoryWorldDeathDropItem", 4910104), Amount = drop.Amount,
        InteractionId = 491000301, DropId = drop.Id,
    });

    /// <summary>32700 pickup claim: validates server actor, distance and carrying limit, then commits one currency grant.</summary>
    public async Task<bool> WorldSharedAsync(GameSession s, uint id, ReadOnlyMemory<byte> body)
    {
        if (id != SceneService.NpcInteraction) return false;
        var req = NpcInteractionReq.Parser.ParseFrom(body.Span);
        if (req.NpcUid < WorldPickupUidBase || req.NpcUid >= WorldPickupUidEnd) return false;
        int code = await CollectWorldPickupAsync(s, req);
        await Send(s, id, new NpcInteractionRsp { Result = code, Req = req });
        return true;
    }

    private async Task<int> CollectWorldPickupAsync(GameSession s, NpcInteractionReq req)
    {
        using var reservation = await EnterGateAsync("territory/world pickup", s);
        using var stagedTerritoryState = StageState();
        try
        {
            using var held = await CharacterService.Lock(s);
            await _worldGate.WaitAsync();
            try
            {
                if (!_worldPickups.TryGetValue(req.NpcUid, out var unit) || unit.Taken) return ErrorCodes.NpcMissing;
                if (s.Role == null || s.Progress == null || s.Dead || s.SceneLoading || s.InstanceId != 0
                    || s.LineNumber != 1 || s.WorldScene != unit.WorldScene
                    || !IsWorldTerritory(s.SceneEntry)) return ErrorCodes.FeatureUnavailable;
                if (req.InteractionId != unit.InteractionId) return ErrorCodes.NpcParameter;
                if (MonsterService.Distance(s.Position.X, s.Position.Z, unit.X, unit.Z) > 5) return ErrorCodes.NpcTooFar;
                long limit = WorldCarryLimit();
                long carried = s.Progress.Currency.GetValueOrDefault(SupplyCurrency);
                if (unit.Amount <= 0 || carried < 0 || carried > limit - unit.Amount) return ErrorCodes.BagFull;
                WorldState? next = null;
                if (unit.DropId != 0)
                {
                    if (!state.WorldDrops.TryGetValue(unit.DropId, out var drop) || drop.Amount != unit.Amount) return ErrorCodes.NpcMissing;
                    next = Copy(state); next.WorldDrops.Remove(unit.DropId);
                }
                else
                {
                    var occurrence = state.WorldPickupOccurrences.GetValueOrDefault(unit.Slot);
                    if ((occurrence?.Ordinal ?? 0) != unit.Ordinal || occurrence?.NextEligible > _clock.Now)
                        return ErrorCodes.NpcMissing;
                    next = Copy(state);
                    next.WorldPickupOccurrences[unit.Slot] = new WorldPickupOccurrence
                    {
                        Ordinal = checked(unit.Ordinal + 1),
                        NextEligible = checked(_clock.Now + unit.RespawnSeconds),
                    };
                }
                unit.Taken = true;
                try
                {
                    var asset = new EconomyAsset(EconomyAssetKind.Currency, SupplyCurrency);
                    var beneficiary = new EconomyOwner(EconomyOwnerKind.Role,
                        s.Role.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    var escrow = new EconomyOwner(EconomyOwnerKind.Escrow, "territory-drop:" + state.Epoch);
                    string source = unit.DropId != 0
                        ? $"territory-drop:{state.Epoch}:{unit.DropId}:claim"
                        : $"territory-world-pickup:{state.Epoch}:{unit.Slot}:{unit.Ordinal}";
                    var movement = new EventEconomyMutation(EventEconomy.Catalog(_data), Guid.NewGuid().ToString("N"),
                        s.Account!, s.Role.Id, "event/territory-world-pickup", source,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [],
                        unit.DropId == 0 ? [new EconomyQuantity(asset, unit.Amount)] : [],
                        unit.DropId == 0 ? [new EventEconomyOwnerDelta(beneficiary, asset, unit.Amount)] :
                            [new(escrow, asset, -unit.Amount), new(beneficiary, asset, unit.Amount)],
                        JsonSerializer.SerializeToUtf8Bytes(new { unit.Uid, unit.DropId, unit.Amount, Role = s.Role.Id }));
                    if (await _character.ExchangeLockedAsync(s, [], [(SupplyCurrency, unit.Amount)],
                        sync: false, popup: false, persist: tx =>
                        {
                            tx.Add(new Ro3Emu.Server.Accounts.Persistence.RealmStatePlan(
                                Ro3Emu.Server.Accounts.Persistence.RealmStateKind.TerritoryWars,
                                JsonSerializer.SerializeToUtf8Bytes(next)));
                            tx.WithEventEconomy(movement);
                        }, asyncCommit: true, appliedBeforeNotify: () =>
                        {
                            BeforeWorldPickupApplyForTest?.Invoke();
                            PublishCommitted(next);
                        }) == null)
                    { unit.Taken = false; return ErrorCodes.BagFull; }
                }
                catch
                {
                    // A post-commit publication fault must not reopen a consumed live actor. Read the
                    // durable occurrence while still holding the realm gate, then retire/schedule it.
                    var bytes = await _accounts.ReadAsync<byte[]?>(new Ro3Emu.Server.Accounts.Persistence.TerritoryRealmReadPlan(
                        Ro3Emu.Server.Accounts.Persistence.RealmStateKind.TerritoryWars));
                    var durable = bytes == null ? null : JsonSerializer.Deserialize<WorldState>(bytes);
                    var committed = unit.Slot.Length > 0
                        ? durable?.WorldPickupOccurrences.GetValueOrDefault(unit.Slot) : null;
                    bool staticClaimCommitted = committed != null && committed.Ordinal > unit.Ordinal;
                    bool droppedClaimCommitted = unit.DropId > 0 && durable != null
                        && !durable.WorldDrops.ContainsKey(unit.DropId);
                    if (durable != null && (staticClaimCommitted || droppedClaimCommitted))
                    {
                        PublishCommitted(durable);
                        _worldPickups.Remove(unit.Uid);
                        await _world.RemoveUnitAsync(unit);
                        if (staticClaimCommitted)
                        {
                            long generation = _worldGeneration;
                            _loop.ScheduleRuntime(TimeSpan.FromSeconds(Math.Max(0, committed!.NextEligible - _clock.Now)),
                                () => RespawnWorldPickupAsync(unit, generation));
                        }
                    }
                    else unit.Taken = false;
                    throw;
                }
                _worldPickups.Remove(unit.Uid);
                await _world.RemoveUnitAsync(unit);
                if (unit.Slot.Length > 0 && unit.RespawnSeconds > 0)
                {
                    long generation = _worldGeneration;
                    _loop.ScheduleRuntime(TimeSpan.FromSeconds(unit.RespawnSeconds), () => RespawnWorldPickupAsync(unit, generation));
                }
                await _character.ItemsChangedAsync(s, false);
                return 0;
            }
            finally { _worldGate.Release(); }
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
    }

    private async Task RespawnWorldPickupAsync(WorldPickupUnit old, long generation)
    {
        using var reservation = await EnterGateAsync("territory/world pickup respawn");
        using var stagedTerritoryState = StageState();
        try
        {
            await _worldGate.WaitAsync();
            try
            {
                if (generation != _worldGeneration || !_worldMapsLoaded.Contains(old.SceneEntry)) return;
                if (old.Slot.Length == 0) return;
                var occurrence = state.WorldPickupOccurrences.GetValueOrDefault(old.Slot);
                if (occurrence?.NextEligible > _clock.Now)
                {
                    _loop.ScheduleRuntime(TimeSpan.FromSeconds(occurrence.NextEligible - _clock.Now),
                        () => RespawnWorldPickupAsync(old, generation));
                    return;
                }
                long ordinal = occurrence?.Ordinal ?? 0;
                if (_worldPickups.Values.Any(unit => unit.Slot == old.Slot && unit.Ordinal == ordinal)) return;
                await AddWorldPickupAsync(new WorldPickupUnit
                {
                    Uid = WorldPickupUidBase | ++_nextWorldPickupUid, SceneEntry = old.SceneEntry,
                    X = old.X, Y = old.Y, Z = old.Z, Face = old.Face,
                    NpcId = old.NpcId, Amount = old.Amount, InteractionId = old.InteractionId,
                    Slot = old.Slot, Ordinal = ordinal,
                    RespawnSeconds = old.RespawnSeconds,
                });
            }
            finally { _worldGate.Release(); }
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
    }

    private long WorldCarryLimit()
    {
        if (_data.Table("PublicParamConfig").TryGetValue("TerritoryWorldDropItemCarryLimit", out var limits)
            && limits.ValueKind == JsonValueKind.Object && limits.TryGetProperty("133", out var amount))
            return (long)(GameData.Number(amount) ?? 5000);
        return 5000;
    }

    /// <summary>Configured camp-indexed loss [Peace, Free, Guild]; index and rounded loss are inferred.</summary>
    public async Task WorldDiedAsync(GameSession victim)
    {
        if (victim.Role == null || victim.Progress == null || victim.InstanceId != 0 || victim.LineNumber != 1
            || !IsWorldTerritory(victim.SceneEntry)) return;
        var role = victim.Role;
        var attachment = victim.Attachment;
        using var reservation = await EnterGateAsync("territory/world death", victim);
        using var stagedTerritoryState = StageState();
        try
        {
            if (!ReferenceEquals(victim.Role, role) || !ReferenceEquals(victim.Attachment, attachment)) return;
            WorldDrop? committedDrop = null;
            using (var held = await CharacterService.Lock(victim))
            {
                await _worldGate.WaitAsync();
                try
                {
                    if (!victim.Dead || victim.SceneLoading || victim.InstanceId != 0 || victim.LineNumber != 1
                        || !IsWorldTerritory(victim.SceneEntry)) return;
                    int camp = victim.TerritoryWars?.Camp ?? LoadRole(victim.Role.Id).Camp;
                    var rates = _data.Table("PublicParamConfig").GetValueOrDefault("TerritoryWorldDeathDropRate");
                    if (rates.ValueKind != JsonValueKind.Array || camp < 1 || camp > rates.GetArrayLength()) return;
                    double rate = GameData.Number(rates[camp - 1]) ?? 0;
                    long carried = victim.Progress.Currency.GetValueOrDefault(SupplyCurrency);
                    int lost = (int)Math.Min(int.MaxValue, Math.Ceiling(carried * Math.Clamp(rate, 0, 1)));
                    if (lost <= 0) return;
                    var next = Copy(state);
                    var drop = new WorldDrop
                    {
                        Id = next.NextWorldDropId++, Scene = victim.SceneEntry,
                        X = victim.Position.X, Y = victim.Position.Y, Z = victim.Position.Z, Amount = lost
                    };
                    next.WorldDrops.Add(drop.Id, drop);
                    var asset = new EconomyAsset(EconomyAssetKind.Currency, SupplyCurrency);
                    var movement = new EventEconomyMutation(EventEconomy.Catalog(_data), Guid.NewGuid().ToString("N"),
                        victim.Account!, victim.Role.Id, "event/territory-world-death",
                        $"territory-drop:{state.Epoch}:{drop.Id}:create",
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [], [],
                        [new(new(EconomyOwnerKind.Role, victim.Role.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)), asset, -lost),
                         new(new(EconomyOwnerKind.Escrow, "territory-drop:" + state.Epoch), asset, lost)],
                        JsonSerializer.SerializeToUtf8Bytes(new { drop.Id, drop.Amount, Role = victim.Role.Id }));
                    try
                    {
                        if (await _character.ExchangeLockedAsync(victim, [(SupplyCurrency, (long)lost)], [],
                            sync: false, popup: false, persist: tx =>
                            {
                                tx.Add(new Ro3Emu.Server.Accounts.Persistence.RealmStatePlan(
                                    Ro3Emu.Server.Accounts.Persistence.RealmStateKind.TerritoryWars,
                                    JsonSerializer.SerializeToUtf8Bytes(next)));
                                tx.WithEventEconomy(movement);
                            }, asyncCommit: true, appliedBeforeNotify: () =>
                            { BeforeWorldDeathApplyForTest?.Invoke(); PublishCommitted(next); }) == null) return;
                    }
                    catch
                    {
                        var bytes = await _accounts.ReadAsync<byte[]?>(new Ro3Emu.Server.Accounts.Persistence.TerritoryRealmReadPlan(
                            Ro3Emu.Server.Accounts.Persistence.RealmStateKind.TerritoryWars));
                        var durable = bytes == null ? null : JsonSerializer.Deserialize<WorldState>(bytes);
                        if (durable?.WorldDrops.TryGetValue(drop.Id, out var recovered) == true
                            && recovered.Amount == drop.Amount && recovered.Scene == drop.Scene)
                        {
                            PublishCommitted(durable);
                            if (_worldMapsLoaded.Contains(recovered.Scene)
                                && !_worldPickups.Values.Any(actor => actor.DropId == recovered.Id))
                                await AddWorldDropActorAsync(recovered);
                        }
                        throw;
                    }
                    committedDrop = drop;
                }
                finally { _worldGate.Release(); }
            }
            if (committedDrop != null && _worldMapsLoaded.Contains(committedDrop.Scene)) await AddWorldDropActorAsync(committedDrop);
            if (committedDrop != null) await _character.ItemsChangedAsync(victim, false);
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
    }

    /// <summary>Only our spawned land actor can trigger the configured normal/owner extra awards.</summary>
    public async Task WorldKilledAsync(GameSession killer, MonsterUnit monster)
    {
        if (!monster.Dead || monster.Hp > 0 || monster.InstanceId != 0 || monster.LineNumber != 1
            || !IsWorldTerritory(monster.SceneEntry)) return;
        var killerRole = killer.Role;
        var killerAttachment = killer.Attachment;
        long deliverRole = 0;
        using var reservation = await EnterGateAsync("territory/world actor kill", killer);
        using var stagedTerritoryState = StageState();
        try
        {
            await _worldGate.WaitAsync();
            try
            {
                var pair = _worldActivities.FirstOrDefault(x => ReferenceEquals(x.Value, monster));
                if (pair.Value == null || _data.Row("GVGLandActivityConfig", pair.Key) is not { } row) return;
                long occurrence = _worldActivityOrdinals.GetValueOrDefault(pair.Key);
                if (state.WorldActivityOccurrences.GetValueOrDefault(pair.Key) != occurrence) return;
                var next = Copy(state);
                long respawn = Math.Max(1, GameData.Int(row, "_iRespawnCD") / 1000);
                next.WorldActivityRespawns[pair.Key] = _clock.Now + respawn;
                next.WorldActivityOccurrences[pair.Key] = checked(occurrence + 1);
                var role = ReferenceEquals(killer.Role, killerRole) && ReferenceEquals(killer.Attachment, killerAttachment)
                    && killerRole != null ? LoadRole(killerRole.Id) : null;
                // RestrictConfig._iRecoverType=2 and the client's WeekLastChallengeCount: UTC week (inferred boundary).
                var utc = DateTimeOffset.FromUnixTimeSeconds(_clock.Now).UtcDateTime;
                long week = new DateTimeOffset(utc.Date.AddDays(-((int)utc.DayOfWeek + 6) % 7), TimeSpan.Zero).ToUnixTimeSeconds();
                string claimKey = $"{Epoch}:{week}:{pair.Key}";
                int max = (int)(_data.Row("RestrictConfig", GameData.Int(row, "_iRestrictId")) is { } restrict
                    ? GameData.Int(restrict, "_iMaxRestrictNum") : 0);
                if (role != null && killer.Role != null && killer.InstanceId == 0 && killer.LineNumber == 1 && !killer.SceneLoading
                    && killer.WorldScene == monster.WorldScene && max > 0
                        && role.WorldLandClaims.GetValueOrDefault(claimKey) < max)
                {
                    var grants = new List<Grant>();
                    AddAward(grants, GameData.Int(row, "_iAward"), killer);
                    int zone = Zones.Where(z => GameData.Int(z, "_iMapID") == monster.SceneEntry)
                        .Select(z => (int)Id(z)).FirstOrDefault();
                    long owner = OwnerOf(zone), guild = Own(killer)?.Id ?? 0;
                    double range = GameData.Int(row, "_iOwnerRange") / 100.0;
                    if (owner != 0 && guild == owner && range > 0
                        && MonsterService.Distance(killer.Position.X, killer.Position.Z, monster.X, monster.Z) <= range)
                        AddAward(grants, GameData.Int(row, "_iExtraAward"), killer);
                    role.WorldLandClaims[claimKey] = role.WorldLandClaims.GetValueOrDefault(claimKey) + 1;
                    role.Pending[$"world-land:{claimKey}:{role.WorldLandClaims[claimKey]}"] = grants;
                    var transaction = _roles.NewTransaction();
                    Save(next, transaction);
                    SaveRole(killer.Role.Id, role, transaction);
                    if (PendingSource(new Dictionary<long, List<Grant>> { [killer.Role.Id] = grants },
                        "world-land", $"territory-world-land:{Epoch}:{pair.Key}:{occurrence}") is { } reward)
                        transaction.WithEventEconomy(reward);
                    try { await _roles.CommitAsync(transaction, "territory/world-kill"); }
                    catch
                    {
                        await RecoverWorldActivityAfterFaultAsync(pair.Key, occurrence);
                        throw;
                    }
                    killer.TerritoryWars = role;
                    deliverRole = killer.Role.Id;
                }
                else await SaveAsync(next);
                PublishCommitted(next);
                _worldActivities.Remove(pair.Key); _worldActivityOrdinals.Remove(pair.Key);
                long generation = _worldGeneration;
                _loop.ScheduleRuntime(TimeSpan.FromSeconds(respawn), () => RespawnWorldActivityAsync(pair.Key, generation));
            }
            finally { _worldGate.Release(); }
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
        if (deliverRole != 0 && killer.Role?.Id == deliverRole) await DeliverAsync(killer);
    }

    private async Task RecoverWorldActivityAfterFaultAsync(int id, long occurrence)
    {
        var bytes = await _accounts.ReadAsync<byte[]?>(new Ro3Emu.Server.Accounts.Persistence.TerritoryRealmReadPlan(
            Ro3Emu.Server.Accounts.Persistence.RealmStateKind.TerritoryWars));
        var durable = bytes == null ? null : JsonSerializer.Deserialize<WorldState>(bytes);
        if (durable == null || durable.WorldActivityOccurrences.GetValueOrDefault(id) <= occurrence) return;
        PublishCommitted(durable);
        _worldActivities.Remove(id); _worldActivityOrdinals.Remove(id);
        long generation = _worldGeneration;
        long wait = Math.Max(0, durable.WorldActivityRespawns.GetValueOrDefault(id) - _clock.Now);
        _loop.ScheduleRuntime(TimeSpan.FromSeconds(wait), () => RespawnWorldActivityAsync(id, generation));
    }
}
