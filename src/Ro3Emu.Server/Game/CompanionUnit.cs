using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// An allied story companion of a scripted dungeon step (Reconstructed/scripted-scenes.json, an actor marked
/// "companion"; Payon Forest's Boya and Wule). The client data gives the companion everything but its behaviour: an
/// NPCConfig _iNPCType 1 row in the player's camp (_iCamp 1) whose monster template holds its HP and attack
/// (_iAttribute, _kAttriScale, _iLevel), its own skills (_kSkills, with SkillConfig _iCD, _iDistanceMax, _iActionTime)
/// its walking speed (_iSpeed) and its AIConfig row (_iAI: _iSearchTime, _iAttackTickTime, _iViewRange, _iChaseDistance,
/// _iStayInPlace). How it fights is (inferred): the server script behind it is not in the client. It attacks the nearest
/// living monster of its room within its view, with the first listed skill whose cooldown is ready and whose reach holds
/// that monster, one cast per action and at most one per attack tick. When no ready skill reaches it, it walks toward it
/// (31081, as a tower pet walks) until one does, never farther than _iChaseDistance from its point; with no target it
/// walks back to its point (_iStayInPlace 1 read as "goes back to its point", not "never moves"). It never targets
/// players or other companions, and players cannot target it: it is no MonsterUnit. Separate from TowerPetUnit, which
/// is bound to a Pet Tower attempt.
/// </summary>
internal sealed class CompanionUnit(CompanionUnit.Kit kit, World world, CombatService combat) : WorldUnit
{
    /// <summary>Uids from 2^50: apart from monsters (2^41), scene actors (2^47), chests and campfire seeds (2^48), stall
    /// projections (2^49) and tower pets (2^51), exact in the Lua number range.</summary>
    internal const long UidBase = 1L << 50;
    /// <summary>NPCConfig _iNPCType 1: the client creates the unit as a monster (entity type 1).</summary>
    private const int MonsterNpcType = 1;
    /// <summary>NPCConfig _iCamp 1: the player's side (5300086/5300087; their hostile versions 5300080/5300081 are camp 3).</summary>
    internal const int PlayerCamp = 1;
    /// <summary>AIConfig _iStayInPlace 1: read as "goes back to its point when it has no target" (inferred).</summary>
    private const int StaysInPlace = 1;
    /// <summary>The shortest action a cast takes (inferred, as for tower pets).</summary>
    private const int MinActionMs = 200;
    /// <summary>The level and maximum SP attribute ids (2002, 102) the head bar reads, as for monster-type scene actors.</summary>
    private const int Level = 2002, MaxSp = 102;
    /// <summary>(inferred, as for tower pets) It walks until it stands at this share of the reach, and resends its walk
    /// (31081) every this many ms; within this many metres of its point it is home.</summary>
    private const float ApproachShare = 0.8f, HomeMetres = 0.25f;
    private const int WalkResendMs = 200;

    /// <summary>A skill of NPCConfig _kSkills: SkillConfig _iCD, _iActionTime and _iDistanceMax (metres).</summary>
    internal sealed record Skill(int Id, int CooldownMs, int ActionMs, float Range)
    {
        // 只影響原服已觀察的故事支援技；不可將治癒／祝福硬套一般對敵傷害公式。
        public bool CanDamageEnemy { get; init; } = true;
    }

    /// <summary>What the client data says about a companion: its template, skills, walking speed (cm/s) and AI parameters
    /// (view range and chase distance in metres).</summary>
    internal sealed record Kit(int NpcId, MonsterTemplate Template, IReadOnlyList<Skill> Skills, int SpeedCm, int SearchMs,
        int AttackTickMs, float ViewRange, float ChaseDistance)
    {
        public bool RotateAttackSkills { get; init; }
    }

    private readonly Dictionary<int, long> _readyMs = [];
    private long _nextAttackMs, _nextSearchMs, _nextWalkMs;
    private MonsterUnit? _target;
    private bool _moving;
    private int _lastAttackSkillId;

