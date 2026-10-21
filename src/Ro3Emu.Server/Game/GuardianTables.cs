using System.Runtime.CompilerServices;
using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// BuffConfig by (_iBuffID, _iLevel) and SkillConfig by (_iSkillID, _iLevel) and (_kScriptLogic, _iLevel), in table
/// order, built once per loaded <see cref="GameData"/>: guardian reactions resolve these rows on every hit and pulse.
/// </summary>
internal sealed class GuardianTables
{
    private static readonly ConditionalWeakTable<GameData, GuardianTables> Cache = [];
    private readonly Dictionary<(long Group, long Level), JsonElement[]> _buffs;
    private readonly Dictionary<(long Group, long Level), JsonElement[]> _skills;
    private readonly Dictionary<(string Script, long Level), JsonElement[]> _scripts;

    private GuardianTables(GameData data)
    {
        _buffs = Index(data.Table("BuffConfig").Values, r => (GameData.Int(r, "_iBuffID"), GameData.Int(r, "_iLevel")));
        var skills = data.Table("SkillConfig").Values;
        _skills = Index(skills, r => (GameData.Int(r, "_iSkillID"), GameData.Int(r, "_iLevel")));
        _scripts = Index(skills, r => (GuardianPetRules.Script(r), GameData.Int(r, "_iLevel")));
    }

    internal static GuardianTables Of(GameData data) => Cache.GetValue(data, d => new GuardianTables(d));

    private static Dictionary<TKey, JsonElement[]> Index<TKey>(IEnumerable<JsonElement> rows, Func<JsonElement, TKey> key)
        where TKey : notnull => rows.GroupBy(key).ToDictionary(g => g.Key, g => g.ToArray());

    internal IReadOnlyList<JsonElement> Buffs(long group, long level) => _buffs.GetValueOrDefault((group, level)) ?? [];

    internal IReadOnlyList<JsonElement> Skills(long group, long level) => _skills.GetValueOrDefault((group, level)) ?? [];

    internal IReadOnlyList<JsonElement> Scripted(string script, long level) =>
        _scripts.GetValueOrDefault((script, level)) ?? [];
}
