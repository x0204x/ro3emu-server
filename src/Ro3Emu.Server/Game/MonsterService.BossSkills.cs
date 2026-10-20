using System.Globalization;
using System.Text;
using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The boss special-skill scheduler (BossSkillSets resolves the skills). It runs inside the fight step of the monster
/// AI, so it costs a field read for every monster without skills and, for a boss, one pass over its few precomputed
/// skills without allocating. The choice is (inferred): the original AI scripts are server-only.
/// <list type="bullet">
/// <item>The first special comes BossSkillOptions.FirstCastDelayMs after the boss engages; afterwards a skill is ready
/// again after its _iCD, and the boss waits for its cast and action to end, then GlobalCooldownMs (or the row's longer
/// _iPubCD) before the next one. Among the ready skills whose reach covers the target it takes the one cast longest ago,
/// so the first round goes in data order and later rounds rotate.</item>
/// <item>Phases: when HP falls to a threshold the phase's skills (a phase variant, a once-only skill) are cast first,
/// without waiting for the gap. Phase and cooldowns reset when the boss leaves combat.</item>
/// <item>While the cast runs the boss stands and does not swing; death, reset or removal interrupts it (31103).</item>
/// </list>
/// CombatService carries the cast out (cast notice, hits at the show's attack points, buffs, summons, charges).
/// </summary>
public sealed partial class MonsterService
{
    private BossSkillSets? _bossSkillSets;
    internal BossSkillSets BossSkillSets => _bossSkillSets ??= new BossSkillSets(data, bossOptions);
    internal BossSkillOptions BossOptions => bossOptions;

    /// <summary>Set by CombatService: carry out a chosen special (boss, its player or tower-pet target, skill, cast serial).</summary>
    public Func<MonsterUnit, GameSession?, TowerPetUnit?, BossSkill, long, Task>? CastBossSkill { get; set; }

    /// <summary>Set by CombatService: a boss's specials end (death, reset, removal); true = interrupt the cast in flight.</summary>
    public Func<MonsterUnit, BossSkillRuntime, bool, Task>? BossSkillsEnded { get; set; }

    /// <summary>Only bosses (NPCConfig subtype 3 mini, 4 MVP, 6 boss) get special skills, and only from their data.</summary>
    private BossSkillBook? BossBook(int npcId, JsonElement npc, int normalSkill) =>
        bossOptions.Enabled && GameData.Int(npc, "_iNPCSubType") is 3 or 4 or 6 ? BossSkillSets.For(npcId, normalSkill) : null;

    /// <summary>MvpService's tick: its own plans were not due, so the boss may cast one of its generic specials.</summary>
    internal bool TryBossSkill(MonsterUnit m, long now) =>
        m.Target is { } target && !m.Dead && m.BusyUntilMs <= now && TryBossSkill(m, target, null, Distance(m, target), now);

    private bool TryBossSkill(MonsterUnit m, GameSession? player, TowerPetUnit? pet, float dist, long now)
    {
        if (CastBossSkill == null || m.Template.Skills is not { } book) return false;
        var rt = m.BossSkills;
        if (rt == null) m.BossSkills = rt = new BossSkillRuntime(book);
        if (!rt.Engaged)
        {
            rt.Engaged = true;
            long first = now + bossOptions.FirstCastDelayMs;
            for (int i = 0; i < book.Skills.Length; i++) rt.ReadyMs[i] = book.Skills[i].Once && book.Skills[i].FromPhase > 0 ? long.MaxValue : first;
        }
        int phase = book.PhaseAt(m.Hp, m.Template.MaxHp);
        while (rt.Phase < phase)
        {
            rt.Phase++;
            for (int i = 0; i < book.Skills.Length; i++)
            {
                var skill = book.Skills[i];
                if (skill.FromPhase == rt.Phase && !rt.Opener[i])
                {
                    rt.ReadyMs[i] = now;
                    rt.Opener[i] = true;
                    rt.Openers++;
                }
                else if (rt.Opener[i] && skill.ToPhase < rt.Phase)
                {
                    rt.Opener[i] = false; // a phase skipped by one big hit: its version is out of the rotation
                    rt.Openers--;
                }
            }
        }
        if (rt.Openers == 0 && now < m.NextSpecialMs) return false;
        int index = PickBossSkill(m, rt, dist, now);
        if (index < 0) return false;
        StartBossSkill(m, player, pet, rt, index, now);
        return true;
    }