    public Kit Spec { get; } = kit;
    public int NpcId => Spec.NpcId;
    public MonsterTemplate Template => Spec.Template;
    /// <summary>The scene-editor point it stands at, and where that point is.</summary>
    public required long Point { get; init; }
    public required float HomeX { get; init; }
    public required float HomeZ { get; init; }
    public long Hp { get; set; } = kit.Template.MaxHp;
    public bool Dead { get; set; }
    public long BornMs { get; init; } = MonsterService.NowMs;
    /// <summary>The participant its hits and kills are credited to (null: the run is over).</summary>
    public required Func<GameSession?> Credit { get; init; }
    /// <summary>Called once when it falls; its owner removes it.</summary>
    public required Func<CompanionUnit, Task> Fell { get; init; }

    /// <summary>玩家陣營monster-type NPC同伴。優先用本身_kSkills；空表只接受實包繫結且AISpecial吻合的12名故事NPC。</summary>
    public static Kit? Build(GameData data, MonsterService monsters, int npcId)
    {
        if (NPCConfig.Get(data, npcId) is not { NPCType: MonsterNpcType } npc
            || monsters.Template(npcId) is not { Camp: PlayerCamp } template
            || AIConfig.Get(data, npc.Ai) is not { StayInPlace: StaysInPlace } ai) return null;
        long[] ids = npc.Skills;
        bool captured = ids.Length == 0;
        if (captured) ids = CapturedStorySkills(npcId, npc);
        if (ids.Length == 0) return null;
        var skills = new List<Skill>();
        foreach (long id in ids)
        {
            if (SkillConfig.Get(data, id) is not { } row) return null;
            skills.Add(new Skill((int)id, (int)Math.Max(0, row.Cd), (int)Math.Max(MinActionMs, row.ActionTime), row.DistanceMax / 100f)
            {
                CanDamageEnemy = !captured || CapturedEnemyAttack(npcId, row),
            });
        }
        return new Kit(npcId, template, skills, (int)Math.Max(0, npc.Speed), (int)Math.Max(1, ai.SearchTime),
            (int)Math.Max(0, ai.AttackTickTime), ai.ViewRange / 100f, ai.ChaseDistance / 100f)
        { RotateAttackSkills = captured };
    }

    /// <summary>虎王原服2026-10-31 seq35075–39192繫結的12名冒險者；不是世界NPC的通用技能推測。</summary>
    private static long[] CapturedStorySkills(int npcId, NPCConfig npc) => npc.Ai != 5300006 ? [] : (npcId, npc.AISpecial) switch
    {
        (5300007, "MainStory_ZhanShi") or (5300073, "MainStory_ZhanShi2") => [5330101, 5330201, 5330301, 5330401],
        (5300008, "MainStory_FaShi") or (5300074, "MainStory_FaShi2") => [5330901, 5331001, 5331101, 5331201],
        (5300009, "MainStory_MuShi") or (5300075, "MainStory_MuShi2") => [5330501, 5330601, 5330701, 5330801, 5331301],
        (5300070, "MainStory_LieRen") or (5300076, "MainStory_LieRen2") => [5331501, 5331601, 5331701],
        (5300071, "MainStory_DaoZei") or (5300077, "MainStory_DaoZei2") => [1511601, 5332301, 5332401, 5332501],
        (5300072, "MainStory_ShangRen") or (5300078, "MainStory_ShangRen2") => [5331901, 5332001, 5332101],
        _ => [],
    };

    // 牧師本次只對友方／自己施放；其他人僅開放已觀察對敵且配置有正傷害係數的技能。
    // 支援技的選人、buff及治癒路由尚未還原（UNKNOWN），不可先變成敵方傷害。
    private static bool CapturedEnemyAttack(int npcId, SkillConfig skill) => npcId is not (5300009 or 5300075)
        && skill.DmgType > 0 && (skill.DamageParam1.Any(v => v > 0) || skill.DamageParam2.Any(v => v > 0));

