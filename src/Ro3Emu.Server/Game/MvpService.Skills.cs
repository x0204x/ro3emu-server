using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>A boss ground object (NPCConfig _iNPCType 4, e.g. 5000037 "Sanctified Ground"), owned by its boss.</summary>
internal sealed class MvpFieldUnit : WorldUnit
{
    public required int NpcId { get; init; }
    public required int EntityType { get; init; }
    public required MonsterUnit Boss { get; init; }
    public required long BornMs { get; init; }
    public required long EndMs { get; init; }
    public bool Gone { get; set; }

    public override EntityBase Entity() => new()
    {
        Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = EntityType, Owner = Boss.Uid,
        AiMasterEntityId = (ulong)Boss.Uid, EntityCamp = Boss.CampOverride ?? Boss.Template.Camp, State = EntityState.Alive,
        BornTimeMs = BornMs, Face = Face, MovementInfo = new MovementInfo { Pos = Pos, Face = Face, ServerTime = MonsterService.NowMs },
        NotifyType = EntityBase.Types.NotifyType.All,
    };
}

internal enum BossSkillKind { Summon, Field, SelfBuff }

/// <summary>Minions of a summon skill: NPC, count, life, and the blast their spawn skill (_kRespawnSkill) ends with.</summary>
internal sealed record MinionPlan(int Npc, int Count, int LifeMs, int BlastSkill, float BlastRadius, int BlastTargets);

/// <summary>A marked ground field: the marking skill's buff/targets/range, then the field NPC's life, tick and radius.</summary>
internal sealed record FieldPlan(int Npc, int MarkSkill, int MarkBuff, int MarkMs, int Targets, float SelectRadius,
    int LifeMs, int TickMs, float Radius);

internal sealed record BossSkillPlan(int Skill, BossSkillKind Kind, int CdMs, int ActionMs, int PublicCdMs,
    MinionPlan? Minions = null, FieldPlan? Field = null, int Buff = 0, int BuffMs = 0);

public sealed partial class MvpService
{
    /// <summary>
    /// The field-MVP skills the client data settles. The boss of each skill comes from its own description
    /// ("Angeling summons...", "Golden Thief Bug increases its own..."); everything else is resolved from the rows: the
    /// minion or ground NPC from SkillConfig _kLogicData, its kind from NPCConfig _iNPCType (1 minion, 4 ground object),
    /// self buffs from _kBuff2. A field skill is paired with the "...Select" skill of the same script family that marks
    /// the players (AngelingSelect / DevilingSelect, identical description 10110300450 on the Deviling pair).
    /// 5206401 "Summon Thief Bug" (NPC 5001003 Lv 14) is not listed: no NPCConfig/AIConfig/Lua row names its owner and it
    /// belongs to the description-less second Golden Thief Bug set 5206101-5206801 (with "Hiding"), not the field MVP.
    /// </summary>
    private static readonly Dictionary<int, (int Skill, int Select)[]> BossSkillIds = new()
    {
        [5020001] = [(5202101, 0), (5201701, 5201601)], // Angeling: Summon Self-Destruct Poring, Realm of Light
        [5020003] = [(5202901, 0), (5202801, 5202701)], // Deviling: Summon Bombring, Realm of Darkness
        [5020002] = [(5202201, 0), (5202301, 0)],       // Golden Thief Bug: Summon Thief Bug, Frenzy
    };

    /// <summary>A skill this service casts for its field MVP itself (its plans and their marking selectors): the generic
    /// boss scheduler (BossSkillSets) leaves it out of the boss's rotation.</summary>
    internal static bool OwnsSkill(int bossNpc, int skill) =>
        BossSkillIds.TryGetValue(bossNpc, out var ids) && Array.Exists(ids, p => p.Skill == skill || p.Select == skill);

    private const int SummonCount = 3;        // descriptions: "summons three", 10110300447 @{1} = 3
    private const int SelfDestructBuff = 5002801; // BuffConfig "Summoned Unit Self-Destruct", its born time is the life
    private const int DefaultMinionLifeMs = 15_000;
    private const long FieldUidBase = 1L << 55;
    private long _fieldSerial;
    private readonly Dictionary<int, IReadOnlyList<BossSkillPlan>> _plans = new();
    private HashSet<int>? _frenzyNpcs;

