using System.Text.Json;
using System.Text.RegularExpressions;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The normal attack of a boss whose NPCConfig row lists none: its server AI script (_iAISpecial, not in the client)
/// chose the skills, so the client data only names them. Each family below binds a boss to its own SkillConfig
/// normal-attack row; every binding is by name and therefore inferred.
/// <list type="number">
/// <item>Field MVPs and Rift Crisis bosses: by NPC id (the rows named after them, "Orc Hero Normal Attack-Earth"...).</item>
/// <item>Mini bosses 51000xx: the "XX Skills" row whose script logic is the boss's own AI script (5101201
/// "MiniBossVocal") ends in 201; the x101 row of its block is the paired "XX Normal Attack" (5101101).</item>
/// <item>Story bosses (AI "MainStory_X"): the 53xxxxx row whose script logic is "MainStory_X" + NormalAttack /
/// NormalAttack1 / Attack. Three AI scripts use another name for the same boss (<see cref="StoryAliases"/>).</item>
/// <item>Phantom Realm, Valhalla and Realm of the Gods: the AI scripts and the skill logics are not named alike
/// ("RaidFive_Boss00" swings "GoldenBug_attacking"), so <see cref="AiScriptAttacks"/> names one attack per AI script;
/// the difficulty and tier variants of a script ("_easy", "EZ", "_Peak", "Valhalla_1".."_6") are found in the data.</item>
/// <item>Any other boss running the AI script of a boss bound by id (Boss Challenge 810000xx runs "AngelingMVP")
/// shares that boss's attack.</item>
/// </list>
/// </summary>
public sealed class BossNormalAttacks(GameData data)
{
    /// <summary>
    /// Field MVPs have no _kSkills (their server AI script picks skills); their normal attacks are the
    /// SkillConfig 52xxxxx rows named after them ("Orc Hero Normal Attack-Earth"...).
    /// </summary>
    private static readonly Dictionary<int, long> ById = new Dictionary<int, long>
    {
        [5020001] = 5204001, [5020002] = 5204101, [5020003] = 5204201, [5020004] = 5204301, [5020005] = 5204401,
        [5020006] = 5204501, [5020007] = 5204601, [5020008] = 5215401, [5020009] = 5204901, [5020010] = 5204701,
        [5020011] = 5207001, [5020012] = 5204801, [5020013] = 5209101, [5020014] = 5212101, [5020015] = 5210501,
        [5020016] = 5210301, [5020017] = 5213301,
    }.Concat(RiftBossNormalAttacks()).ToDictionary();

    /// <summary>
    /// Rift Crisis bosses (NPCConfig 16000001-16000030) have no _kSkills either. Stars 1-5 (the next four ids are the same
    /// boss) use SkillConfig's own rows named after them: 16000101 "Cat o' Nine-Tail Normal Attack", 16002701 Rocker,
    /// 16002901 Maya Purple, 16003101 Gryphon, 16003301 Dark Priest. The 6-star MVPs use their MVP attacks above.
    /// </summary>
    private static IEnumerable<KeyValuePair<int, long>> RiftBossNormalAttacks()
    {
        (int First, long Skill)[] fives = [(16000001, 16000101), (16000006, 16003101), (16000011, 16003301), (16000016, 16002701), (16000021, 16002901)];
        foreach (var (first, skill) in fives)
            for (int star = 0; star < 5; star++) yield return new(first + star, skill);
        (int Boss, long Skill)[] mvps = [(16000026, 5204301), (16000027, 5204901), (16000028, 5209101), (16000029, 5207001), (16000030, 5210301)];
        foreach (var (boss, skill) in mvps) yield return new(boss, skill);
    }

    /// <summary>
    /// One normal attack per boss AI script where the names do not match. Phantom Realm (43xxxxx): the boss's
    /// skill block (43N....) holds one row named as its swing. Valhalla (59000xx): block 59k0101, except the first
    /// boss (5900101). Realm of the Gods (147xxxxx): the first of the boss's "Normal Attack" rows (Nessa's
    /// 14740000 "Normal Attack Shell" only picks one of 14740100/200/300, so its first). Exceptions: Drake's minion
    /// has its own row (4320601 "Drake Minion Normal Attack"); Valhalla Drake's swing 5970101 is named
    /// "Valhala_07_skill" (its 1.6 s action and cooldown are the Drake swing's, 4320001); "RaidFive_Boss07_Jzt1" is the
    /// pyramid, not a variant of Pharaoh, and gets none.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, long> AiScriptAttacks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
    {
        ["RaidFive_Boss00"] = 4301101, // Golden Thief Bug, GoldenBug_attacking
        ["RaidFive_Boss01"] = 4310101, // Orc Hero, FiveBoss_01_atk
        ["RaidFive_Boss02"] = 4320001, // Drake, FiveBoss_02_atk
        ["RaidFive_Boss02_minion"] = 4320601,
        ["RaidFive_Boss03"] = 4331101, // Mistress, Mistress_Attacking
        ["RaidFive_Boss04"] = 4340001, // Maya, FiveBoss_04_atk
        ["raidFiveBoss06"] = 4361101, // Osiris, Osiris_atk
        ["RaidFive_Boss07"] = 4370001, // Pharaoh, FiveBoss_07_PG
        ["RaidFive_Boss08"] = 4380001, // Moonlight Flower
        ["RaidFive_Boss09"] = 4391101, // Baphomet, Baphomet_Attack
        ["MistressMiddleValhalla"] = 5900101, // Valhala_Mistress_Attacking
        ["mayaValhalla"] = 5920101, // Valhala_02_atk
        ["osirisValhalla"] = 5930101, // Valhala_03_atk
        ["MoonlightValhalla"] = 5940101, // Valhala_04_atk
        ["PharaohValhalla"] = 5950101, // Valhala_FiveBoss_07_PG
        ["BaphometValhalla"] = 5960101, // Valhala_Baphomet_Attack
        ["DrakeValhalla"] = 5970101, // Valhala_07_skill
        ["Raid_S01_Boss01"] = 14721001, // Este, EsteAttack1
        ["Raid_S01_Boss02"] = 14740100, // Nessa, Raid_S01_Boss02_Attack01
        ["Model_AI_Test_Boss3"] = 14761000, // s01_boss03_attack01
        ["Raid_S01_Boss04"] = 14781101, // s1_4_attack1
    };