    /// <summary>(inferred) 依既有順序或故事輪替，挑選冷卻已好且可對敵、距離可達的技能。</summary>
    public static Skill? Pick(Kit kit, IReadOnlyDictionary<int, long> readyMs, long now, float distance, int afterSkill = 0)
    {
        int start = 0;
        // (inferred) 原服各AI技能優先序未知，暫輪替可用攻擊，避免CD0永遠壓住其餘已知技能。
        if (kit.RotateAttackSkills)
            for (int i = 0; i < kit.Skills.Count; i++)
                if (kit.Skills[i].Id == afterSkill) { start = i + 1; break; }
        for (int i = 0; i < kit.Skills.Count; i++)
        {
            var skill = kit.Skills[(start + i) % kit.Skills.Count];
            if (skill.CanDamageEnemy && now >= readyMs.GetValueOrDefault(skill.Id) && distance <= skill.Range) return skill;
        }
        return null;
    }

    /// <summary>(inferred) The next cast may start after this one's action and the AI's attack tick.</summary>
    public static long NextAttackMs(Kit kit, Skill skill, long now) => now + Math.Max(skill.ActionMs, kit.AttackTickMs);

    public override void Tick(MapInstance map, GameTime time)
    {
        if (Dead || Hp <= 0) return;
        long now = MonsterService.NowMs;
        if (now < _nextAttackMs) return;
        if (_target is not { Dead: false, Hp: > 0 } || now >= _nextSearchMs)
        {
            _nextSearchMs = now + Spec.SearchMs;
            _target = Nearest(map);
        }
        if (_target is not { } target)
        {
            // (inferred) _iStayInPlace 1: with no target it goes back to its point.
            WalkToward(map, time, now, HomeX, HomeZ, MonsterService.Distance(X, Z, HomeX, HomeZ) - HomeMetres);
            return;
        }
        float distance = MonsterService.Distance(X, Z, target.X, target.Z);
        if (Pick(Spec, _readyMs, now, distance, _lastAttackSkillId) is not { } skill)
        {
            Approach(map, time, now, target, distance);
            return;
        }
        StopMoving(now);
        // Reserved here, on the loop: the cast itself runs detached.
        _readyMs[skill.Id] = now + skill.CooldownMs;
        _lastAttackSkillId = skill.Id;
        _nextAttackMs = NextAttackMs(Spec, skill, now);
        Face = MonsterService.Angle(X, Z, target.X, target.Z);
        world.TrackDetached("companion attack", () => combat.CompanionAttackAsync(this, target, skill));
    }

    /// <summary>(inferred) No ready skill reaches the target: walk until the longest reach of a ready skill holds it; with
    /// every skill cooling down it waits where it is.</summary>
    private void Approach(MapInstance map, GameTime time, long now, MonsterUnit target, float distance)
    {
        float reach = 0;
        foreach (var s in Spec.Skills)
            if (s.CanDamageEnemy && now >= _readyMs.GetValueOrDefault(s.Id)) reach = Math.Max(reach, s.Range);
        if (reach <= 0) StopMoving(now);
        else WalkToward(map, time, now, target.X, target.Z, distance - reach * ApproachShare);
    }

    /// <summary>One tick's walk of at most <paramref name="metres"/> toward (x, z) at _iSpeed, never farther than
    /// _iChaseDistance from its point (inferred); the walk goes out as 31081, as a tower pet's does.</summary>
    private void WalkToward(MapInstance map, GameTime time, long now, float x, float z, float metres)
    {
        float distance = MonsterService.Distance(X, Z, x, z);
        float step = Math.Min(metres, Spec.SpeedCm / 100f * (float)time.Delta.TotalSeconds);
        if (step <= 0 || distance <= 0)
        {
            StopMoving(now);
            return;
        }
        float nx = X + (x - X) / distance * step, nz = Z + (z - Z) / distance * step;
        if (MonsterService.Distance(nx, nz, HomeX, HomeZ) > Spec.ChaseDistance)
        {
            StopMoving(now);
            return;
        }
        _moving = true;
        Face = MonsterService.Angle(X, Z, x, z);
        (X, Z) = (nx, nz);
        map.Moved(this);
        if (now < _nextWalkMs) return;
        _nextWalkMs = now + WalkResendMs;
        SendMove(Walk(now));
    }