    internal IReadOnlyList<BossSkillPlan> Plans(int bossNpc)
    {
        if (!_plans.TryGetValue(bossNpc, out var plans)) _plans[bossNpc] = plans = ResolveBossSkills(_data, bossNpc);
        return plans;
    }

    internal static List<BossSkillPlan> ResolveBossSkills(GameData data, int bossNpc)
    {
        var plans = new List<BossSkillPlan>();
        if (!BossSkillIds.TryGetValue(bossNpc, out var ids)) return plans;
        foreach (var (skill, select) in ids)
        {
            if (data.Row("SkillConfig", skill) is not { } row) continue;
            int cd = (int)GameData.Int(row, "_iCD"), action = (int)Math.Max(300, GameData.Int(row, "_iActionTime"));
            int pub = Ints(row, "_iPubCD").FirstOrDefault();
            int npc = Ints(row, "_kLogicData").FirstOrDefault();
            var npcRow = npc > 0 ? data.Row("NPCConfig", npc) : null;
            long npcType = npcRow is { } typed ? GameData.Int(typed, "_iNPCType") : 0;
            if (npcType == 1)
                plans.Add(new(skill, BossSkillKind.Summon, cd, action, pub, Minions: MinionOf(data, npcRow!.Value, npc)));
            else if (npcType == 4 && data.Row("SkillConfig", select) is { } mark)
                plans.Add(new(skill, BossSkillKind.Field, cd, action, pub, Field: FieldOf(data, row, mark, npc)));
            else if (BuffTuples(row, "_kBuff2").FirstOrDefault() is { Buff: > 0 } buff)
                plans.Add(new(skill, BossSkillKind.SelfBuff, cd, action, pub, Buff: buff.Buff, BuffMs: buff.Ms));
        }
        return plans;
    }

    /// <summary>Life: the self-destruct born buff's time (Thief Bug 60 s), else the blast skill's described delay
    /// (5201501 "explode after @{1}=15 sec"). The blast is the minion's _kRespawnSkill with its _kRange / _iTargetMax.</summary>
    private static MinionPlan MinionOf(GameData data, JsonElement npcRow, int npc)
    {
        int blast = (int)GameData.Int(npcRow, "_kRespawnSkill");
        var blastRow = blast > 0 ? data.Row("SkillConfig", blast) : null;
        int life = BornBuffs(npcRow).Where(b => b.Buff == SelfDestructBuff && b.Ms > 0).Select(b => b.Ms).FirstOrDefault();
        if (life == 0 && blastRow is { } described && DescParam(described, 1) is double seconds && seconds > 0) life = (int)(seconds * 1000);
        return new MinionPlan(npc, SummonCount, life > 0 ? life : DefaultMinionLifeMs, blastRow != null ? blast : 0,
            blastRow is { } b ? Ints(b, "_kRange").FirstOrDefault() / 100f : 0,
            blastRow is { } t ? (int)Math.Max(1, GameData.Int(t, "_iTargetMax")) : 0);
    }

    /// <summary>
    /// The select skill marks up to its _iTargetMax players within its _kRange with its _kBuff2 buff ("Creates a Light
    /// Field underfoot after @{1} seconds", 5 s); the field skill's description "@{1} sec ... damage every second ... for
    /// @{2} sec" gives the 1 s tick and the 20 s life; the radius is the field skill show's m_fAtkRange (5 m, inferred).
    /// </summary>
    private static FieldPlan FieldOf(GameData data, JsonElement row, JsonElement mark, int npc)
    {
        var markBuff = BuffTuples(mark, "_kBuff2").FirstOrDefault();
        int life = DescParam(row, 2) is double seconds && seconds > 0 ? (int)(seconds * 1000) : 20_000;
        float radius = data.SkillTemplate(GameData.Int(row, "_iScriptShow")) is { } show
            && show.TryGetProperty("m_fAtkRange", out var range) && GameData.Number(range) is double r && r > 0 ? (float)r : 5f;
        return new FieldPlan(npc, (int)GameData.Int(mark, "_iID"), markBuff.Buff, markBuff.Ms,
            (int)Math.Max(1, GameData.Int(mark, "_iTargetMax")), Ints(mark, "_kRange").FirstOrDefault() / 100f,
            life, 1000, radius);
    }

