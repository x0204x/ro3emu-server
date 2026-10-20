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
    // Separate from monsters (2^41) and passive followers (2^52); remain exact in the Lua number range.
    private static long nextUid = 1L << 51;
    private readonly GameSession owner;
    private readonly World world;
    private readonly MonsterService monsters;
    private readonly CombatService combat;
    private long nextAttackMs;
    private long nextMoveMs;
    private long nextSearchMs;
    private MonsterUnit? target;
    private bool moving;
    private readonly Dictionary<int, long> skillReadyMs = new();

    private TowerPetUnit(GameSession owner, World world, MonsterService monsters, CombatService combat) =>
        (this.owner, this.world, this.monsters, this.combat) = (owner, world, monsters, combat);

    public int PetId { get; private init; }
    public GameSession Owner => owner;
    public long OwnerId => owner.Role!.Id;
    public int NpcTemplateId { get; private init; }
    public int Level { get; private init; }
    public int Element { get; private init; }
    public int Camp { get; private init; }
    public IReadOnlyList<AttackSkill> AttackSkills { get; private init; } = [];
    public IReadOnlyList<AttackSkill> SupportSkills { get; private init; } = [];
    internal IReadOnlyDictionary<int, double> BaseAttributes { get; private init; } = new Dictionary<int, double>();
    internal HashSet<int> PublishedAttributeIds { get; } = new();
    public StatSheet CombatStats => combat.TowerPetStats(this);
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
        MonsterService monsters, CombatService combat, int id, long instance, float x, float y, float z)
    {
        var t = Build(data, owned, id);
        if (t == null) return null;
        return new TowerPetUnit(owner, world, monsters, combat)
        {
            Uid = Interlocked.Increment(ref nextUid), SceneEntry = owner.SceneEntry, InstanceId = instance,
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
        if (now < nextAttackMs || now < skillReadyMs.GetValueOrDefault(skill.Id)) return false;
        skillReadyMs[skill.Id] = now + skill.CooldownMs;
        nextAttackMs = now + ActionMsFor(skill);
        StopMoving();
        return true;
    }

    /// <summary>(inferred) Apply the configured attack-speed influence using the shared 100-based ASPD root.</summary>
    internal int ActionMsFor(AttackSkill skill) => skill.AttackSpeedInfluenced
        ? (int)Math.Max(200, Math.Ceiling(skill.ActionMs * 100.0 / Math.Max(1, CombatStats[115]))) : skill.ActionMs;

    public override void Tick(MapInstance map, GameTime time)
    {
        if (Dead || Hp <= 0 || owner.Dead || owner.SceneLoading || owner.TowerAttempt is not { Loaded: true, Settled: false } a
            || a.InstanceId != InstanceId || owner.WorldScene != WorldScene) return;
        long now = MonsterService.NowMs;
        if (now < nextAttackMs) return;
        // (inferred) Support takes priority when ready and does not require an enemy in melee range.
        var support = SupportSkills.LastOrDefault(s => now >= skillReadyMs.GetValueOrDefault(s.Id));
        if (support != null) { world.TrackDetached("tower pet support", () => combat.TowerPetSupportAsync(owner, this, support.Id)); return; }
        if (AttackSkills.Count == 0) return;
        // Use NPCConfig._iAI -> AIConfig._iSearchTime for target-search cadence. Nearest-enemy
        // selection is inferred; the named NPC _iAISpecial server scripts are not in the client export.
        if (target == null || target.Dead || target.Hp <= 0 || now >= nextSearchMs)
        {
            nextSearchMs = now + SearchIntervalMs;
            target = map.Units.OfType<MonsterUnit>().Where(m => !m.Dead && m.Hp > 0 && m.InstanceId == InstanceId)
                .MinBy(m => MonsterService.Distance(X, Z, m.X, m.Z));
        }
        if (target == null) { StopMoving(); return; }
        float distance = MonsterService.Distance(X, Z, target.X, target.Z);
        Face = MonsterService.Angle(X, Z, target.X, target.Z);
        if (distance > AttackRange + 0.25f)
        {
            float step = Math.Min(distance - AttackRange * 0.8f, SpeedCm / 100f * (float)time.Delta.TotalSeconds);
            if (step > 0 && distance > 0)
            {
                moving = true;
                X += (target.X - X) / distance * step;
                Z += (target.Z - Z) / distance * step;
                map.Moved(this);
                if (now >= nextMoveMs)
                {
                    nextMoveMs = now + 200;
                    world.TrackDetached("tower pet move", () => world.MoveUnitAsync(this, new MovementChangeNtf
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
        if (now < nextAttackMs) return;
        // Prefer the later active entry when its own cooldown is ready; otherwise use the earlier attack.
        var chosen = AttackSkills.Reverse().FirstOrDefault(s => now >= skillReadyMs.GetValueOrDefault(s.Id)
            && distance <= s.Range + 0.25f);
        if (chosen == null) return;
        world.TrackDetached("tower pet attack", () => combat.TowerPetAttackAsync(owner, this, target, chosen.Id));
    }

    /// <summary>31081 closes the walk before a cast or idle period, as for ordinary monster movement.</summary>
    private void StopMoving()
    {
        if (!moving) return;
        moving = false;
        world.TrackDetached("tower pet stop", () => world.MoveUnitAsync(this, new MovementChangeNtf
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
        combat.PopulateTowerPetBuffs(this, entity);
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
