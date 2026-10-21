using System.Collections.Concurrent;
using System.Text.Json;
using Ro3Emu.Server.Economy;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class TerritoryEscortService
{
    // The shared escort ledger/cargo has no line field. Event actors and local actions belong to public line 1.
    private static bool EscortLine(GameSession session) => session.InstanceId == 0 && session.LineNumber == 1;
    private const long CartUidBase = 1L << 43;
    private const long CargoUidBase = 1L << 44;
    private readonly SemaphoreSlim _actorGate = new(1, 1);
    private readonly ConcurrentDictionary<long, (MonsterUnit Unit, long Guild, long Owner)> _liveCarts = new();
    private readonly ConcurrentDictionary<long, CartState> _pendingDestroyed = new();
    private readonly ConcurrentDictionary<long, long> _cartRespawnAfter = new();
    private readonly ConcurrentDictionary<long, CargoUnit> _liveCargo = new();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, long>> _pvpHits = new();
    private readonly ConcurrentDictionary<GameSession, byte> _leavingEscort = new();
    internal Action? BeforeCargoApplyForTest;

    private sealed class CargoUnit : WorldUnit
    {
        public override EntityBase Entity() => new()
        {
            Uuid = (ulong)Uid, EntityTemplateID = 4910003, EntityType = 2,
            State = EntityState.Alive, NotifyType = EntityBase.Types.NotifyType.All,
            MovementInfo = new MovementInfo
            {
                Pos = Pos, Face = Face, ServerTime = MonsterService.NowMs,
                MoveType = MovementType.MoveTypeNone
            },
        };
    }

    /// <summary>32287 publishes guild goods; 32700 is intercepted only for our reserved cargo UID namespace.</summary>
    public async Task<bool> HandleSharedAsync(GameSession session, uint id, ReadOnlyMemory<byte> body)
    {
        if (id == 32287)
        {
            if (Volatile.Read(ref _committedEpoch) != Epoch || _lastStageType != _clock.Current.Type)
                await ReconcileAsync();
            var guild = Own(session);
            await session.SendGameAsync(id, new GetGuildItemDataResp
            {
                Result = guild == null ? ErrorCodes.NotInGuild : 0,
                Info = guild == null ? new GuildItemData() : GuildItems(guild.Id),
            });
            return true;
        }
        if (id != SceneService.NpcInteraction) return false;
        var req = NpcInteractionReq.Parser.ParseFrom(body.Span);
        if (req.NpcUid < CargoUidBase || req.NpcUid >= CargoUidBase * 2) return false;
        if (Volatile.Read(ref _committedEpoch) != Epoch || _lastStageType != _clock.Current.Type)
            await ReconcileAsync();
        int code = await CollectCargoAsync(session, req);
        await session.SendGameAsync(id, new NpcInteractionRsp { Result = code, Req = req });
        return true;
    }

    private async Task<int> CollectCargoAsync(GameSession session, NpcInteractionReq req)
    {
        int result = await CollectCargoLockedAsync(session, req);
        if (result == 0) await RefreshCargoActorsAsync();
        return result;
    }

    private async Task<int> CollectCargoLockedAsync(GameSession session, NpcInteractionReq req)
    {
        if (req.InteractionId != 491000301) return ErrorCodes.NpcParameter;
        using var reservation = await EnterGateAsync("escort/cargo pickup", session);
        try
        {
            using var held = await CharacterService.Lock(session);
            using var stagedEscortLedger = StageLedger();
            long id = req.NpcUid & ~CargoUidBase;
            if (!ledger.Cargo.TryGetValue(id, out var cargo) || cargo.Picked || cargo.Expires <= _clock.Now)
                return ErrorCodes.NpcMissing;
            if (!Playing || session.Dead || session.SceneLoading || cargo.Scene != session.SceneEntry || !EscortLine(session))
                return ErrorCodes.FeatureUnavailable;
            if (_clock.Now < cargo.FreeAfter && Own(session)?.Id != cargo.LooterGuild)
                return ErrorCodes.GuildPermission;
            var point = new Vec3 { X = cargo.X, Y = cargo.Y, Z = cargo.Z };
            if (!Near(session.Position, point, 5)) return ErrorCodes.NpcTooFar;
            cargo.Picked = true;
            try
            {
                var item = new EconomyAsset(EconomyAssetKind.Item, 1258002);
                var source = $"escort-cargo:{Epoch}:{id}:claim";
                var mutation = new EventEconomyMutation(EventEconomy.Catalog(_data), Guid.NewGuid().ToString("N"),
                    session.Account!, session.Role!.Id, "event/escort-cargo-pickup", source,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [], [],
                    [new(new(EconomyOwnerKind.Escrow, "escort-cargo:" + id), item, -1),
                     new(new(EconomyOwnerKind.Role, session.Role.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)), item, 1)],
                    JsonSerializer.SerializeToUtf8Bytes(new { Cargo = id, Role = session.Role.Id }));
                var granted = await _character.ExchangeLockedAsync(session, [], [(1258002, 1)],
                    persist: tx =>
                    {
                        tx.Add(new Ro3Emu.Server.Accounts.Persistence.RealmStatePlan(
                            Ro3Emu.Server.Accounts.Persistence.RealmStateKind.TerritoryEscort,
                            JsonSerializer.SerializeToUtf8Bytes(ledger)));
                        tx.WithEventEconomy(mutation);
                    }, asyncCommit: true, appliedBeforeNotify: () =>
                    {
                        BeforeCargoApplyForTest?.Invoke();
                        _committedLedger = CopyLedger(ledger);
                        PublishSnapshots();
                    });
                if (granted == null) { cargo.Picked = false; return ErrorCodes.BagFull; }
            }
            catch
            {
                var bytes = await _accounts.ReadAsync<byte[]?>(new Ro3Emu.Server.Accounts.Persistence.TerritoryRealmReadPlan(
                    Ro3Emu.Server.Accounts.Persistence.RealmStateKind.TerritoryEscort));
                var durable = bytes == null ? null : JsonSerializer.Deserialize<Ledger>(bytes);
                if (durable?.Cargo.TryGetValue(id, out var recovered) == true && recovered.Picked)
                {
                    _committedLedger = CopyLedger(durable);
                    ledger = CopyLedger(durable);
                    PublishSnapshots();
                    // The committed realm owner is quarantined after an apply fault. Retire this
                    // claimed actor under the original admission; a second realm admission fails.
                    await _actorGate.WaitAsync();
                    try
                    {
                        long uid = CargoUidBase | id;
                        if (_liveCargo.TryRemove(uid, out var actor)) await _world.RemoveUnitAsync(actor);
                    }
                    finally { _actorGate.Release(); }
                }
                else cargo.Picked = false;
                throw;
            }
        }
        finally { _gate.Release(); }
        return 0;
    }

    /// <summary>Combat's composite objective predicate calls this for living cart actors.</summary>
    public bool CanHitCart(GameSession attacker, MonsterUnit monster)
    {
        if (!_liveCarts.TryGetValue(monster.Uid, out var cart)) return true;
        return Playing && EscortLine(attacker) && attacker.WorldScene == monster.WorldScene
            && Own(attacker) is { } guild && guild.Id != cart.Guild;
    }

    /// <summary>Escort PvP is restricted to participants on configured escort point-area maps.</summary>
    public bool Hostile(GameSession attacker, GameSession target)
    {
        if (!Playing || _leavingEscort.ContainsKey(attacker) || _leavingEscort.ContainsKey(target)
            || !EscortLine(attacker) || !EscortLine(target) || attacker.WorldScene != target.WorldScene
            || attacker.Role == null || target.Role == null || attacker.Role.Id == target.Role.Id
            || _data.Table("EscortPointAreaConfig").Values.All(row => GameData.Int(row, "_iMapId") != attacker.SceneEntry)) return false;
        var a = Own(attacker); var b = Own(target);
        return a != null && b != null && a.Id != b.Id;
    }

    /// <summary>The escort actors' keys, for a scene load that refreshes them (C/D 15).</summary>
    private Ownership.ReservationKey[] SceneKeys(GameSession session) =>
        session.Role != null && session.TerritoryEscort != null && EscortLine(session) ? MutationKeys(null, false) : [];

    private async Task SceneLoadedAsync(GameSession session)
    {
        _leavingEscort.TryRemove(session, out _);
        if (session.Role == null || session.TerritoryEscort == null || !EscortLine(session)) return;
        await RefreshCartActorsAsync();
        await RefreshCargoActorsAsync();
        await SendCartsAsync(session);
    }

    private Task SceneLeavingAsync(GameSession session)
    {
        _leavingEscort[session] = 1; // Synchronous before the SceneService hook's first await.
        return Task.CompletedTask;
    }

    // (inferred) 4910001/4910002 are NPCType 2 in NPCConfig; World combat targets MonsterUnit.
    // We carry the configured cart template and HP in a non-roaming MonsterUnit so existing AOI and
    // damage/death authority can represent the escort cart until a native cart actor type exists.
    private MonsterUnit MakeCart(CartState cart, GameSession owner)
    {
        int npcId = cart.Type == 0 ? 4910001 : 4910002;
        var npc = _data.Row("NPCConfig", npcId)!.Value;
        var attr = _data.Row("AttriMonsterConfig", GameData.Int(npc, "_iAttribute"))!.Value;
        var stats = GameData.Pairs(attr, "_kAttri").ToDictionary(x => x.Id, x => x.Value);
        long Stat(int id) => (long)stats.GetValueOrDefault(id);
        int level = (int)GameData.Int(npc, "_iLevel");
        var template = new MonsterTemplate(npcId, level, Math.Max(1, Stat(101)), Stat(102), Stat(103), Stat(104),
            Stat(105), Stat(106), level + 1, level, 0, (int)GameData.Int(npc, "_iSpeed"),
            (int)GameData.Int(npc, "_iElement"), (int)GameData.Int(npc, "_iRace"), (int)GameData.Int(npc, "_iSize"),
            1, false, 0, 8000, 0, 0, 0, 0, 0, 0, 0, 500);
        var point = new SpawnPoint(-1, owner.Position.X, owner.Position.Y, owner.Position.Z, owner.Face, npcId);
        return new MonsterUnit
        {
            Uid = CartUidBase | cart.Id, SceneEntry = owner.SceneEntry,
            X = owner.Position.X - 3, Y = owner.Position.Y, Z = owner.Position.Z,
            Face = owner.Face, Template = template, Group = new SpawnGroup(-1, false, 1, 0, [], [point]),
            Home = point, Hp = cart.CurrentHp > 0 ? Math.Min(cart.CurrentHp, template.MaxHp) : template.MaxHp,
            BornTimeMs = MonsterService.NowMs
        };
    }

    private async Task RefreshCartActorsAsync()
    {
        CartState[] wanted;
        var finalized = new List<(CartState Cart, long Scene)>();
        using var reservation = await EnterGateAsync("escort/cart actors");
        using var stagedEscortLedger = StageLedger();
        try
        {
            wanted = ledger.Carts.Values.Select(c => new CartState
            {
                Id = c.Id, Guild = c.Guild,
                Owner = c.Owner, Round = c.Round, Type = c.Type, Point = c.Point, Count = c.Count,
                OriginCount = c.OriginCount, DamageTier = c.DamageTier, CurrentHp = c.CurrentHp,
                Started = c.Started, Scene = c.Scene
            }).ToArray();
        }
        finally { stagedEscortLedger.Dispose(); _gate.Release(); reservation?.Dispose(); }
        await _actorGate.WaitAsync();
        try
        {
            var ids = wanted.Where(c => _sessions.All().Any(s => s.Role?.Id == c.Owner && !s.SceneLoading && EscortLine(s)))
                .Select(c => CartUidBase | c.Id).ToHashSet();
            foreach (var (uid, live) in _liveCarts.ToArray().Where(x =>
                (!ids.Contains(x.Key) && !_pendingDestroyed.ContainsKey(x.Key)) || x.Value.Unit.Dead))
                if (_liveCarts.TryRemove(uid, out _))
                {
                    if (live.Unit.Dead && ids.Contains(uid) && !_pendingDestroyed.ContainsKey(uid))
                        DeferCartRespawn(live.Unit);
                    await _world.RemoveUnitAsync(live.Unit);
                    if (_pendingDestroyed.TryRemove(uid, out var destroyed))
                        finalized.Add((destroyed, live.Unit.SceneEntry));
                }
            // Combat can skip its role-bound Killed callbacks if the caster changes role or scene
            // during the awaited damage hook. The ticker owns visual completion in that case.
            foreach (var (uid, destroyed) in _pendingDestroyed.ToArray())
            {
                bool hasLive = _liveCarts.TryGetValue(uid, out var live);
                if (!hasLive || live.Unit.Dead)
                    if (_pendingDestroyed.TryRemove(uid, out _))
                        finalized.Add((destroyed, hasLive ? live.Unit.SceneEntry : destroyed.Scene));
            }
            foreach (var cart in wanted)
            {
                var owner = _sessions.All().FirstOrDefault(s => s.Role?.Id == cart.Owner && !s.SceneLoading && EscortLine(s));
                if (owner == null) continue;
                long uid = CartUidBase | cart.Id;
                if (_cartRespawnAfter.TryGetValue(uid, out long due))
                {
                    if (MonsterService.NowMs < due) continue;
                    _cartRespawnAfter.TryRemove(uid, out _);
                }
                if (!_liveCarts.TryGetValue(uid, out var live))
                {
                    var actor = MakeCart(cart, owner);
                    _liveCarts[uid] = (actor, cart.Guild, cart.Owner);
                    await _world.AddUnitAsync(actor);
                    continue;
                }
                var unit = live.Unit;
                if (unit.SceneEntry != owner.SceneEntry)
                {
                    await _world.RemoveUnitAsync(unit);
                    unit.SceneEntry = owner.SceneEntry;
                    unit.X = owner.Position.X - 3; unit.Y = owner.Position.Y; unit.Z = owner.Position.Z;
                    await _world.AddUnitAsync(unit);
                }
                else if (!Near(unit.Pos, owner.Position, 3.5f))
                {
                    unit.X = owner.Position.X - 3; unit.Y = owner.Position.Y; unit.Z = owner.Position.Z;
                    unit.Face = owner.Face;
                    await _world.MoveUnitAsync(unit, new MovementChangeNtf
                    {
                        Uid = unit.Uid,
                        TransType = MovementTransitionType.MoveTransNormal,
                        MovementInfo = new MovementInfo
                        {
                            Pos = unit.Pos, ServerTime = MonsterService.NowMs,
                            Face = unit.Face, MoveType = MovementType.MoveTypeNone
                        }
                    });
                }
            }
        }
        finally { _actorGate.Release(); }
        foreach (var (cart, scene) in finalized)
        {
            await BroadcastCartDestroyedAsync(cart, scene);
            await RoleFanout.EachAsync(_sessions.All().Where(s => s.SceneEntry == scene && EscortLine(s) && s.TerritoryEscort != null), SendScoreAsync);
        }
        if (finalized.Count > 0) await RefreshCargoActorsAsync();
    }

    private async Task RefreshCargoActorsAsync()
    {
        CargoState[] wanted;
        using var reservation = await EnterGateAsync("escort/cargo actors");
        using var stagedEscortLedger = StageLedger();
        try
        {
            wanted = ledger.Cargo.Values.Where(x => !x.Picked && x.Expires > _clock.Now).Select(x => new CargoState
            {
                Id = x.Id, Scene = x.Scene, LooterGuild = x.LooterGuild, X = x.X, Y = x.Y, Z = x.Z,
                FreeAfter = x.FreeAfter, Expires = x.Expires
            }).ToArray();
        }
        finally { stagedEscortLedger.Dispose(); _gate.Release(); reservation?.Dispose(); }
        await _actorGate.WaitAsync();
        try
        {
            var ids = wanted.Select(x => CargoUidBase | x.Id).ToHashSet();
            foreach (var (uid, unit) in _liveCargo.ToArray().Where(x => !ids.Contains(x.Key)))
                if (_liveCargo.TryRemove(uid, out _)) await _world.RemoveUnitAsync(unit);
            foreach (var cargo in wanted)
            {
                long uid = CargoUidBase | cargo.Id;
                if (_liveCargo.ContainsKey(uid)) continue;
                var unit = new CargoUnit
                {
                    Uid = uid, SceneEntry = cargo.Scene,
                    X = cargo.X, Y = cargo.Y, Z = cargo.Z
                };
                _liveCargo[uid] = unit;
                await _world.AddUnitAsync(unit);
            }
        }
        finally { _actorGate.Release(); }
    }

    private async Task CartKilledAsync(GameSession killer, MonsterUnit monster)
    {
        _liveCarts.TryRemove(monster.Uid, out _);
        if (!_pendingDestroyed.TryRemove(monster.Uid, out var destroyed))
        {
            // The combat kill may follow a failed pre-commit damage callback. Keep the durable
            // reservation and recreate its actor only after the old corpse leaves the world.
            DeferCartRespawn(monster);
            return;
        }
        // The awaited damage callback already committed the terminal goods debit, cargo and score.
        await BroadcastCartDestroyedAsync(destroyed, monster.SceneEntry);
        await SendScoreAsync(killer);
        await RefreshCartActorsAsync();
        await RefreshCargoActorsAsync();
    }

    private void DeferCartRespawn(MonsterUnit monster)
    {
        long delayMs = monster.Template.BodyRemainMs + 250;
        _cartRespawnAfter[monster.Uid] = MonsterService.NowMs + delayMs;
        _loop.ScheduleRuntime(TimeSpan.FromMilliseconds(delayMs), RefreshCartActorsAsync);
    }

    private async Task BroadcastCartDestroyedAsync(CartState cart, long scene)
    {
        await RoleFanout.EachAsync(_sessions.All().Where(s => s.SceneEntry == scene && EscortLine(s) && !s.SceneLoading),
            peer => peer.SendGameAsync(CartNtf, new TerritoryWarsEscortCartNtf
            { CartInfo = CartWire(cart), Reason = (TerritoryWarEscortCartChangeReason)3 }));
    }

    /// <summary>
    /// The configured damage levels are 66%, 33% and 1% HP and _kDrops is cumulative by level.
    /// (inferred) Each reached level loses 20% of original goods (EscortScoreParamPartRob),
    /// releases only its incremental cargo, and awards the robber 20% of those lost goods.
    /// The hook runs after HP mutation and before death; tier/HP/cargo/score commit together.
    /// </summary>
    private async Task CartDamagedAsync(GameSession attacker, MonsterUnit monster, long effectiveDamage)
    {
        if (effectiveDamage <= 0 || !Playing || !_liveCarts.TryGetValue(monster.Uid, out var live)
            || Own(attacker) is not { } guild || guild.Id == live.Guild) return;
        long attackerRole = attacker.Role?.Id ?? 0;
        var attackerAttachment = attacker.Attachment;
        long attackerScene = attacker.SceneEntry;
        long attackerInstance = attacker.InstanceId;
        bool changedTier = false;
        int remaining = 0;
        using var reservation = await EnterGateAsync("escort/cart damage", attacker);
        using var stagedEscortLedger = StageLedger();
        try
        {
            if (attackerRole == 0 || attacker.Role?.Id != attackerRole
                || !ReferenceEquals(attacker.Attachment, attackerAttachment)
                || attacker.SceneEntry != attackerScene || attacker.WorldScene != monster.WorldScene
                || attacker.InstanceId != attackerInstance || !EscortLine(attacker)
                || Own(attacker)?.Id != guild.Id || !Playing) return;
            if (!ledger.Carts.TryGetValue(monster.Uid & ~CartUidBase, out var cart)) return;
            var transaction = _roles.NewTransaction();
            bool durable = false;
            RoleState? scoredRole = null;
            long priorHp = cart.CurrentHp > 0 ? cart.CurrentHp : monster.Template.MaxHp;
            long hitHp = Math.Max(0, monster.Hp);
            try
            {
                var config = _data.Row("EscortCartConfig", cart.Type == 0 ? 4910001 : 4910002)!.Value;
                var levels = config.GetProperty("_kDamageLevelArgs");
                var drops = config.GetProperty("_kDrops");
                long hp = Math.Max(0, monster.Hp);
                int origin = cart.OriginCount == 0 ? cart.Count : cart.OriginCount;
                long score = 0;
                for (int index = cart.DamageTier; index < levels.GetArrayLength(); index++)
                {
                    var level = levels[index];
                    int percent = (int)(GameData.Number(level[1]) ?? 0);
                    if (hp * 100 > monster.Template.MaxHp * percent) break;
                    int priorDrops = index == 0 ? 0 : (int)(GameData.Number(drops[index - 1][1]) ?? 0);
                    int totalDrops = (int)(GameData.Number(drops[index][1]) ?? 0);
                    for (int i = priorDrops; i < totalDrops; i++)
                    {
                        long id = ledger.NextCargoId++;
                        ledger.Cargo[id] = new CargoState
                        {
                            Id = id, Scene = monster.SceneEntry,
                            LooterGuild = guild.Id, X = monster.X + (i % 3 - 1) * 1.5f,
                            Y = monster.Y, Z = monster.Z + (i / 3) * 1.5f,
                            FreeAfter = _clock.Now + 300, Expires = _clock.Now + 1800
                        };
                    }
                    int lost = Math.Min(cart.Count, (int)Math.Ceiling(origin * 0.2));
                    cart.Count -= lost;
                    score += (long)(lost * 0.2);
                    cart.DamageTier = index + 1;
                    changedTier = true;
                }
                cart.CurrentHp = hp;
                remaining = cart.Count;
                if (hp == 0)
                {
                    // Lethal damage consumes the cart in this same durable transaction. A process stop
                    // between damage and the visual death callback cannot resurrect its reserved stock.
                    score += (long)(cart.Count * 0.5); // EscortScoreParamRob (inferred).
                    ledger.Carts.Remove(cart.Id);
                }
                if (score > 0)
                {
                    AddGuildScore(guild.Id, score);
                    if (attacker.TerritoryEscort != null)
                    {
                        scoredRole = CopyRole(attacker.TerritoryEscort);
                        scoredRole.ScoreByRound[Round] = checked(scoredRole.ScoreByRound.GetValueOrDefault(Round) + score);
                        SaveRole(attackerRole, scoredRole, transaction);
                    }
                }
                await CommitAsync(transaction, "cart-damage",
                    $"escort-cart:{Epoch}:{cart.Id}:damage:{cart.DamageTier}:{cart.CurrentHp}", attacker);
                durable = true;
                if (scoredRole != null && attacker.Role?.Id == attackerRole
                    && ReferenceEquals(attacker.Attachment, attackerAttachment))
                    attacker.TerritoryEscort = scoredRole;
                if (hp == 0) _pendingDestroyed[monster.Uid] = cart;
            }
            catch
            {
                if (!durable)
                {
                    RestoreCommittedLedger();
                    // Commit can throw while publishing snapshots after SQLite has committed.
                    // Read the persisted cart to distinguish that case from a failed debit.
                    bool persistedHit = hitHp == 0
                        ? !ledger.Carts.ContainsKey(cart.Id)
                        : ledger.Carts.TryGetValue(cart.Id, out var savedHit)
                            && savedHit.CurrentHp == hitHp && hitHp < priorHp;
                    if (persistedHit && attacker.Role?.Id == attackerRole
                        && ReferenceEquals(attacker.Attachment, attackerAttachment))
                        attacker.TerritoryEscort = _roles.LoadTerritoryEscort(attacker.Role.Id) is { } prior
                            ? System.Text.Json.JsonSerializer.Deserialize<RoleState>(prior) : new RoleState();
                    if (persistedHit && hitHp == 0) _pendingDestroyed[monster.Uid] = cart;
                    // A nonlethal failed hit must not leave actor HP below the durable reservation.
                    if (!persistedHit && monster.Hp > 0
                        && ledger.Carts.TryGetValue(monster.Uid & ~CartUidBase, out var saved))
                    {
                        monster.Hp = saved.CurrentHp > 0 ? saved.CurrentHp : monster.Template.MaxHp;
                        _loop.PostRuntime(() => _world.ToUnitViewersAsync(monster, CombatService.AoiAttrChange, MonsterService.Hp(monster)));
                    }
                }
                throw;
            }
        }
        finally { stagedEscortLedger.Dispose(); _gate.Release(); reservation?.Dispose(); }
        if (!changedTier) return;
        await RoleFanout.EachAsync(_sessions.All().Where(s => s.WorldScene == monster.WorldScene && !s.SceneLoading),
            peer => peer.SendGameAsync(CartVarNtf, new TerritoryWarsEscortCartInfoChange
            {
                Uuid = monster.Uid & ~CartUidBase, CartVarinfo = new EscortCartVarInfo
                { CurNum = remaining, FollowPlayer = live.Owner }
            }));
        await SendScoreAsync(attacker);
        await RefreshCargoActorsAsync();
    }

    private void PvpRecorded(GameSession attacker, GameSession victim, long damage, bool lethal)
    {
        if (!Hostile(attacker, victim) || damage <= 0) return;
        _pvpHits.GetOrAdd(victim.Role!.Id, _ => new())[attacker.Role!.Id] = _clock.Now;
    }

    private async Task PlayerKilledAsync(GameSession killer, GameSession victim)
    {
        if (!Hostile(killer, victim)) return;
        var recent = _pvpHits.TryRemove(victim.Role!.Id, out var hits) ? hits : new();
        var scorers = _sessions.All().Where(s => s.Role != null && (s == killer ||
            recent.TryGetValue(s.Role.Id, out var time) && _clock.Now - time <= 30 && s != victim)).ToArray();
        var selected = scorers.ToDictionary(s => s, s => (Role: s.Role, Attachment: s.Attachment));
        var victimRole = victim.Role;
        var victimAttachment = victim.Attachment;
        using var reservation = await EnterGateAsync("escort/player kill", killer,
            additionalRoles: scorers.Select(s => s.Role!.Id).Append(victim.Role!.Id).ToArray());
        using var stagedEscortLedger = StageLedger();
        try
        {
            if (!ReferenceEquals(victim.Role, victimRole) || !ReferenceEquals(victim.Attachment, victimAttachment)) return;
            var transaction = _roles.NewTransaction();
            var stagedScores = new List<(GameSession Session, long RoleId, object? Attachment, RoleState State)>();
            foreach (var scorer in scorers)
            {
                if (!ReferenceEquals(scorer.Role, selected[scorer].Role)
                    || !ReferenceEquals(scorer.Attachment, selected[scorer].Attachment)) continue;
                if (scorer.TerritoryEscort == null || Own(scorer) is not { } guild || Own(victim)?.Id == guild.Id) continue;
                long score = scorer == killer ? 1000 : 500; // EscortScoreParamKill / EscortScoreParamAssist.
                var stagedRole = CopyRole(scorer.TerritoryEscort);
                stagedRole.ScoreByRound[Round] = checked(stagedRole.ScoreByRound.GetValueOrDefault(Round) + score);
                AddGuildScore(guild.Id, score);
                SaveRole(scorer.Role!.Id, stagedRole, transaction);
                stagedScores.Add((scorer, scorer.Role.Id, scorer.Attachment, stagedRole));
            }
            await CommitAsync(transaction, "player-kill", actor: killer);
            foreach (var staged in stagedScores)
                if (staged.Session.Role?.Id == staged.RoleId
                    && ReferenceEquals(staged.Session.Attachment, staged.Attachment))
                    staged.Session.TerritoryEscort = staged.State;
        }
        finally { stagedEscortLedger.Dispose(); _gate.Release(); reservation?.Dispose(); }
        await RoleFanout.EachAsync(scorers.Where(s => s.TerritoryEscort != null), SendScoreAsync);
    }
}