    // ---- casting ----

    /// <summary>
    /// While fighting, the boss starts the first listed skill whose cooldown (_iCD) is up, at most one skill per action /
    /// public cooldown (_iPubCD) (inferred: the AI scripts are server-only). Out of combat for AIConfig's
    /// _iUnFightParam ms, it takes _iUnFightBUFF (901401) and resets its skills and adds (inferred).
    /// </summary>
    private void TickBossSkills(State st, MonsterUnit boss, long now)
    {
        if (boss.Target != null)
        {
            if (st.LastFightMs == 0 && st.UnFightApplied)
            {
                st.UnFightApplied = false;
                _loop.TrackRuntimeDetached("MVP back in combat", () => EnterCombatAsync(boss));
            }
            st.LastFightMs = now;
            if (st.SkillsStopped || now < st.NextCastMs || boss.BusyUntilMs > now) return;
            var plan = now < boss.NextSpecialMs ? null : Plans(st.Def.NpcId).FirstOrDefault(p => now >= st.SkillReadyMs.GetValueOrDefault(p.Skill));
            if (plan == null)
            {
                // Its own plans first, then the generic specials of its family (BossSkillSets), on the same gap.
                _monsters.TryBossSkill(boss, now);
                return;
            }
            st.SkillReadyMs[plan.Skill] = now + Math.Max(1000, plan.CdMs);
            st.NextCastMs = now + Math.Max(plan.ActionMs, plan.PublicCdMs);
            boss.NextSpecialMs = Math.Max(boss.NextSpecialMs, st.NextCastMs);
            _loop.TrackRuntimeDetached("MVP skill", () => CastAsync(st, boss, plan));
        }
        else if (st.LastFightMs > 0 && now - st.LastFightMs >= UnFight(boss.Template.NpcId).Ms)
        {
            st.LastFightMs = 0;
            st.SkillReadyMs.Clear();
            st.NextCastMs = 0;
            st.UnFightApplied = true;
            _loop.TrackRuntimeDetached("MVP left combat", () => LeaveCombatAsync(st, boss));
        }
    }

    private async Task CastAsync(State st, MonsterUnit boss, BossSkillPlan plan)
    {
        if (st.Retired || boss.Dead || boss.Hp <= 0 || st.Unit != boss) return;
        st.Pending++;
        try
        {
            var target = boss.Target;
            await _world.ToUnitViewersAsync(boss, CombatService.UseInSkill, new UseInSkillInfo
            {
                SrcID = boss.Uid, SkillID = plan.Skill, TargetId = target?.Role?.Id ?? 0,
                TargetPos = plan.Kind == BossSkillKind.Field && target != null ? target.Position.Clone() : boss.Pos,
                Face = boss.Face, SkillMs = plan.ActionMs, StartTime = MonsterService.NowMs,
            });
            switch (plan.Kind)
            {
                case BossSkillKind.Summon:
                    Schedule(st, TimeSpan.FromMilliseconds(plan.ActionMs), () => SummonAsync(st, boss, plan.Minions!), boss);
                    break;
                case BossSkillKind.Field:
                    await MarkAsync(st, boss, plan);
                    break;
                case BossSkillKind.SelfBuff:
                    Schedule(st, TimeSpan.FromMilliseconds(plan.ActionMs), () => SelfBuffAsync(st, boss, plan), boss);
                    break;
            }
        }
        finally { EndPending(st); }
    }