    /// <summary>The ready skill to cast now, or -1: phase openers first, then the one cast longest ago.</summary>
    private static int PickBossSkill(MonsterUnit m, BossSkillRuntime rt, float dist, long now)
    {
        var skills = rt.Book.Skills;
        bool gapOpen = now >= m.NextSpecialMs;
        int best = -1;
        bool bestOpener = false;
        long bestLast = long.MaxValue;
        for (int i = 0; i < skills.Length; i++)
        {
            var skill = skills[i];
            if (rt.Phase < skill.FromPhase || rt.Phase > skill.ToPhase || rt.ReadyMs[i] > now) continue;
            bool opener = rt.Opener[i];
            if (!opener && !gapOpen || dist > skill.Reach + 0.5f) continue;
            if (best >= 0 && (bestOpener && !opener || bestOpener == opener && rt.LastCastMs[i] >= bestLast)) continue;
            best = i;
            bestOpener = opener;
            bestLast = rt.LastCastMs[i];
        }
        return best;
    }

    private void StartBossSkill(MonsterUnit m, GameSession? player, TowerPetUnit? pet, BossSkillRuntime rt, int index, long now)
    {
        var skill = rt.Book.Skills[index];
        rt.ReadyMs[index] = skill.Once ? long.MaxValue : now + Math.Max(1000, skill.CdMs);
        rt.LastCastMs[index] = now;
        if (rt.Opener[index])
        {
            rt.Opener[index] = false;
            rt.Openers--;
        }
        long busy = skill.BusyMs + skill.ChargeMs;
        m.BusyUntilMs = now + busy;
        m.NextSpecialMs = now + Math.Max(busy + bossOptions.GlobalCooldownMs, skill.PublicCdMs);
        m.NextSwingMs = Math.Max(m.NextSwingMs, m.BusyUntilMs);
        if (player != null) m.Face = Angle(m.X, m.Z, player.Position.X, player.Position.Z);
        else if (pet != null) m.Face = Angle(m.X, m.Z, pet.X, pet.Z);
        long serial = ++rt.CastSerial;
        rt.ActiveSkill = skill.Id;
        rt.ActiveSession = 0;
        rt.ActiveEndMs = m.BusyUntilMs;
        if (m.Moving)
        {
            m.Moving = false;
            SendStop(m, now);
        }
        var cast = CastBossSkill!;
        loop.TrackRuntimeDetached("boss skill", () => cast(m, player, pet, skill, serial));
    }

    /// <summary>Leaving combat or death: cooldowns and phase start over; the cast in flight and the summons end.</summary>
    private void EndBossSkills(MonsterUnit m, bool interrupt)
    {
        if (EndBossSkillsTask(m, interrupt) is { } end) loop.TrackRuntimeDetached("boss skills end", () => end);
    }

    /// <summary>The ending work, or null when there is none. A boss leaving the world awaits it: its summons must be gone
    /// before a dungeon room releases its scene (InstanceService refuses a scene that still holds units).</summary>
    private Task? EndBossSkillsTask(MonsterUnit m, bool interrupt)
    {
        m.BusyUntilMs = 0;
        if (m.BossSkills is not { } rt) return null;
        m.BossSkills = null;
        m.NextSpecialMs = 0;
        rt.CastSerial++; // the cast's scheduled hits see a newer serial and do nothing
        bool inFlight = interrupt && rt.ActiveSession != 0 && rt.ActiveEndMs > NowMs;
        return BossSkillsEnded is { } ended && (inFlight || rt.Summons.Count > 0) ? ended(m, rt, inFlight) : null;
    }

    // ---- GM (!boss) ----

