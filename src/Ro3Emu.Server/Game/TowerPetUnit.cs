using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A tower-only, owner-bound type-7 combat actor. Its NPC template and displayed base attributes follow
/// target selection, movement cadence and attack timing are (inferred).
/// The ordinary 5017 setup plan and passive follower do not determine this four-slot tower formation.
/// </summary>
public sealed class TowerPetUnit : WorldUnit
{
    /// <summary>Uids above this one: separate from monsters (2^41) and passive followers (2^52), exact in the Lua
    /// number range.</summary>
    internal const long UidBase = 1L << 51;
    private readonly GameSession _owner;
    private readonly World _world;
    private readonly MonsterService _monsters;
    private readonly CombatService _combat;
    private long _nextAttackMs;
    private long _nextMoveMs;
    private long _nextSearchMs;
    private MonsterUnit? _target;
    private bool _moving;
    private readonly Dictionary<int, long> _skillReadyMs = new();

    private TowerPetUnit(GameSession owner, World world, MonsterService monsters, CombatService combat) =>
        (this._owner, this._world, this._monsters, this._combat) = (owner, world, monsters, combat);

    public int PetId { get; private init; }
    public GameSession Owner => _owner;
    public long OwnerId => _owner.Role!.Id;
    public int NpcTemplateId { get; private init; }
    public int Level { get; private init; }
    public int Element { get; private init; }
    public int Camp { get; private init; }
    public IReadOnlyList<AttackSkill> AttackSkills { get; private init; } = [];
    public IReadOnlyList<AttackSkill> SupportSkills { get; private init; } = [];
    internal IReadOnlyDictionary<int, double> BaseAttributes { get; private init; } = new Dictionary<int, double>();
    internal HashSet<int> PublishedAttributeIds { get; } = new();
    public StatSheet CombatStats => _combat.TowerPetStats(this);
    public int SpeedCm => (int)CombatStats[113];
    public int SearchIntervalMs { get; private init; }
    public float AttackRange { get; private init; }
    public long MaxHp => CombatStats[101];
    public long MaxSp => CombatStats[102];
    public long Atk => CombatStats[103];
    public long Matk => CombatStats[104];
    public long Def => CombatStats[105];
    public long Mdef => CombatStats[106];
    public long Hit => CombatStats[109];
    public long Flee => CombatStats[110];
    public long Cri => CombatStats[111];
    public long Hp { get; set; }
    public bool Dead { get; set; }
    public long BornTimeMs { get; private init; }

    /// <summary>Configured support/effect pets can join the formation even when no damage skill is implemented.</summary>
    public static bool CanSpawn(GameData data, DBPetInfo owned, int id) => Build(data, owned, id) != null;

    public static TowerPetUnit? Create(GameData data, DBPetInfo owned, GameSession owner, World world,
        MonsterService monsters, CombatService combat, long uid, int id, long instance, float x, float y, float z)
    {
        var t = Build(data, owned, id);
        if (t == null) return null;
        return new TowerPetUnit(owner, world, monsters, combat)
        {
            Uid = uid, SceneEntry = owner.SceneEntry, InstanceId = instance,
            RuntimeMap = owner.RuntimeMap,
            X = x, Y = y, Z = z, Face = owner.Face,
            PetId = id, NpcTemplateId = t.Value.Npc, Level = t.Value.Level,
            Element = t.Value.Element, Camp = t.Value.Camp, AttackSkills = t.Value.Skills, SupportSkills = t.Value.Support,
            AttackRange = t.Value.Range, SearchIntervalMs = t.Value.SearchMs, BaseAttributes = t.Value.Attributes,
            Hp = Math.Max(1, StatCalculator.Compute(t.Value.Attributes, data)[101]),
            BornTimeMs = MonsterService.NowMs,
        };
    }

    public sealed record AttackSkill(int Id, int CooldownMs, int ActionMs, float Range, bool AttackSpeedInfluenced = false);

    private readonly record struct Stats(int Npc, int Level, int Element, int Camp, AttackSkill[] Skills,
        AttackSkill[] Support, int SearchMs, float Range, Dictionary<int, double> Attributes);