    /// <summary>The minions around the boss after the action time; a minion with a blast casts it at once and stands
    /// (the blast has _bMovement 0 and outlasts its life, inferred), others fight the boss's target.</summary>
    private async Task SummonAsync(State st, MonsterUnit boss, MinionPlan plan)
    {
        if (st.Retired || boss.Dead || st.Unit != boss || _monsters.Template(plan.Npc) is not { } template) return;
        var blastRow = plan.BlastSkill > 0 ? _data.Row("SkillConfig", plan.BlastSkill) : null;
        if (blastRow != null) template = template with { SpeedCm = 0, AttackRange = 0, RoamDistance = 0 };
        for (int i = 0; i < plan.Count; i++)
        {
            if (st.Retired) break;
            double a = _random.NextDouble() * Math.PI * 2;
            var at = new MapPoint(boss.X + (float)Math.Cos(a) * 3, boss.Y, boss.Z + (float)Math.Sin(a) * 3, boss.Face);
            if (await _monsters.SpawnSingleAsync(st.Runtime, plan.Npc, at, customTemplate: template) is not { } m) continue;
            if (st.Retired) break;
            st.Minions.Add(m);
            if (blastRow is { } blast)
            {
                await _world.ToUnitViewersAsync(m, CombatService.UseInSkill, new UseInSkillInfo
                {
                    SrcID = m.Uid, SkillID = plan.BlastSkill, TargetPos = m.Pos, Face = m.Face,
                    SkillMs = (int)GameData.Int(blast, "_iActionTime"), StartTime = MonsterService.NowMs,
                });
            }
            else if (boss.Target is { } target) _monsters.Engage(m, target);
            Schedule(st, TimeSpan.FromMilliseconds(plan.LifeMs), () => MinionLifeEndsAsync(st, m, plan), m);
        }
        _log.LogInformation("MVP {Cfg} summons {Count} x {Minion}", st.Def.CfgId, plan.Count, plan.Npc);
    }

    /// <summary>Self-destruct at the end of life: a blast minion first hits the players in its blast range.</summary>
    private async Task MinionLifeEndsAsync(State st, MonsterUnit m, MinionPlan plan)
    {
        if (!st.Retired && !m.Dead && m.Hp > 0 && plan.BlastSkill > 0 && _data.Row("SkillConfig", plan.BlastSkill) is { } row)
        {
            var targets = _world.PlayersIn(m.WorldScene)
                .Where(p => p.Role != null && !p.Dead && MonsterService.Distance(m, p) <= plan.BlastRadius + 0.5f)
                .OrderBy(p => MonsterService.Distance(m, p)).Take(plan.BlastTargets).ToArray();
            int hit = await _combat.MonsterSkillHitAsync(m, row, targets, m.Uid);
            _log.LogInformation("MVP {Cfg} minion {Npc} explodes on {Hit} player(s)", st.Def.CfgId, m.Template.NpcId, hit);
        }
        await DespawnMinionAsync(st, m);
    }

    private async Task DespawnMinionAsync(State st, MonsterUnit m)
    {
        if (!st.Retired && !m.Dead) await _monsters.KillAsync(m);
        st.Minions.Remove(m);
        st.Deadlines.RemoveAll(d => d.Actor == m);
    }

    /// <summary>Frenzy: the buff on the boss and its live minions ("its own and all summoned Thief Bugs").</summary>
    private async Task SelfBuffAsync(State st, MonsterUnit boss, BossSkillPlan plan)
    {
        if (st.Retired || boss.Dead || st.Unit != boss) return;
        foreach (var unit in st.Minions.Where(m => !m.Dead).Prepend(boss).ToArray())
            await _buffs.AddSupportAsync(unit, plan.Buff, plan.BuffMs, boss.Uid);
    }

    private async Task MarkAsync(State st, MonsterUnit boss, BossSkillPlan plan)
    {
        var field = plan.Field!;
        var marked = _world.PlayersIn(boss.WorldScene)
            .Where(p => p.Role != null && !p.Dead && !p.SceneLoading && MonsterService.Distance(boss, p) <= field.SelectRadius)
            .OrderBy(p => MonsterService.Distance(boss, p)).Take(field.Targets)
            .Select(p => (Session: p, Role: p.Role!, p.SceneGeneration)).ToArray();
        foreach (var (p, _, _) in marked)
            if (field.MarkBuff > 0) await _buffs.AddAsync(p, field.MarkBuff, field.MarkMs, boss.Uid);
        Schedule(st, TimeSpan.FromMilliseconds(Math.Max(0, field.MarkMs)), async () =>
        {
            if (st.Retired || boss.Dead || st.Unit != boss || _data.Row("SkillConfig", plan.Skill) is not { } row) return;
            foreach (var (p, role, generation) in marked)
            {
                if (!ReferenceEquals(p.Role, role) || p.Dead || p.SceneLoading || p.SceneGeneration != generation
                    || p.WorldScene != boss.WorldScene) continue;
                await PlaceFieldAsync(st, boss, plan, row, p.Position);
            }
        }, boss);
    }