    /// <summary>"!boss spawn|skills|phase|cast|kill": spawn a boss beside the GM, list a boss's skill schedule, force
    /// its phase or a cast (test aids).</summary>
    public async Task<string> BossGmAsync(GameSession s, string[] args)
    {
        string op = args.Length > 0 ? args[0].ToLowerInvariant() : "skills";
        switch (op)
        {
            case "spawn":
                {
                    int npc = int.Parse(args[1], CultureInfo.InvariantCulture);
                    if (Template(npc) is not { } template) return $"NPC {npc} is not a monster.";
                    double angle = s.Face * Math.PI / 180;
                    var at = new MapPoint(s.Position.X + (float)Math.Sin(angle) * 4, s.Position.Y, s.Position.Z + (float)Math.Cos(angle) * 4, s.Face + 180);
                    if (await SpawnSingleAsync(s.RuntimeMap, npc, at) is not { } boss) return $"Could not spawn {npc} here.";
                    Engage(boss, s);
                    return $"Spawned {NpcName(npc)} ({npc}, uid {boss.Uid}) with {template.Skills?.Skills.Length ?? 0} special skill(s); it attacks you.";
                }
            case "skills":
                {
                    if (args.Length > 1)
                    {
                        int npc = int.Parse(args[1], CultureInfo.InvariantCulture);
                        return Template(npc) is { } t ? Describe(npc, t.Skills, null, NowMs) : $"NPC {npc} is not a monster.";
                    }
                    return NearestBoss(s) is { } boss ? Describe(boss.Template.NpcId, boss.Template.Skills, boss, NowMs) : "No boss with special skills near you.";
                }
            case "phase":
                {
                    if (NearestBoss(s) is not { Template.Skills: { } book } boss) return "No boss with special skills near you.";
                    int phase = int.Parse(args[1], CultureInfo.InvariantCulture);
                    if (phase < 1 || phase > book.PhaseHpBp.Length) return $"{NpcName(boss.Template.NpcId)} has phases 0..{book.Phases - 1}.";
                    boss.Hp = Math.Max(1, boss.Template.MaxHp * book.PhaseHpBp[phase - 1] / 10000 - 1);
                    SendHealth(boss);
                    if (boss.Target == null) Engage(boss, s);
                    return $"{NpcName(boss.Template.NpcId)} HP set to {boss.Hp}/{boss.Template.MaxHp}: phase {phase} begins.";
                }
            case "cast":
                {
                    if (NearestBoss(s) is not { Template.Skills: { } book } boss) return "No boss with special skills near you.";
                    int id = int.Parse(args[1], CultureInfo.InvariantCulture);
                    int index = Array.FindIndex(book.Skills, k => k.Id == id);
                    if (index < 0) return $"Skill {id} is not in {NpcName(boss.Template.NpcId)}'s set.";
                    if (boss.Target != s) Engage(boss, s);
                    var rt = boss.BossSkills ??= new BossSkillRuntime(book);
                    rt.Engaged = true;
                    StartBossSkill(boss, s, null, rt, index, NowMs);
                    return $"{NpcName(boss.Template.NpcId)} casts {data.TextOf(book.Skills[index].Row, "_iName")} ({id}).";
                }
            case "kill":
                {
                    if (NearestBoss(s, anyMonster: true) is not { } boss) return "No monster near you.";
                    await KillAsync(boss);
                    return $"{NpcName(boss.Template.NpcId)} removed (no rewards).";
                }
            default: throw new ArgumentException("op");
        }
    }

    private MonsterUnit? NearestBoss(GameSession s, bool anyMonster = false)
    {
        MonsterUnit? best = null;
        float bestDistance = 40f;
        foreach (var unit in world.Map(s.WorldScene).Units)
            if (unit is MonsterUnit m && !m.Dead && (anyMonster || m.Template.Skills != null) && Distance(m, s) is var d && d < bestDistance)
            {
                best = m;
                bestDistance = d;
            }
        return best;
    }

    private string NpcName(int npc) => data.Row("NPCConfig", npc) is { } row ? data.TextOf(row, "_kName") : npc.ToString(CultureInfo.InvariantCulture);

    private string Describe(int npc, BossSkillBook? book, MonsterUnit? live, long now)
    {
        if (book == null) return $"{NpcName(npc)} ({npc}) has no special skills in its data: normal attack only.";
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{NpcName(npc)} ({npc}), {book.Family}: {book.Skills.Length} skill(s)");
        if (book.Phases > 1)
            text.Append(CultureInfo.InvariantCulture, $", phases at HP {string.Join("/", book.PhaseHpBp.Select(p => p / 100 + "%"))}");
        if (live?.BossSkills is { } rt) text.Append(CultureInfo.InvariantCulture, $", now phase {rt.Phase}");
        for (int i = 0; i < book.Skills.Length; i++)
        {
            var k = book.Skills[i];
            text.Append('\n').Append(CultureInfo.InvariantCulture,
                $"{k.Id} {data.TextOf(k.Row, "_iName")} [{k.Logic}] cd {k.CdMs / 1000.0:0.#}s cast {k.BusyMs / 1000.0:0.#}s {k.Area} {k.Radius:0.#}m");
            if (k.FromPhase > 0 || k.ToPhase != int.MaxValue)
                text.Append(CultureInfo.InvariantCulture, $" phase {k.FromPhase}{(k.ToPhase == int.MaxValue ? "+" : k.ToPhase > k.FromPhase ? "-" + k.ToPhase : "")}");
            if (k.Once) text.Append(" once");
            if (k.SummonNpc > 0) text.Append(CultureInfo.InvariantCulture, $" summons {k.SummonCount}x{k.SummonNpc}");
            if (k.ChargeEffect > 0) text.Append(" charge");
            if (live?.BossSkills is { } state)
                text.Append(state.ReadyMs[i] == long.MaxValue ? " (spent)" : state.ReadyMs[i] > now
                    ? string.Create(CultureInfo.InvariantCulture, $" (ready in {(state.ReadyMs[i] - now) / 1000.0:0.#}s)") : " (ready)");
        }
        return text.ToString();
    }
}