    private MovementChangeNtf Walk(long now) => new()
    {
        Uid = Uid, TransType = MovementTransitionType.MoveTransNormal,
        MovementInfo = new MovementInfo
        {
            Pos = Pos, Face = Face, ServerTime = now, MoveType = MovementType.MoveTypeWalk, MoveDuration = WalkResendMs,
            MovementWalk = new MovementWalk { Dir = Face, Speed = Spec.SpeedCm / 100f, EndHeight = Y }
        },
    };

    /// <summary>31081 closes the walk before a cast or a halt, as for monsters and tower pets.</summary>
    private void StopMoving(long now)
    {
        if (!_moving) return;
        _moving = false;
        SendMove(new MovementChangeNtf
        {
            Uid = Uid, TransType = MovementTransitionType.MoveTransNormal,
            MovementInfo = new MovementInfo { Pos = Pos, Face = Face, ServerTime = now, MoveType = MovementType.MoveTypeNone },
        });
    }

    // The lambda lives here, so a tick that sends nothing allocates no closure.
    private void SendMove(MovementChangeNtf move) => world.TrackDetached("companion move", () => world.MoveUnitAsync(this, move));

    /// <summary>The nearest living monster of its room within its view range (_iViewRange).</summary>
    private MonsterUnit? Nearest(MapInstance map)
    {
        MonsterUnit? nearest = null;
        float best = Spec.ViewRange;
        foreach (var unit in map.Units)
            if (unit is MonsterUnit { Dead: false, Hp: > 0 } m && m.InstanceId == InstanceId
                && MonsterService.Distance(X, Z, m.X, m.Z) is var d && d <= best)
                (nearest, best) = (m, d);
        return nearest;
    }

    public override EntityBase Entity()
    {
        var t = Template;
        var entity = new EntityBase
        {
            Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = MonsterNpcType, Name = "", EntityCamp = t.Camp,
            ElementType = t.Element, Face = Face, State = Dead ? EntityState.Dead : EntityState.Alive, BornTimeMs = BornMs,
            NotifyType = EntityBase.Types.NotifyType.All,
            MovementInfo = new MovementInfo
            {
                Pos = Pos, Face = Face, ServerTime = MonsterService.NowMs, MoveType = MovementType.MoveTypeNone
            },
        };
        // The head bar's attributes, as a monster-type scene actor carries them, with its current HP.
        foreach (var (idx, value) in new (int, long)[] { (AttributeId.MaxHp, t.MaxHp), (AttributeId.Hp, Hp), (Level, t.Level),
                     (AttributeId.Speed, Spec.SpeedCm), (MaxSp, t.MaxSp), (AttributeId.Sp, t.MaxSp) })
            entity.Property.Add(new AoIProperty { Idx = idx, Data = value, BaseData = value });
        return entity;
    }

    public EntityBase StateChange() => new()
    {
        Uuid = (ulong)Uid, NotifyType = EntityBase.Types.NotifyType.Base,
        State = Dead ? EntityState.Dead : EntityState.Alive, EntityTemplateID = (ulong)NpcId, EntityCamp = Template.Camp,
        ElementType = Template.Element,
    };

    public AOIAttrChange HpChange() => new()
    {
        EntityUuid = (ulong)Uid, Property = { new AoIProperty { Idx = AttributeId.Hp, Data = Hp } },
    };
}
