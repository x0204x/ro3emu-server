using System.Text.Json;
using System.Text.RegularExpressions;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>Where a boss skill lands, from SkillConfig _iRelease (0 None, 1 Lock, 2 FixCircle, 3 FixRect, 4 Fan,
/// 5 Arrow, 6 Rect360, 7 DragRect, 8 DragCircle; spec-skills-all §1) and _iRangeType (2 = area).</summary>
public enum BossSkillArea { Target, AroundSelf, AroundTarget, Fan, Rect }

/// <summary>One special skill of a boss, resolved once from its SkillConfig row and show template.</summary>
public sealed class BossSkill
{
    public required int Id { get; init; }
    public required JsonElement Row { get; init; }
    public string Logic { get; init; } = "";
    public int CdMs { get; init; }
    public int ChantMs { get; init; }
    public int ActionMs { get; init; }
    /// <summary>The row's _iPubCD: the boss's other specials wait at least this long after the cast starts.</summary>
    public int PublicCdMs { get; init; }
    /// <summary>Phases in which the skill is in the rotation (a phase variant replaces the one before it).</summary>
    public int FromPhase { get; init; }
    public int ToPhase { get; init; } = int.MaxValue;
    /// <summary>A row with an effectively endless cooldown (9999999 ms): cast once, when its phase begins.</summary>
    public bool Once { get; init; }
    public BossSkillArea Area { get; init; }
    public float Radius { get; init; }
    public float Width { get; init; }
    public float AngleDeg { get; init; }
    /// <summary>Distance to its target within which the boss starts the skill.</summary>
    public float Reach { get; init; }
    public int MaxTargets { get; init; } = 1;
    /// <summary>Hit moments after the cast starts (the show's m_kAtkPoints on the cast timeline).</summary>
    public int[] HitMs { get; init; } = [];
    public bool HighLevel { get; init; }
    public bool Damages { get; init; }
    /// <summary>Damage or a hostile _kBuff2: the hit selects players; a skill with neither only buffs the boss.</summary>
    public bool HitsPlayers { get; init; }
    public (int Buff, int Chance, int Ms)[] SelfBuffs { get; init; } = [];
    public int SummonNpc { get; init; }
    public int SummonCount { get; init; }
    public int SummonLifeMs { get; init; }
    /// <summary>The summoned minion's _kRespawnSkill (Poring Explosion): it blasts the players around it when its life ends.</summary>
    public int SummonBlast { get; init; }
    /// <summary>A ground object (NPCConfig _iNPCType 4) the skill places where it lands, for <see cref="GroundLifeMs"/>;
    /// its _kRespawnSkill (<see cref="GroundBlast"/>) blasts the players around it when it goes.</summary>
    public int GroundNpc { get; init; }
    public int GroundLifeMs { get; init; }
    public int GroundBlast { get; init; }
    /// <summary>A show with a displacement EffectData that moves the caster (a charge or a leap): its effect id and time.</summary>
    public int ChargeEffect { get; init; }
    public int ChargeMs { get; init; }
    /// <summary>A show whose displacement EffectData moves the players hit: a knockback away from the boss
    /// (<see cref="PushDistance"/> m) or, with a negative distance, a pull that stops that far in front of it.</summary>
    public int PushEffect { get; init; }
    public int PushMs { get; init; }
    public float PushDistance { get; init; }
    public int BusyMs => ChantMs + ActionMs;
}

/// <summary>A boss's resolved special skills and phases. Immutable, shared by every unit of the NPC id.</summary>
public sealed class BossSkillBook
{
    public required string Family { get; init; }
    public required BossSkill[] Skills { get; init; }
    /// <summary>1 = no phases; a phase variant or a once-only skill adds phases.</summary>
    public int Phases { get; init; } = 1;
    /// <summary>HP (basis points of max HP) at or below which phase i+1 begins.</summary>
    public int[] PhaseHpBp { get; init; } = [];

    /// <summary>The phase for the boss's current HP (no allocation: runs in the AI tick).</summary>
    public int PhaseAt(long hp, long maxHp)
    {
        if (PhaseHpBp.Length == 0 || maxHp <= 0) return 0;
        long bp = hp * 10000 / maxHp;
        int phase = 0;
        for (int i = 0; i < PhaseHpBp.Length; i++) if (bp <= PhaseHpBp[i]) phase = i + 1;
        return phase;
    }
}

/// <summary>A live boss's scheduler state (MonsterService.BossSkills): cooldowns, phase, the cast in flight, summons.</summary>
public sealed class BossSkillRuntime(BossSkillBook book)
{
    public BossSkillBook Book { get; } = book;
    public long[] ReadyMs { get; } = new long[book.Skills.Length];
    public long[] LastCastMs { get; } = new long[book.Skills.Length];
    /// <summary>Skills whose phase just began: cast first, without waiting for the gap after the previous special.</summary>
    public bool[] Opener { get; } = new bool[book.Skills.Length];
    /// <summary>How many openers wait: without one, a boss inside the gap after its last special skips the scan.</summary>
    public int Openers { get; set; }
    public bool Engaged { get; set; }
    public int Phase { get; set; }
    /// <summary>Bumped by every cast and reset; a scheduled hit of an older serial does nothing.</summary>
    public long CastSerial { get; set; }
    public long ActiveSession { get; set; }
    public int ActiveSkill { get; set; }
    public long ActiveEndMs { get; set; }
    public List<Maps.MonsterUnit> Summons { get; } = new();
    /// <summary>The ground objects its skills placed (<see cref="BossSkill.GroundNpc"/>), removed with the boss.</summary>
    internal List<MvpFieldUnit> Grounds { get; } = [];
}

/// <summary>
/// The special skills of each boss. The original server's AI scripts (NPCConfig _iAISpecial) chose them; the client
/// carries only the rows, so the binding of rows to a boss is by family and is (inferred):
/// <list type="number">
/// <item>NPCConfig _kSkills lists them directly (the airship crew, Wule/Boya, a few story bosses).</item>
/// <item>Field MVPs and every boss running a field-MVP AI script (Boss Challenge 810000xx, the 480/502/702 copies, the
/// Pet Tower bosses "Angeling"/"OrcLordPT"...): the 52xxxxx rows named after the MVP (<see cref="MvpFamilies"/>, the
/// spec's atlas; the 52xxxxx swings are named "PetTowerMonsterAttack", the family was the Pet Tower's).</item>
/// <item>Phantom Realm, Valhalla and Realm of the Gods: the whole 10000-row block of the boss's own swing
/// (<see cref="BossNormalAttacks.AiScriptAttacks"/>: 4310101 → 431xxxx), with the AI script's difficulty
/// ("_easy"/"EZ", "_hard"/"_Peak"/"_emeng") choosing between rows tagged the same way.</item>
/// <item>Rift Crisis (Secretlair1-10): the rows named after the boss in the 1600xxxx block (<see cref="RiftFamilies"/>).</item>
/// <item>Mini bosses: the "XX Skills" row whose script logic is the AI script (5101201 "MiniBossVocal").</item>
/// <item>Story bosses: the rows whose script logic starts with "MainStory_" + the boss (as their swing).</item>
/// </list>
/// A row joins the rotation when the client can play it and the server can resolve it: a cast or action time and a show
/// (LC_Scene_Sync._useInSkill drops a cast with neither), damage, a buff or a minion; not a swing, not a heal, a real
/// cooldown (0 marks a script's sub-step). Phases (inferred): rows named as numbered variants of one skill ("ssj"/"ssj2",
/// "Bullet1/3/5", Valhalla's "shunpi1/shunpi2"; not "Skill1..5", which number different skills) are its versions in
/// successive phases, and a 9999999 ms cooldown row is cast once when the second phase begins; the HP thresholds are
/// BossSkillOptions.PhaseHpPercents.
/// </summary>
public sealed class BossSkillSets(GameData data, BossSkillOptions options)
{
    /// <summary>Field-MVP AI script → its special rows (by skill name; inferred).
    /// Sub-steps (selectors, damage parts, placeholders) are listed where the atlas lists them and drop out in the filter.</summary>
    internal static readonly IReadOnlyDictionary<string, int[]> MvpFamilies = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["AngelingMVP"] = [5201301, 5201401, 5201701, 5202101],             // Ring of Light, Angel Swipe, Realm of Light, Summon Self-Destruct Poring
        ["GoldenBugMVP"] = [5201901, 5202001, 5202201, 5202301],            // Pyroblast, Lava Heatwave, Summon Thief Bug, Frenzy
        ["DevilingMVP"] = [5202401, 5202501, 5202801, 5202901],             // Ring of Darkness, Fallen Swipe, Realm of Darkness, Summon Bombring
        ["orkheroMVP"] = [5203001, 5203101, 5203301, 5203401, 5203501],     // Terrifying Roar, Blade Whirlwind, Spinning Blade Strike, Shatterblade, Rock-Shattering Strike
        ["mayaMVP"] = [5203601, 5203701, 5203801, 5203901],                 // Stone Glyph Slash, Petrify Cleave, Petrifying Mist, Sandstorm
        ["OrcLordMVP"] = [5211201, 5211301, 5211401, 5211501, 5211701],     // Brutal Sweep, Rockfall Barrage, Triple Quake, Gravity Totem, Brute Force Rupture
        ["goblinLeaderMVP"] = [5207701, 5207801, 5207901, 5208601, 5208701], // Intimidating Roar, Spear Thrust, Summon Spear, Boss Spear Throw, Spear Barrage
        ["DrakeMVP"] = [5213101, 5215001, 5215101, 5215201, 5215301],       // Roaming Tempest, Abyssal Cross Impact, Wavebreaker Strike, Explosive Tide, Cannonball Barrage
        ["EddgaMVP"] = [5214201, 5214301, 5214401, 5214501],                // Scorched Earth Brand, Blazing Tiger Roar, Blazing Pursuit, Tiger Leap Quake
        ["MistressMiddleMVP"] = [5208001, 5208101, 5208201, 5208301, 5208401], // Beehive Toss, Swarming Bees, Hornet Assault, Falling Beehive, Hornet Attack
        ["osirisMVP"] = [5207101, 5207201, 5207501],                        // Summon Horong, Bandage Lash, Dark Pulse
        ["piliennMVP"] = [5205101, 5205201, 5205301, 5205401, 5205501],     // Devouring Tongue, Indigestion, Putrid Explosion, Acid Spray, Powerful Drain
        ["BaphometMVP"] = [5209201, 5209301, 5209401, 5209501, 5209601],    // Scythe Sweep, Inferno Explosion, Hellfire, Summon Scythe, Soul Reaper
        ["MoonlightMVP"] = [5212201, 5212301, 5212401, 5212501],            // Dark Corrosion, Sonic Impact, Falling Bell, Bell Resonance
        ["DraculaMVP"] = [5210601, 5210701, 5210801, 5210901, 5211001],     // Gift of the Underworld, Shadow Siphon, Circle of the Dead, Nether Pact Brand, Funeral Beat
        ["PharaohMVP"] = [5209701, 5209801, 5209901, 5210001, 5210101, 5210201], // Summon Coffin, Mummy's Hand, Summon Pyramid, Spinning Pyramid, Coffin Eruption, Scarab Swarm
        ["DoppelgangerMVP"] = [5213401, 5213501, 5213601, 5213701],         // Dark Magnum Break, Charging Blade, Blade Fan, Storm Gust
    };

    /// <summary>Rift Crisis bosses (16000001-30, "Secretlair1".."10"): the 1600xxxx rows named after them ("Cat o'
    /// Nine-Tail Skill", "Orc Hero New Skill", Pharaoh's "Summon Coffin"/"Coffin Eruption"...; inferred by name).</summary>
    internal static readonly IReadOnlyDictionary<string, int[]> RiftFamilies = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Secretlair1"] = [16000201],                                       // Cat o' Nine-Tail
        ["Secretlair2"] = [16003201],                                       // Gryphon
        ["Secretlair3"] = [16003401],                                       // Dark Priest
        ["Secretlair4"] = [16002801],                                       // Rocker
        ["Secretlair5"] = [16003001],                                       // Maya Purple
        ["Secretlair6"] = [16000401, 16000501, 16001801],                   // Orc Hero
        ["Secretlair7"] = [16000701, 16000801, 16001901],                   // Eddga
        ["Secretlair8"] = [16001001, 16001101, 16002001],                   // Baphomet
        ["Secretlair9"] = [16001301, 16001401, 16002101, 16002301],         // Osiris
        ["Secretlair10"] = [16001601, 16001701, 16002201, 16002401, 16002501], // Pharaoh
    };

    /// <summary>Story AI scripts named in pinyin whose rows are named in English (as BossNormalAttacks).</summary>
    private static readonly Dictionary<string, string> StoryAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HuangJinChong"] = "GoldenBug", ["BaFengTe"] = "Baphomet", ["DevilBoss"] = "EvilBoss",
    };

    private enum Difficulty { Normal, Easy, Hard }

    private static readonly Regex EasyTag = new(@"(^|_)(easy|eazy|ez|simple)(?=_|\d|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HardTag = new(@"(^|_)(hard|peak|emeng)(?=_|\d|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AiEasy = new(@"(easy|eazy|ez|simple)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AiHard = new(@"(hard|peak|emeng)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    /// <summary>A numbered variant: "ssj2", "TD1", "shunpi_2", "Bullet5" → base + one-digit tier.</summary>
    private static readonly Regex Numbered = new(@"^(?<base>.*[a-z])_?(?<n>[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const int RealCooldownMs = 3000;   // a shorter one is a sub-step or a swing variant (inferred)
    private const int OnceCooldownMs = 1_000_000;
    private const int SelfDestructBuff = 5002801; // BuffConfig "Summoned Unit Self-Destruct": its time is the life

    private readonly Dictionary<int, BossSkillBook?> _books = new();
    private readonly object _sync = new();
    private Dictionary<string, List<long>>? _byLogicPrefix;

    /// <summary>The boss's resolved skills, or null when its data has none (it keeps only its normal attack).</summary>
    public BossSkillBook? For(int npcId, int normalSkill)
    {
        lock (_sync)
        {
            if (!_books.TryGetValue(npcId, out var book)) _books[npcId] = book = Resolve(npcId, normalSkill);
            return book;
        }
    }

    /// <summary>The candidate rows, the family name and the AI script's difficulty suffix, before filtering.</summary>
    internal (string Family, List<long> Rows, string Variant) Candidates(int npcId)
    {
        var rows = new List<long>();
        if (data.Row("NPCConfig", npcId) is not { } npc) return ("", rows, "");
        if (npc.TryGetProperty("_kSkills", out var listed) && listed.ValueKind == JsonValueKind.Array)
            foreach (var s in listed.EnumerateArray())
                if (GameData.Number(s) is { } id) rows.Add((long)id);
        string family = rows.Count > 0 ? "listed" : "";
        string ai = Text(npc, "_iAISpecial");
        if (ai.Length == 0) return (family, rows, "");
        if (MvpFamilies.TryGetValue(ai, out var mvp)) { rows.AddRange(mvp.Select(i => (long)i)); return ("field MVP " + ai, rows, ""); }
        // Pet Tower bosses run the MVP scripts without "MVP" ("Angeling", "orkhero") or with "PT" ("OrcLordPT").
        string tower = (ai.EndsWith("PT", StringComparison.Ordinal) ? ai[..^2] : ai) + "MVP";
        if (MvpFamilies.TryGetValue(tower, out var towerRows)) { rows.AddRange(towerRows.Select(i => (long)i)); return ("tower " + ai, rows, ""); }
        if (RiftFamilies.TryGetValue(ai, out var rift)) { rows.AddRange(rift.Select(i => (long)i)); return ("Rift Crisis " + ai, rows, ""); }
        if (ai.StartsWith("MiniBoss", StringComparison.OrdinalIgnoreCase))
        {
            foreach (long id in ByLogicPrefix().GetValueOrDefault(ai.ToLowerInvariant()) ?? [])
                if (id % 1000 == 201 && id / 100000 == npcId / 100000) rows.Add(id); // not the Rift copy 16003201
            return ("mini boss " + ai, rows, "");
        }
        if (ai.StartsWith("MainStory_", StringComparison.OrdinalIgnoreCase))
        {
            string boss = ai["MainStory_".Length..];
            string prefix = ("MainStory_" + StoryAliases.GetValueOrDefault(boss, boss)).ToLowerInvariant();
            foreach (var (logic, ids) in ByLogicPrefix())
                if (logic.StartsWith(prefix, StringComparison.Ordinal))
                    rows.AddRange(ids.Where(id => id / 100000 == npcId / 100000));
            rows.Sort();
            return ("story " + ai, rows, "");
        }
        var script = BossNormalAttacks.AiScriptAttacks
            .Where(s => ai.StartsWith(s.Key, StringComparison.OrdinalIgnoreCase) && BossNormalAttacks.IsVariantSuffix(ai[s.Key.Length..]))
            .OrderByDescending(s => s.Key.Length).FirstOrDefault();
        if (script.Value != 0 && !script.Key.EndsWith("_minion", StringComparison.OrdinalIgnoreCase))
        {
            long block = script.Value / 10000;
            foreach (var (key, _) in data.Table("SkillConfig"))
                if (long.TryParse(key, out long id) && id / 10000 == block) rows.Add(id);
            rows.Sort();
            string kind = script.Value >= 10_000_000 ? "Realm of the Gods" : script.Value / 100000 == 59 ? "Valhalla" : "Phantom Realm";
            return ($"{kind} {ai}", rows, ai[script.Key.Length..]);
        }
        return (family, rows, "");
    }

    private BossSkillBook? Resolve(int npcId, int normalSkill)
    {
        var (family, candidates, variant) = Candidates(npcId);
        if (candidates.Count == 0) return null;
        var listed = ListedSkills(npcId);
        var difficulty = AiHard.IsMatch(variant) ? Difficulty.Hard : AiEasy.IsMatch(variant) ? Difficulty.Easy : Difficulty.Normal;
        var rows = new List<(long Id, JsonElement Row, string Logic, Difficulty Tag)>();
        var summonedOwn = SummonedOwnSkills(candidates);
        foreach (long id in candidates.Distinct())
        {
            if (id == normalSkill || summonedOwn.Contains(id) || data.Row("SkillConfig", id) is not { } row || !Castable(row)) continue;
            if (listed.Contains(id) && GameData.Int(row, "_iIsNormalAtk") == 1) continue; // a listed swing (Poring's 5003301)
            if (MvpService.OwnsSkill(npcId, (int)id)) continue; // MvpService casts these for its field MVPs
            string logic = Logic(row);
            var tag = HardTag.IsMatch(logic) ? Difficulty.Hard : EasyTag.IsMatch(logic) ? Difficulty.Easy : Difficulty.Normal;
            if (tag != Difficulty.Normal && tag != difficulty) continue;
            rows.Add((id, row, logic, tag));
        }
        // An easy/hard boss uses its tagged version of a skill instead of the untagged one.
        if (difficulty != Difficulty.Normal)
        {
            var tagged = rows.Where(r => r.Tag == difficulty).Select(r => BaseName(Untag(r.Logic))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            rows.RemoveAll(r => r.Tag == Difficulty.Normal && tagged.Contains(BaseName(r.Logic)));
        }
        // Only placeholder cooldowns (every row under 3 s): the encounter script paced them (inferred).
        bool placeholder = rows.Count > 0 && rows.All(r => GameData.Int(r.Row, "_iCD") < RealCooldownMs);
        rows.RemoveAll(r => GameData.Int(r.Row, "_iCD") is var cd && (cd <= 0 || !placeholder && cd < RealCooldownMs));
        if (rows.Count == 0) return null;

        // Phase variants: 2-4 rows named base + one digit (the bare base counts as tier 0).
        var tiers = new Dictionary<long, (int Tier, int Count)>();
        foreach (var group in rows.GroupBy(r => BaseName(Untag(r.Logic)), StringComparer.OrdinalIgnoreCase))
        {
            // "Skill1".."Skill5" number different skills, not versions of one (MainStory_EvilBossSkill1-4).
            if (group.Key.Length == 0 || group.Key.EndsWith("skill", StringComparison.OrdinalIgnoreCase)) continue;
            var members = group.Select(r => (r.Id, N: Numbered.Match(Untag(r.Logic)) is { Success: true } m ? int.Parse(m.Groups["n"].Value) : 0))
                .GroupBy(m => m.N).Select(g => g.First()).OrderBy(m => m.N).ToArray();
            if (members.Length is < 2 or > 4) continue;
            for (int i = 0; i < members.Length; i++) tiers[members[i].Id] = (i, members.Length);
        }
        int maxPhases = options.PhaseHpPercents.Length + 1;
        bool once = rows.Any(r => GameData.Int(r.Row, "_iCD") >= OnceCooldownMs);
        int phases = Math.Min(maxPhases, Math.Max(once ? 2 : 1, tiers.Count == 0 ? 1 : tiers.Values.Max(t => t.Count)));
        var skills = new List<BossSkill>();
        foreach (var (id, row, logic, _) in rows.OrderBy(r => r.Id))
        {
            int from = 0, to = int.MaxValue;
            if (tiers.TryGetValue(id, out var tier) && phases > 1)
            {
                from = Math.Min(tier.Tier, phases - 1);
                to = tier.Tier == tier.Count - 1 || tier.Tier >= phases - 1 ? int.MaxValue : tier.Tier;
            }
            bool isOnce = GameData.Int(row, "_iCD") >= OnceCooldownMs;
            if (isOnce) from = Math.Min(1, phases - 1);
            skills.Add(Build((int)id, row, logic, placeholder, from, to, isOnce));
        }
        var thresholds = options.PhaseHpPercents.Select(p => Math.Clamp(p, 1, 99) * 100).OrderDescending().Take(phases - 1).ToArray();
        return new BossSkillBook { Family = family, Skills = skills.ToArray(), Phases = phases, PhaseHpBp = thresholds };
    }

    /// <summary>A row the client can play and the generic resolution can carry out.</summary>
    private bool Castable(JsonElement row)
    {
        if (BossNormalAttacks.IsNamedSwing(row) || GameData.Int(row, "_iDmgType") == 3) return false; // swings, heals
        if (GameData.Int(row, "_iActionTime") + GameData.Int(row, "_iChantFix") + GameData.Int(row, "_iChantVar") <= 0) return false;
        if (GameData.Int(row, "_iScriptShow") <= 0) return false;
        bool damage = Numbers(row, "_kDamageParam1").Any(v => v > 0) || Numbers(row, "_kDamageParam2").Any(v => v > 0);
        bool buff = Tuples(row, "_kBuff1").Any() || Tuples(row, "_kBuff2").Any();
        return damage || buff || SummonNpc(row) > 0 || GroundNpc(row) > 0;
    }

    /// <summary>
    /// Field-MVP skills whose row names no NPC (no _kLogicData) and the summoned unit or ground object they make, by the
    /// names of the 50010xx NPCs made for the field MVPs and the skills those NPCs cast on spawn (inferred: the AI scripts
    /// that spawned them are server-only).
    /// </summary>
    internal static readonly IReadOnlyDictionary<int, int> ReconstructedSummons = new Dictionary<int, int>
    {
        [5209701] = 5001035, // Pharaoh's Summon Coffin: "Pharaoh's Coffin", whose _kRespawnSkill is Pharaoh's Coffin Eruption 5210101
        [5209901] = 5001036, // Pharaoh's Summon Pyramid: "Pyramid", whose _kRespawnSkill is Pharaoh's Spinning Pyramid 5210001
        [5213101] = 5001037, // Drake's Roaming Tempest (script MVPDrakeSummonLongJuan, "summon tornado"): "Water Tornado", casting 5213201 Roaming Tempest
        [5207101] = 5001031, // Osiris's Summon Horong: "Osiris Horong"
        [5208601] = 5001032, // Goblin Leader's Boss Spear Throw: "Goblin Leader Spear Throw"
        [5212401] = 5001039, // Moonlight Flower's Falling Bell: "Bell"
        [5210601] = 5001042, // Dracula's Gift of the Underworld (script zhixianmubei, "summon tombstone"): "MVP Dracula Tombstone", _kRespawnSkill Gravestone Blast 5214901
        [5210801] = 5001042, // Dracula's Circle of the Dead (zhixianmubei5, its phase 2 version): the same tombstone
        [5209401] = 5001033, // Baphomet's Hellfire (script MVPHellRing): "Baphomet Magic Circle"
        [5213701] = 5001040, // Doppelganger's Storm Gust: "Wraith Storm Gust Magic Circle"
    };

    private static int Summoned(JsonElement row) => Numbers(row, "_kLogicData").FirstOrDefault() is var npc and > 0 ? (int)npc
        : ReconstructedSummons.GetValueOrDefault((int)new SkillConfig(row).Id);

    private int GroundNpc(JsonElement row) => Summoned(row) is var npc and > 0 && NPCConfig.Get(data, npc) is { NPCType: 4 } ? npc : 0;

    private int SummonNpc(JsonElement row) => Summoned(row) is var npc and > 0
        && data.Row("NPCConfig", (long)npc) is { } npcRow && GameData.Int(npcRow, "_iNPCType") == 1 ? (int)npc : 0;

    private BossSkill Build(int id, JsonElement row, string logic, bool placeholder, int from, int to, bool once)
    {
        int cd = (int)GameData.Int(row, "_iCD");
        if (placeholder) cd = Math.Max(cd, options.PlaceholderCooldownMs);
        int chant = (int)Math.Max(0, GameData.Int(row, "_iChantFix") + GameData.Int(row, "_iChantVar"));
        int action = (int)Math.Max(0, GameData.Int(row, "_iActionTime"));
        var template = data.SkillTemplate(GameData.Int(row, "_iScriptShow"));
        int release = (int)GameData.Int(row, "_iRelease");
        bool area = GameData.Int(row, "_iRangeType") == 2;
        var range = Numbers(row, "_kRange");
        // _kRange in cm; [5], [0,0,0,0] are placeholders (a 5 cm area), the show's m_fAtkRange (m) is next (inferred).
        float rowRadius = range.Length > 0 && range[0] >= 100 ? (float)range[0] / 100 : 0;
        float showRadius = template is { } t && t.TryGetProperty("m_fAtkRange", out var atk) && GameData.Number(atk) is double r and > 0.5 ? (float)r : 0;
        float radius = Math.Min(options.MaxReach * 2, rowRadius > 0 ? rowRadius : showRadius > 0 ? showRadius : options.DefaultRadius);
        float distance = GameData.Int(row, "_iDistanceMax") / 100f;
        var shape = release switch
        {
            4 => BossSkillArea.Fan,
            3 or 5 or 6 or 7 => BossSkillArea.Rect,
            8 => BossSkillArea.AroundTarget,
            1 => area ? BossSkillArea.AroundTarget : BossSkillArea.Target,
            _ => area || release is 0 or 2 && GameData.Int(row, "_iTargetMax") > 1 ? BossSkillArea.AroundSelf : BossSkillArea.Target,
        };
        // Rect: _kRange [length, width] (Stone Glyph Slash [2300,700], Devouring Tongue [1500,600]); a missing width is 3 m.
        float width = shape == BossSkillArea.Rect ? range.Length > 1 && range[1] >= 100 ? (float)range[1] / 100 : 3f : 0;
        float angle = shape == BossSkillArea.Fan ? range.Length > 1 && range[1] > 0 ? (float)range[1] : 120f : 0;
        float reach = shape switch
        {
            BossSkillArea.Target or BossSkillArea.AroundTarget => distance > 0 && distance <= options.MaxReach ? Math.Max(distance, 2f) : options.MaxReach,
            _ => Math.Min(radius, options.MaxReach),
        };
        // A _kBuff2 of BuffConfig _iType 1 (enum_BuffType.Buff) is the boss's own: Frenzy 5202301 / 5002201 "increases its own
        // ... damage" (MvpService treats it alike). A row whose _kBuff2 are all such buffs only buffs the boss, its
        // _kDamageParam1 (Frenzy's 10000) notwithstanding (inferred).
        var buff2 = Tuples(row, "_kBuff2").Where(b => b.Length >= 3 && b[0] > 0).ToArray();
        var beneficial = buff2.Where(b => data.Row("BuffConfig", (long)b[0]) is { } buff && GameData.Int(buff, "_iType") == 1).ToArray();
        var hostile = buff2.Except(beneficial).ToArray();
        bool damages = (hostile.Length > 0 || beneficial.Length == 0)
            && (Numbers(row, "_kDamageParam1").Any(v => v > 0) || Numbers(row, "_kDamageParam2").Any(v => v > 0));
        return new BossSkill
        {
            Id = id, Row = row, Logic = logic, CdMs = cd, ChantMs = chant, ActionMs = action,
            PublicCdMs = (int)Numbers(row, "_iPubCD").FirstOrDefault(),
            FromPhase = from, ToPhase = to, Once = once,
            Area = shape, Radius = radius, Width = width, AngleDeg = angle, Reach = reach,
            MaxTargets = (int)Math.Max(1, GameData.Int(row, "_iTargetMax")),
            HitMs = HitMoments(template, chant, action), HighLevel = GameData.Int(row, "_iSkillEffectLevel") > 0,
            Damages = damages, HitsPlayers = damages || hostile.Length > 0,
            SelfBuffs = Tuples(row, "_kBuff1").Where(b => b.Length >= 3 && b[0] > 0).Concat(beneficial)
                .Select(b => ((int)b[0], (int)b[1], (int)b[2])).ToArray(),
            SummonNpc = SummonNpc(row), SummonCount = SummonCountOf(row), SummonLifeMs = SummonLifeOf(row), SummonBlast = SummonBlastOf(row),
            GroundNpc = GroundNpc(row), GroundLifeMs = GroundLifeOf(row),
            GroundBlast = GroundNpc(row) is var ground and > 0 && NPCConfig.Get(data, ground) is { RespawnSkill: > 0 and var groundBlast }
                && data.Row("SkillConfig", groundBlast) != null ? (int)groundBlast : 0,
            ChargeEffect = Charge(row, out int chargeMs), ChargeMs = chargeMs,
            PushEffect = Push(row, out int pushMs, out float pushDistance), PushMs = pushMs, PushDistance = pushDistance,
        };
    }

    /// <summary>
    /// The show's attack points on the cast timeline (CombatService.ImpactOffset), distinct, at most MaxHitsPerCast. A
    /// show whose points all sit at 0 on a long action is a scripted warning whose damage parts were server objects: one
    /// hit at 70 % of the action (inferred lead time).
    /// </summary>
    private int[] HitMoments(JsonElement? template, int chant, int action)
    {
        var points = template is { } t && t.TryGetProperty("m_kAtkPoints", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(v => GameData.Number(v) ?? 0).Where(v => v >= 0).Distinct().Order().ToArray() : [];
        if (points.Length == 0 || points.All(p => p < 0.05) && action >= 1000) return [chant + action * 7 / 10];
        return points.Select(p => CombatService.ImpactOffset(template, p, chant, action)).Distinct()
            .Take(Math.Max(1, options.MaxHitsPerCast)).ToArray();
    }

    /// <summary>"summons three" / "@{1} Thief Bugs": the description's first parameter when it is a small count.</summary>
    private int SummonCountOf(JsonElement row) => SummonNpc(row) == 0 ? 0
        : DescParam(row, 1) is double n && n is >= 1 and <= 10 && n == Math.Floor(n) ? (int)n : options.SummonCount;

    private HashSet<long> ListedSkills(int npcId)
    {
        var ids = new HashSet<long>();
        if (data.Row("NPCConfig", npcId) is { } npc && npc.TryGetProperty("_kSkills", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var s in list.EnumerateArray())
                if (GameData.Number(s) is { } id) ids.Add((long)id);
        return ids;
    }

    /// <summary>The self-destruct born buff's time (Thief Bug 60 s), else the blast's described delay (5201501 "explode
    /// after @{1}=15 sec"), else BossSkillOptions.SummonLifeMs; as MvpService.MinionOf.</summary>
    private int SummonLifeOf(JsonElement row)
    {
        if (SummonNpc(row) is var npc and > 0 && data.Row("NPCConfig", npc) is { } npcRow
            && npcRow.TryGetProperty("_kBornEventBuff", out var born) && born.ValueKind == JsonValueKind.Array)
            foreach (var b in born.EnumerateArray())
                if (b.ValueKind == JsonValueKind.Array && b.GetArrayLength() >= 2 && GameData.Number(b[0]) == SelfDestructBuff
                    && GameData.Number(b[1]) is double ms and > 0) return (int)ms;
        if (SummonBlastOf(row) is var blast and > 0 && DescParam(data.Row("SkillConfig", blast)!.Value, 1) is double seconds and > 0)
            return (int)(seconds * 1000);
        // A blast without a described delay goes off when its own show ends (the coffin's Coffin Eruption, inferred).
        if (SummonBlastOf(row) is var timed and > 0 && SkillConfig.Get(data, timed) is { ActionTime: > 0 } shown)
            return (int)shown.ActionTime;
        return options.SummonLifeMs;
    }

    /// <summary>A ground object stays for its skill's action (it marks where the skill lands) and at least 3 s, then
    /// its _kRespawnSkill, if any, blasts around it (Dracula's tombstone and Gravestone Blast; inferred).</summary>
    private static int GroundLifeOf(JsonElement row) => Math.Max(3000, (int)new SkillConfig(row).ActionTime);

    /// <summary>The skills a summoned unit or ground object casts itself (its _kRespawnSkill): the coffin's Coffin
    /// Eruption, not Pharaoh's, though the field-MVP family lists it (inferred).</summary>
    private HashSet<long> SummonedOwnSkills(IEnumerable<long> rows)
    {
        var own = new HashSet<long>();
        foreach (long id in rows)
            if (data.Row("SkillConfig", id) is { } row && Summoned(row) is var npc and > 0 && NPCConfig.Get(data, npc) is { } summoned)
            {
                if (summoned.RespawnSkill > 0) own.Add(summoned.RespawnSkill);
                foreach (long skill in summoned.Skills) own.Add(skill);
            }
        return own;
    }

    private int SummonBlastOf(JsonElement row) => SummonNpc(row) is var npc and > 0 && data.Row("NPCConfig", npc) is { } npcRow
        && GameData.Int(npcRow, "_kRespawnSkill") is var blast and > 0 && data.Row("SkillConfig", blast) != null ? (int)blast : 0;

    /// <summary>
    /// A show with a displacement EffectData (CombatService.FindMovementEffect) moves someone. A knockback or pull is
    /// named in its curve (击退/震退, 勾/吸/拉) or in the skill's name/description ("Pulls enemies"); otherwise the
    /// caster moves: a charge or a leap toward its target (inferred).
    /// </summary>
    private int Charge(JsonElement row, out int ms)
    {
        ms = 0;
        var (effect, time, curve, text) = Displacement(row);
        if (effect == 0) return 0;
        if (curve.Contains("击退") || curve.Contains("震退") || curve.Contains('勾') || curve.Contains('吸') || curve.Contains('拉')
            || text.Contains("pull") || text.Contains("knock") || text.Contains("repel")) return 0;
        ms = time > 0 ? time : 300;
        return effect;
    }

    /// <summary>The show's displacement EffectData (id, time, curve) and the skill's lower-case name and description.</summary>
    private (int Effect, int Ms, string Curve, string Text) Displacement(JsonElement row)
    {
        var (effect, time, curve) = CombatService.FindMovementEffect(data, GameData.Int(row, "_iScriptShow"));
        return (effect, time, curve, effect == 0 ? ""
            : (data.TextOf(row, "_iName") + " " + data.TextOf(row, "_iDescription")).ToLowerInvariant());
    }

    // (inferred) How far a boss's knockback moves a player, and where its pull stops: the player skills' Arrow Repel moves
    // its target 3 m, and Phantom Thrust's pull "in front of you" stops 1 m ahead of the caster.
    private const float KnockbackMeters = 3, PullStopMeters = 1;

    /// <summary>A show whose displacement EffectData moves the players hit (the cases <see cref="Charge"/> leaves out): a
    /// knockback when its curve is 击退/震退 or the skill says knock/repel, a pull when its curve is 勾/吸/拉 or it says
    /// "pull" (Powerful Drain: "Pulls enemies inward toward yourself") (inferred).</summary>
    private int Push(JsonElement row, out int ms, out float distance)
    {
        ms = 0;
        distance = 0;
        var (effect, time, curve, text) = Displacement(row);
        if (effect == 0) return 0;
        bool pull = curve.Contains('勾') || curve.Contains('吸') || curve.Contains('拉') || text.Contains("pull");
        bool knock = curve.Contains("击退") || curve.Contains("震退") || text.Contains("knock") || text.Contains("repel");
        if (!pull && !knock) return 0;
        ms = time > 0 ? time : 300;
        distance = pull ? -PullStopMeters : KnockbackMeters;
        return effect;
    }

    private static string Untag(string logic) => HardTag.Replace(EasyTag.Replace(logic, ""), "");

    private static string BaseName(string logic) =>
        Numbered.Match(logic) is { Success: true } m ? m.Groups["base"].Value.TrimEnd('_') : logic.TrimEnd('_');

    private Dictionary<string, List<long>> ByLogicPrefix()
    {
        if (_byLogicPrefix is { } built) return built;
        var byLogic = new Dictionary<string, List<long>>();
        foreach (var (key, row) in data.Table("SkillConfig"))
            if (Logic(row) is { Length: > 0 } logic && long.TryParse(key, out long id))
            {
                if (!byLogic.TryGetValue(logic.ToLowerInvariant(), out var ids)) byLogic[logic.ToLowerInvariant()] = ids = new();
                ids.Add(id);
            }
        foreach (var ids in byLogic.Values) ids.Sort();
        return _byLogicPrefix = byLogic;
    }

    private static string Logic(JsonElement row) => Text(row, "_kScriptLogic");

    private static string Text(JsonElement row, string field) =>
        row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double[] Numbers(JsonElement row, string field) =>
        row.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(v => GameData.Number(v) ?? 0).ToArray() : [];

    private static IEnumerable<double[]> Tuples(JsonElement row, string field) =>
        row.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Array)
                .Select(t => t.EnumerateArray().Select(v => GameData.Number(v) ?? 0).ToArray()) : [];

    private static double? DescParam(JsonElement row, int n) =>
        row.TryGetProperty("_iDescription", out var d) && d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > n
            ? GameData.Number(d[n]) : null;
}