    private async Task PlaceFieldAsync(State st, MonsterUnit boss, BossSkillPlan plan, JsonElement row, Vec3 at)
    {
        var spec = plan.Field!;
        if (_data.Row("NPCConfig", spec.Npc) is not { } npcRow) return;
        long now = MonsterService.NowMs;
        var field = new MvpFieldUnit
        {
            Uid = FieldUidBase | ++_fieldSerial, SceneEntry = boss.SceneEntry, InstanceId = boss.InstanceId, RuntimeMap = boss.RuntimeMap,
            NpcId = spec.Npc, EntityType = (int)GameData.Int(npcRow, "_iNPCType"), Boss = boss, BornMs = now, EndMs = now + spec.LifeMs,
            X = at.X, Y = at.Y, Z = at.Z, Face = boss.Face,
        };
        st.Fields.Add(field);
        await _world.AddUnitAsync(field);
        if (st.Retired || boss.Dead) { await RemoveFieldAsync(st, field); return; }
        Schedule(st, TimeSpan.FromMilliseconds(spec.TickMs), () => TickFieldAsync(st, boss, field, plan), boss);
    }

    /// <summary>Each second the field hits the players standing in it with the field skill (damage + _kBuff2).</summary>
    private async Task TickFieldAsync(State st, MonsterUnit boss, MvpFieldUnit field, BossSkillPlan plan)
    {
        if (field.Gone) return;
        var spec = plan.Field!;
        if (st.Retired || boss.Dead || st.Unit != boss || _data.Row("SkillConfig", plan.Skill) is not { } row)
        {
            await RemoveFieldAsync(st, field);
            return;
        }
        var targets = _world.PlayersIn(field.WorldScene)
            .Where(p => p.Role != null && !p.Dead && MonsterService.Distance(field, p) <= spec.Radius).ToArray();
        if (targets.Length > 0) await _combat.MonsterSkillHitAsync(boss, row, targets, field.Uid);
        if (MonsterService.NowMs + spec.TickMs > field.EndMs) await RemoveFieldAsync(st, field);
        else Schedule(st, TimeSpan.FromMilliseconds(spec.TickMs), () => TickFieldAsync(st, boss, field, plan), boss);
    }

    private async Task RemoveFieldAsync(State st, MvpFieldUnit field)
    {
        if (field.Gone) return;
        field.Gone = true;
        st.Fields.Remove(field);
        await _world.RemoveUnitAsync(field);
    }

    /// <summary>The boss's minions and ground fields end with it (death, reset, retired line).</summary>
    private async Task ClearAddsAsync(State st)
    {
        foreach (var minion in st.Minions.ToList()) await DespawnMinionAsync(st, minion);
        foreach (var field in st.Fields.ToList()) await RemoveFieldAsync(st, field);
    }

    private async Task LeaveCombatAsync(State st, MonsterUnit boss)
    {
        if (st.Retired || boss.Dead || st.Unit != boss) return;
        if (boss.Target != null) { st.UnFightApplied = false; return; } // engaged again before this ran
        st.Pending++;
        try
        {
            st.Deadlines.RemoveAll(d => d.Actor == boss);
            await ClearAddsAsync(st);
            foreach (int buff in UnFight(boss.Template.NpcId).Buffs) await _buffs.AddPermanentAsync(boss, buff, boss.Uid);
        }
        finally { EndPending(st); }
    }

    private async Task EnterCombatAsync(MonsterUnit boss)
    {
        if (boss.Dead) return;
        foreach (int buff in UnFight(boss.Template.NpcId).Buffs) await _buffs.RemoveUnitConfigAsync(boss, buff);
    }

    /// <summary>AIConfig _iUnFightType 1 = out of combat for _iUnFightParam ms, then _iUnFightBUFF (20200001: 5000 ms, 901401).</summary>
    private (long Ms, int[] Buffs) UnFight(int npc)
    {
        if (_data.Row("NPCConfig", npc) is not { } npcRow || _data.Row("AIConfig", GameData.Int(npcRow, "_iAI")) is not { } ai)
            return (5000, []);
        var types = Ints(ai, "_iUnFightType");
        var param = Ints(ai, "_iUnFightParam");
        int index = Array.IndexOf(types, 1);
        return (index >= 0 && index < param.Length ? param[index] : 5000, Ints(ai, "_iUnFightBUFF"));
    }