    /// <summary>Story AI scripts named in pinyin whose swing is named in English.</summary>
    private static readonly Dictionary<string, string> StoryAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HuangJinChong"] = "GoldenBug", ["BaFengTe"] = "Baphomet", ["DevilBoss"] = "EvilBoss",
    };

    /// <summary>A difficulty or tier variant of an AI script: "_easy", "EZ", "Eazy", "_Test_Easy", "_Peak", "_emeng", "_3".</summary>
    private static readonly Regex Variant = new(@"^(_?(easy|eazy|ez|test|peak|simple|hard|emeng|\d+))*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A script logic named as a swing: "...NormalAttack1", "..._Attacking", "..._atk", "..._PG".</summary>
    private static readonly Regex SwingName = new(@"(NormalAttack\d*|Attack(ing)?\d*|_atk\d*|_PG)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed record Index(Dictionary<string, List<long>> ByLogic, Dictionary<string, long> ByMvpAi);
    private Index? _index;
    private readonly object _sync = new();

    /// <summary>The boss's own normal-attack SkillConfig id, or 0 when no family binds one.</summary>
    public long For(int npcId)
    {
        if (data.Row("NPCConfig", npcId) is not { } npc) return 0;
        long skill = Resolve(npcId, npc);
        return skill != 0 && data.Row("SkillConfig", skill) != null ? skill : 0;
    }

    /// <summary>A SkillConfig row whose script logic is named as a normal attack (inferred from the name).</summary>
    public static bool IsNamedSwing(JsonElement row) => SwingName.IsMatch(Logic(row));

    /// <summary>The rest of an AI script after a known stem is a difficulty or tier variant ("_easy", "EZ", "_3").</summary>
    public static bool IsVariantSuffix(string suffix) => Variant.IsMatch(suffix);

    private long Resolve(int npcId, JsonElement npc)
    {
        if (ById.TryGetValue(npcId, out long byId)) return byId;
        string ai = Text(npc, "_iAISpecial");
        if (ai.Length == 0) return 0;
        var index = Build();
        // Mini bosses: "XX Skills" (logic = the AI script) at x201, its "XX Normal Attack" at x101.
        foreach (long special in index.ByLogic.GetValueOrDefault(ai.ToLowerInvariant()) ?? [])
            if (special % 1000 == 201 && data.Row("SkillConfig", special - 100) is { } pair && IsNamedSwing(pair)) return special - 100;
        if (ai.StartsWith("MainStory_", StringComparison.OrdinalIgnoreCase))
        {
            string boss = ai["MainStory_".Length..];
            boss = StoryAliases.GetValueOrDefault(boss, boss);
            long story = new[] { "NormalAttack", "NormalAttack1", "Attack" }
                .SelectMany(suffix => index.ByLogic.GetValueOrDefault(("MainStory_" + boss + suffix).ToLowerInvariant()) ?? [])
                .Where(id => id / 100000 == npcId / 100000).DefaultIfEmpty().Min();
            if (story != 0) return story;
        }
        var script = AiScriptAttacks.Where(s => ai.StartsWith(s.Key, StringComparison.OrdinalIgnoreCase) && Variant.IsMatch(ai[s.Key.Length..]))
            .OrderByDescending(s => s.Key.Length).FirstOrDefault();
        if (script.Value != 0) return script.Value;
        return index.ByMvpAi.GetValueOrDefault(ai);
    }

    private Index Build()
    {
        if (_index is { } built) return built;
        lock (_sync)
        {
            if (_index is { } again) return again;
            var byLogic = new Dictionary<string, List<long>>();
            foreach (var (key, row) in data.Table("SkillConfig"))
                if (Logic(row) is { Length: > 0 } logic && long.TryParse(key, out long id))
                {
                    if (!byLogic.TryGetValue(logic.ToLowerInvariant(), out var ids)) byLogic[logic.ToLowerInvariant()] = ids = new();
                    ids.Add(id);
                }
            foreach (var ids in byLogic.Values) ids.Sort();
            // An AI script run by bosses bound to different attacks is ambiguous and shares none.
            var byMvpAi = ById.Select(b => (Ai: data.Row("NPCConfig", b.Key) is { } row ? Text(row, "_iAISpecial") : "", Skill: b.Value))
                .Where(b => b.Ai.Length > 0).GroupBy(b => b.Ai, StringComparer.Ordinal)
                .Where(g => g.Select(b => b.Skill).Distinct().Count() == 1)
                .ToDictionary(g => g.Key, g => g.First().Skill, StringComparer.Ordinal);
            return _index = new Index(byLogic, byMvpAi);
        }
    }

    private static string Logic(JsonElement row) => Text(row, "_kScriptLogic");

    private static string Text(JsonElement row, string field) =>
        row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