    private static Stats? Build(GameData data, DBPetInfo owned, int id)
    {
        if (!owned.ListPet.TryGetValue(id, out var pet) || data.Row("PetBaseConfig", id) is not { } cfg) return null;
        int npcId = (int)GameData.Int(cfg, "_iMonster");
        if (data.Row("NPCConfig", npcId) is not { } npc || GameData.Int(npc, "_iNPCType") != 7) return null;
        int level = Math.Max(1, pet.Level);
        if (PetCombatRules.Attributes(data, id, pet) is not { } values || PetCombatRules.Star(data, id, pet) is not { } star)
            return null;
        var attackSkills = new List<AttackSkill>();
        var supportSkills = new List<AttackSkill>();
        if (star.TryGetProperty("_kActiveSkill", out var skills) && skills.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var entry in skills.EnumerateArray())
            {
                int candidate = (int)(GameData.Number(entry) ?? 0);
                if (data.Row("SkillConfig", candidate) is not { } row) continue;
                var skill = new AttackSkill(candidate, (int)Math.Max(500, GameData.Int(row, "_iCD")),
                    (int)Math.Max(200, GameData.Int(row, "_iActionTime")),
                    Math.Max(1f, GameData.Int(row, "_iDistanceMax") / 100f), GameData.Int(row, "_iActionTimeInfluence") != 0);
                if (PetCombatRules.SupportFor(row) != null) supportSkills.Add(skill);
                else if (row.TryGetProperty("_kDamageParam1", out var damage)
                    && damage.ValueKind == System.Text.Json.JsonValueKind.Array && damage.GetArrayLength() > 0
                    && GameData.Number(damage[0]) is > 0
                    && GameData.Int(row, "_iRelease") is 1 or 2
                    && GameData.Int(row, "_iRangeType") is 1 or 2)
                    attackSkills.Add(skill);
            }
        // (verified) NPCConfig._iCamp is the actor's combat allegiance. PetBaseConfig._iCamp is the unrelated
        // enum_PetCamp collection/destiny category (LC_PetHelper.GetPetAttributeInfo), not an enemy camp.
        var ai = data.Row("AIConfig", GameData.Int(npc, "_iAI"));
        return new Stats(npcId, level, (int)GameData.Int(npc, "_iElement"), (int)GameData.Int(npc, "_iCamp"),
            attackSkills.ToArray(), supportSkills.ToArray(),
            ai is { } brain ? (int)Math.Max(50, GameData.Int(brain, "_iSearchTime")) : 100,
            attackSkills.Count > 0 ? attackSkills.Min(s => s.Range) : 2f, values);
    }

    /// <summary>(inferred) Reserve one action and its configured cooldown before publishing a cast.</summary>
    internal bool TryBeginCast(AttackSkill skill, long now)
    {
        if (now < _nextAttackMs || now < _skillReadyMs.GetValueOrDefault(skill.Id)) return false;
        _skillReadyMs[skill.Id] = now + skill.CooldownMs;
        _nextAttackMs = now + ActionMsFor(skill);
        StopMoving();
        return true;
    }

    /// <summary>(inferred) Apply the configured attack-speed influence using the shared 100-based ASPD root.</summary>
    internal int ActionMsFor(AttackSkill skill) => skill.AttackSpeedInfluenced
        ? (int)Math.Max(200, Math.Ceiling(skill.ActionMs * 100.0 / Math.Max(1, CombatStats[115]))) : skill.ActionMs;

