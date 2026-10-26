using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Tests;

/// <summary>The player skill timelines exported from Config/Skill and Lua_Config_EffectData.</summary>
public sealed class SkillAnimationDataTests
{
    private static readonly GameData Data = new(
        Options.Create(new ServerOptions { DataDir = Path.Combine(ClientData.RepoRoot, "data") }),
        NullLogger<GameData>.Instance);

    [ClientDataFact]
    public void PlayerActiveAndNormalAttacksHaveAuthoredCasterAnimations()
    {
        var treeGroups = Data.Table("SkillTreeConfig").Values
            .SelectMany(SkillTreeGroups).ToHashSet();
        var rows = SkillConfig.All(Data)
            .Where(row => row.BasicType == 1 && treeGroups.Contains(row.SkillId))
            .Concat(JobAttackConfig.All(Data).Select(row => SkillConfig.Get(Data, row.SkillId))
                .OfType<SkillConfig>())
            .GroupBy(row => (row.SkillId, row.ScriptShow))
            .ToArray();
        var effects = Data.SkillTemplateTable("Lua_Config_EffectData");
        var failures = new List<string>();

        Assert.NotEmpty(rows);
        foreach (var skill in rows)
        {
            var (group, show) = skill.Key;
            var template = Data.SkillTemplate(show);
            if (show <= 0 || template == null)
            {
                failures.Add($"skill {group}, show {show}: missing display template");
                continue;
            }

            var references = EffectReferences(template.Value).Distinct().ToArray();
            foreach (long effect in references)
                if (!effects.ContainsKey(effect.ToString()))
                    failures.Add($"skill {group}, show {show}: missing EffectData {effect}");

            bool damages = skill.Any(row => row.DamageParam1.Any(value => value > 0)
                || row.DamageParam2.Any(value => value > 0));
            if (damages && !references.Any(effect => effects.TryGetValue(effect.ToString(), out var row)
                    && HasCasterAnimation(row)))
                failures.Add($"skill {group}, show {show}: damaging skill has no authored caster animation");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static IEnumerable<long> SkillTreeGroups(JsonElement row) => row.EnumerateObject()
        .Where(field => field.Name.StartsWith("_iX", StringComparison.Ordinal)
            && field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt64(out _))
        .Select(field => field.Value.GetInt64()).Where(id => id > 0);

    private static IEnumerable<long> EffectReferences(JsonElement template)
    {
        if (!template.TryGetProperty("m_kShows", out var shows) || shows.ValueKind != JsonValueKind.Object) yield break;
        foreach (var point in shows.EnumerateObject())
            if (point.Value.ValueKind == JsonValueKind.Array)
                foreach (var reference in point.Value.EnumerateArray())
                    if (reference.ValueKind == JsonValueKind.Array && reference.GetArrayLength() >= 1
                        && reference[0].TryGetInt64(out long id) && id > 0)
                        yield return id;
    }

    private static bool HasCasterAnimation(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Any(HasCasterAnimation);
        if (value.ValueKind != JsonValueKind.Object) return false;
        foreach (var field in value.EnumerateObject())
        {
            if (field.Name is "m_kAni" or "m_kAnimName"
                && field.Value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(field.Value.GetString())) return true;
            if (field.Name == "m_bUseWeaponConfigAnim" && field.Value.ValueKind == JsonValueKind.True) return true;
            if (HasCasterAnimation(field.Value)) return true;
        }
        return false;
    }
}