    /// <summary>NPCConfig _kBornEventBuff [buff, ms (-1 permanent), count, chance/10000]. The MVP rows (900901, 904301,
    /// 904101) carry no _kChangeAttri: they are sent so the client has them; their effects are server-only.</summary>
    private async Task ApplyBornBuffsAsync(MonsterUnit unit)
    {
        unit.EntityExtras = entity => _buffs.PopulateEntity(unit, entity);
        if (_data.Row("NPCConfig", unit.Template.NpcId) is not { } npcRow) return;
        foreach (var (buff, ms, chance) in BornBuffs(npcRow))
        {
            if (unit.Dead || _random.Next(10000) >= chance) continue;
            if (ms < 0) await _buffs.AddPermanentAsync(unit, buff, unit.Uid);
            else await _buffs.AddSupportAsync(unit, buff, ms, unit.Uid);
        }
    }

    internal async Task CastForTest(MonsterUnit boss, int skill)
    {
        if (Owner(boss) is { } st && Plans(st.Def.NpcId).FirstOrDefault(p => p.Skill == skill) is { } plan)
            await CastAsync(st, boss, plan);
    }

    internal async Task EndMinionLivesForTest(MonsterUnit boss)
    {
        if (Owner(boss) is not { } st) return;
        foreach (var minion in st.Minions.ToList())
            if (Plans(st.Def.NpcId).Select(p => p.Minions).FirstOrDefault(p => p?.Npc == minion.Template.NpcId) is { } plan)
                await MinionLifeEndsAsync(st, minion, plan);
    }

    // ---- Frenzy (Delusion 5002201: 215 damage +30 %, 216 damage reduction -30 %) ----

    private bool Frenzied(MonsterUnit m)
    {
        _frenzyNpcs ??= BossSkillIds.Keys.Where(boss => Plans(boss).Any(p => p.Kind == BossSkillKind.SelfBuff))
            .SelectMany(boss => Plans(boss).Where(p => p.Minions != null).Select(p => p.Minions!.Npc).Append(boss)).ToHashSet();
        return _frenzyNpcs.Contains(m.Template.NpcId);
    }

    private long Attribute(MonsterUnit m, long id) =>
        (long)_buffs.UnitAttributes(m).Where(a => a.Id == id).Sum(a => a.Value);

    /// <summary>Outgoing monster damage × (1 + attribute 215). ASPD 114 is not applied: swings are timed by MonsterService.</summary>
    internal long FrenzyDealt(MonsterUnit m, long damage)
    {
        long up = Frenzied(m) ? Attribute(m, 215) : 0;
        return up == 0 ? damage : Math.Max(0, damage * (10000 + up) / 10000);
    }

    internal long FrenzyTaken(MonsterUnit m, long damage)
    {
        long cut = Frenzied(m) ? Attribute(m, 216) : 0;
        return cut == 0 ? damage : Math.Max(0, damage * (10000 - cut) / 10000);
    }

    // ---- data helpers ----

    private static int[] Ints(JsonElement row, string field) =>
        row.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(v => (int)(GameData.Number(v) ?? 0)).ToArray() : [];

    private static IEnumerable<(int Buff, int Ms)> BuffTuples(JsonElement row, string field) =>
        row.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Array && t.GetArrayLength() >= 3)
                .Select(t => ((int)(GameData.Number(t[0]) ?? 0), (int)(GameData.Number(t[2]) ?? 0))).ToArray()
            : [];

    private static IEnumerable<(int Buff, int Ms, int Chance)> BornBuffs(JsonElement npcRow) =>
        npcRow.TryGetProperty("_kBornEventBuff", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Array && t.GetArrayLength() >= 2)
                .Select(t => ((int)(GameData.Number(t[0]) ?? 0), (int)(GameData.Number(t[1]) ?? 0),
                    t.GetArrayLength() >= 4 ? (int)(GameData.Number(t[3]) ?? 10000) : 10000))
                .Where(t => t.Item1 > 0).ToArray()
            : [];

    /// <summary>The n-th _iDescription parameter (the text's @{n}).</summary>
    private static double? DescParam(JsonElement row, int n) =>
        row.TryGetProperty("_iDescription", out var d) && d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > n
            ? GameData.Number(d[n]) : null;
}