    public override void Tick(MapInstance map, GameTime time)
    {
        if (Dead || Hp <= 0 || _owner.Dead || _owner.SceneLoading || _owner.TowerAttempt is not { Loaded: true, Settled: false } a
            || a.InstanceId != InstanceId || _owner.WorldScene != WorldScene) return;
        long now = MonsterService.NowMs;
        if (now < _nextAttackMs) return;
        // (inferred) Support takes priority when ready and does not require an enemy in melee range.
        var support = SupportSkills.LastOrDefault(s => now >= _skillReadyMs.GetValueOrDefault(s.Id));
        if (support != null) { _world.TrackDetached("tower pet support", () => _combat.TowerPetSupportAsync(_owner, this, support.Id)); return; }
        if (AttackSkills.Count == 0) return;
        // Use NPCConfig._iAI -> AIConfig._iSearchTime for target-search cadence. Nearest-enemy
        // selection is inferred; the named NPC _iAISpecial server scripts are not in the client export.
        if (_target == null || _target.Dead || _target.Hp <= 0 || now >= _nextSearchMs)
        {
            _nextSearchMs = now + SearchIntervalMs;
            _target = map.Units.OfType<MonsterUnit>().Where(m => !m.Dead && m.Hp > 0 && m.InstanceId == InstanceId)
                .MinBy(m => MonsterService.Distance(X, Z, m.X, m.Z));
        }
        if (_target == null) { StopMoving(); return; }
        float distance = MonsterService.Distance(X, Z, _target.X, _target.Z);
        Face = MonsterService.Angle(X, Z, _target.X, _target.Z);
        if (distance > AttackRange + 0.25f)
        {
            float step = Math.Min(distance - AttackRange * 0.8f, SpeedCm / 100f * (float)time.Delta.TotalSeconds);
            if (step > 0 && distance > 0)
            {
                _moving = true;
                X += (_target.X - X) / distance * step;
                Z += (_target.Z - Z) / distance * step;
                map.Moved(this);
                if (now >= _nextMoveMs)
                {
                    _nextMoveMs = now + 200;
                    _world.TrackDetached("tower pet move", () => _world.MoveUnitAsync(this, new MovementChangeNtf
                    {
                        Uid = Uid, TransType = MovementTransitionType.MoveTransNormal,
                        MovementInfo = new MovementInfo
                        {
                            Pos = Pos, Face = Face, ServerTime = now,
                            MoveType = MovementType.MoveTypeWalk, MoveDuration = 200,
                            MovementWalk = new MovementWalk { Dir = Face, Speed = SpeedCm / 100f, EndHeight = Y }
                        },
                    }));
                }
            }
            return;
        }
        StopMoving();
        if (now < _nextAttackMs) return;
        // Prefer the later active entry when its own cooldown is ready; otherwise use the earlier attack.
        var chosen = AttackSkills.Reverse().FirstOrDefault(s => now >= _skillReadyMs.GetValueOrDefault(s.Id)
            && distance <= s.Range + 0.25f);
        if (chosen == null) return;
        _world.TrackDetached("tower pet attack", () => _combat.TowerPetAttackAsync(_owner, this, _target, chosen.Id));
    }

    /// <summary>31081 closes the walk before a cast or idle period, as for ordinary monster movement.</summary>
    private void StopMoving()
    {
        if (!_moving) return;
        _moving = false;
        _world.TrackDetached("tower pet stop", () => _world.MoveUnitAsync(this, new MovementChangeNtf
        {
            Uid = Uid, TransType = MovementTransitionType.MoveTransNormal,
            MovementInfo = new MovementInfo
            {
                Pos = Pos, Face = Face, ServerTime = MonsterService.NowMs,
                MoveType = MovementType.MoveTypeNone
            },
        }));
    }

    public override EntityBase Entity()
    {
        var entity = new EntityBase
        {
            Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcTemplateId, Owner = OwnerId, EntityType = 7,
            EntityCamp = Camp, ElementType = Element, State = Dead ? EntityState.Dead : EntityState.Alive,
            Name = "", Face = Face, BornTimeMs = BornTimeMs, NotifyType = EntityBase.Types.NotifyType.All,
            MovementInfo = new MovementInfo { Pos = Pos, Face = Face, ServerTime = MonsterService.NowMs },
        };
        // (verified) EntityBase.property is the shared attribute carrier. ATK/DEF are not required for
        // client damage calculation (31101 is authoritative); including them is an inferred complete snapshot.
        var stats = CombatStats;
        foreach (var (idx, value) in stats.Data.OrderBy(p => p.Key))
        {
            entity.Property.Add(new AoIProperty { Idx = idx, Data = value, BaseData = stats.Base.GetValueOrDefault(idx, value) });
            PublishedAttributeIds.Add(idx);
        }
        _combat.PopulateTowerPetBuffs(this, entity);
        return entity;
    }

    public EntityBase StateChange() => new()
    {
        Uuid = (ulong)Uid, NotifyType = EntityBase.Types.NotifyType.Base,
        State = Dead ? EntityState.Dead : EntityState.Alive, EntityTemplateID = (ulong)NpcTemplateId,
        EntityCamp = Camp, ElementType = Element,
    };

    public AOIAttrChange HpChange() => new()
    {
        EntityUuid = (ulong)Uid, Property = { new AoIProperty { Idx = 2000, Data = Hp } },
    };
}
