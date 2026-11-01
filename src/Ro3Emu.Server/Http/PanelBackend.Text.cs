using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ro3Emu.Panel;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The client's own text rules for the database pages: a text field [text id, param...] filled as Language.Trans does
/// it, an attribute line as Lua_CFG_AttributeConfig.GetAdditionDesc writes it, and a special effect's description as
/// LC_SpecialEffectHelper.GetSpecialEffectDesc picks it.
/// </summary>
public sealed partial class PanelBackend
{
    /// <summary>
    /// A text field read as Language.Trans (Utility/Language.lua, _strReplace and Lua_Utility StrReplace): the params
    /// that look like a tag ("&lt;color=#cc762a&gt;", "&lt;/link&gt;") fill ^{n} in order, the others fill @{n}, and
    /// <paramref name="dynamic"/> (a row's _kDescData) fills ${n}; a missing value leaves its number, as the client
    /// prints it. ParseContent then keeps the text of a "^id#text^" content link. The coloured parts are marked runs.
    /// </summary>
    private List<PanelRun> Trans(JsonElement? field, IReadOnlyList<string>? dynamic = null)
    {
        if (field is not { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() == 0) return [];
        var values = array.EnumerateArray().Select(Param).ToList();
        if (!long.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) || id == 0) return [];
        var tags = values.Skip(1).Where(IsTag).ToList();
        var extras = values.Skip(1).Where(v => !IsTag(v)).ToList();
        string text = Placeholder().Replace(data.Text(id), m =>
        {
            int n = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var list = m.Groups[1].Value switch { "@" => extras, "^" => tags, _ => dynamic ?? [] };
            return n >= 1 && n <= list.Count ? list[n - 1] : m.Groups[2].Value;
        });
        return Runs(ParseContent(text));
    }

    /// <summary>A text id alone, as plain text.</summary>
    private string Plain(JsonElement? field) => string.Concat(Trans(field).Select(r => r.Text));

    private static string Param(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.TryGetInt64(out long n) ? n.ToString(CultureInfo.InvariantCulture) : value.GetDouble().ToString(CultureInfo.InvariantCulture),
        _ => "",
    };

    private static bool IsTag(string value) => value.StartsWith('<') && value.EndsWith('>');

    [GeneratedRegex(@"([@^$])\{(\d+)\}")]
    private static partial Regex Placeholder();

    /// <summary>ParseContent: after the first "^", each part is "id#text" (a link the client draws as its text) or plain.</summary>
    private static string ParseContent(string text)
    {
        if (!text.Contains('^')) return text;
        var parts = text.Split('^');
        var result = new StringBuilder(parts[0]);
        foreach (string part in parts.Skip(1))
        {
            var pieces = part.Split('#');
            result.Append(pieces.Length == 1 ? pieces[0] : pieces[1]);
        }
        return result.ToString();
    }

    /// <summary>The text between &lt;color&gt; tags as marked runs; links and the other rich-text tags are left out.</summary>
    private static List<PanelRun> Runs(string text)
    {
        var runs = new List<PanelRun>();
        int depth = 0, at = 0;
        void Add(string part)
        {
            if (part.Length == 0) return;
            bool mark = depth > 0;
            if (runs.Count > 0 && runs[^1].Mark == mark) runs[^1] = runs[^1] with { Text = runs[^1].Text + part };
            else runs.Add(new PanelRun(part, mark));
        }
        foreach (Match tag in RichTag().Matches(text))
        {
            Add(text[at..tag.Index]);
            if (tag.Groups[1].Value.Equals("color", StringComparison.OrdinalIgnoreCase)) depth += tag.Value.StartsWith("</", StringComparison.Ordinal) ? -1 : 1;
            depth = Math.Max(0, depth);
            at = tag.Index + tag.Length;
        }
        Add(text[at..]);
        return runs;
    }

    [GeneratedRegex(@"</?(color|link|b|i|size|u)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex RichTag();

    /// <summary>
    /// An attribute and its value as the client's equipment and card tips write them (GetAdditionDesc): the name from
    /// _iNameEquip, the value signed; _iTextValueDisplayType 1 negates it, 2 shows value/100, 3 value*100, otherwise a
    /// percentage attribute (_iDataType 1) shows value/100 with two decimals and a percent sign.
    /// </summary>
    private PanelStat AttributeLine(long attribute, double value)
    {
        var row = AttributeConfig.Get(data, attribute);
        string name = row is { } r && r.NameEquip is [var text, ..] && data.Text(text) is { Length: > 0 } equip ? equip : AttributeName(attribute);
        double v = Math.Ceiling(value);
        long display = row?.TextValueDisplayType ?? 0;
        if (display == 1) v = -v;
        string Signed(double x, string format) => (x >= 0 ? "+" : "") + x.ToString(format, CultureInfo.InvariantCulture);
        string shown = display switch
        {
            2 => Signed(v / 100, "0.##"),
            3 => Signed(Math.Round(v * 100), "0"),
            _ when row?.DataType == 1 => Signed(v / 100, "0.00") + "%",
            _ => Signed(v, "0"),
        };
        return new PanelStat(name, shown);
    }

    private List<PanelRun> AttributeRuns(long attribute, double value)
    {
        var line = AttributeLine(attribute, value);
        return [new PanelRun(line.Name + " "), new PanelRun(line.Value, Mark: true)];
    }

    /// <summary>
    /// A special effect's description (GetSpecialEffectDesc): with _iIsOtherDesc, the effect named by _kOtherDesc (or
    /// each op of _kSpecialEffect) - [1, attribute, value] an attribute line, [2, buff] the buff's description, [3, skill
    /// row] the skill's - otherwise its own _iEffectDesc filled with _kDescData.
    /// </summary>
    private List<List<PanelRun>> EffectLines(long effectId)
    {
        if (SpecialEffectConfig.Get(data, effectId) is not { } effect) return [];
        if (effect.IsOtherDesc != 1) return Lines(Trans(effect.EffectDesc, effect.DescData));
        if (effect.OtherDesc.Length > 0) return Lines(OpRuns(effect.OtherDesc, effect));
        return [.. effect.SpecialEffect.Select(op => OpRuns(op, effect)).Where(r => r.Count > 0)];
    }

    private static List<List<PanelRun>> Lines(List<PanelRun> runs) => runs.Count > 0 ? [runs] : [];

    private List<PanelRun> OpRuns(long[] op, SpecialEffectConfig effect) => op switch
    {
        [1, var attribute, var value, ..] => AttributeRuns(attribute, value),
        [2, var buff, ..] when BuffConfig.Get(data, buff) is { } row => Trans(row.Description, row.DescData),
        [3, var skill, ..] when SkillConfig.Get(data, skill) is { } row => Trans(row.Description, row.DescData),
        _ => Trans(effect.EffectDesc, effect.DescData),
    };
}
