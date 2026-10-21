using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

internal static partial class GuardianPetRules
{
    internal readonly record struct Pulse(double Point, double? Coefficient = null, int? Element = null,
        double? HealCoefficient = null, int? DelayMs = null, bool Rectangle = false);

    /// <summary>Named scripts only. Deleter/Garm descriptions specify continuous hits and their shows contain
    /// ordered distinct impact points. Miguel/Zherlthsh coefficient vectors sum to the advertised total.
    /// Bongun explicitly separates Neutral and Poison components. (inferred) Bind each coefficient to the next
    /// ordered visual point; unlike these families, duplicate points in ordinary single-hit shows stay cosmetic.</summary>
    internal static Pulse[] Pulses(GameData data, Skill skill)
    {
        var template = data.SkillTemplate(GameData.Int(skill.Effect, "_iScriptShow"));
        var points = template is { } row ? Numbers(row, "m_kAtkPoints").Distinct().Order().ToArray() : [];
        double first = points.FirstOrDefault(0.4); // (inferred) a show without attack points lands after 0.4 s
        var coefficients = Numbers(skill.Effect, "_kDamageParam1");
        var description = Numbers(skill.Active, "_kDescData");
        var literals = DescriptionLiterals(skill.Active);
        return Script(skill.Effect) switch
        {
            "Deleter_Huzhu" or "GarmBaby_Huzhu" or "PetrifyingGaze" or "BlueprintYinYueJiangLin2" when points.Length > 0 && coefficients.Length == 1
                => [.. points.Select(p => new Pulse(p, coefficients[0]))],
            "huxiaolieduan" or "yemujuechang" or "xingxingshike" or "PetSkill_2_2_KuangBaoYueYeMao"
                when coefficients.Length > 1 && points.Length >= coefficients.Length
                => [.. coefficients.Select((c, i) => new Pulse(points[points.Length - coefficients.Length + i], c))],
            "WuShiZhiNu" when points.Length > 0 && coefficients.Length == 1
                => [.. points.Where(p => p > 0).Select(p => new Pulse(p, coefficients[0] / points.Count(v => v > 0)))],
            "JingZhunShouShu" when points.Length > 0 && coefficients.Length > 0
                => [.. points.Select(p => new Pulse(p, coefficients[0]))],
            "Pet_MmSarah_23342" when literals.Length > 0 && points.Length >= (int)literals[0] && coefficients.Length > 0
                => [.. points.TakeLast((int)literals[0]).Select(p => new Pulse(p, coefficients[0]))],
            "Siwanliandao" when points.Length > 0 && coefficients.Length > 0 && description.Length >= 5
                => [.. points.Select(p => new Pulse(p, coefficients[0], HealCoefficient: description[4]))],
            "XieXinLianRen" when coefficients.Length == 3 && points.Length >= 3
                => [.. coefficients.Select((c, i) => new Pulse(points[points.Length - 3 + i], c, Rectangle: i == 2))],
            "PetSkill_2_2_EXiaoNanJue" when description.Length >= 6 && description[4] is > 0 and <= 50
                => [.. Enumerable.Range(0, (int)description[4]).Select(i => new Pulse(first, description[5] * 100,
                    DelayMs: (int)(i * description[1] * 1000)))],
            "Pet_Bolkoba_23292" when points.Length > 0 && literals.Length >= 2 && literals[1] is > 0 and <= 10 && coefficients.Length > 0
                => [.. points.SelectMany(p => Enumerable.Range(0, (int)literals[1]).Select(_ => new Pulse(p, coefficients[0] / literals[1])))],
            "xuesefengbao" when points.Length >= 2 && description.Length >= 8 && description[5] is > 0 and <= 50
                => [.. Enumerable.Range(0, (int)description[5]).Select(i => new Pulse(first + (points[^1] - first) * i / description[5],
                    description[3] * 100, HealCoefficient: description[4]))
                    .Append(new Pulse(points[^1], description[6] * 100, HealCoefficient: description[7]))],
            "PetSkill_2_2_YaoDao" when coefficients.Length == 2
                => [new(first, coefficients[0]), new(first, coefficients[1], 6)], // ElementColumns._iPoison.
            _ => [new(first)],
        };
    }

    internal static JsonElement PulseRow(JsonElement row, Pulse pulse, JsonElement? active = null)
    {
        if (pulse.Coefficient == null && pulse.Element == null && !pulse.Rectangle) return row;
        var node = GuardianPetRules.CopyObject(row);
        if (pulse.Coefficient is { } coefficient) node["_kDamageParam1"] = new JsonArray(JsonValue.Create(coefficient));
        if (pulse.Element is { } element) node["_iEleType"] = element;
        if (pulse.Rectangle && active is { } wrapper)
        {
            var desc = Numbers(wrapper, "_kDescData");
            var literals = DescriptionLiterals(wrapper);
            if (desc.Length >= 3 && literals.Length >= 2)
            {
                node["_iRangeType"] = SkillRelease.Area; node["_iRelease"] = SkillRelease.FixRect; node["_iTargetMax"] = (int)literals[1];
                node["_kRange"] = new JsonArray(desc[1] * 100, desc[2] * 100);
                node["_ro3GuardianForwardRectangle"] = true;
            }
        }
        return JsonSerializer.SerializeToElement(node);
    }
}
