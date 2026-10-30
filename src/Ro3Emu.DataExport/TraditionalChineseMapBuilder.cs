using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ro3Emu.DataExport;

/// <summary>以玩家自己客戶端同一 SID 的繁中內容建立覆寫；不執行 Lua，也不重建伺服器資料。</summary>
public static partial class TraditionalChineseMapBuilder
{
    public const string TextFile = "loc_zh_TW.tsv";
    public const string SidFile = "loc_zh_TW.sids.tsv";
    public const string Header = "# RO3Emu localization profile zh-TW v1";
    public sealed record Summary(int SourceIds, int TranslatedIds, int TextEntries, int AmbiguousTexts,
        string[] MissingTraditionalIds, string[] PlaceholderMismatches, string[] UnresolvedIds);

    public static Summary Build(string simplifiedJson, string englishJson, string traditionalJson, string destination,
        IEnumerable<string>? overrides = null)
    {
        var simplified = ReadTable(simplifiedJson);
        var english = ReadTable(englishJson);
        var traditional = ReadTable(traditionalJson);
        var texts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var sids = new SortedDictionary<long, string>();
        var votes = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var missing = new List<string>();
        var mismatches = new List<string>();
        foreach (var id in simplified.Keys.Union(english.Keys).Order())
        {
            if (!traditional.TryGetValue(id, out string? target) || !Usable(id, target)) { missing.Add(id); continue; }
            sids.Add(long.Parse(id, CultureInfo.InvariantCulture), target);
            texts[target] = target; // 原生繁中不再被英文對照表改回去。
            foreach (var table in new[] { simplified, english })
                if (table.TryGetValue(id, out string? source)) AddVote(source, target, id, votes, mismatches);
        }
        foreach (var (source, candidates) in votes)
            if (!texts.ContainsKey(source))
                texts[source] = candidates.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key;
        var knownSids = simplified.Keys.Union(english.Keys).Select(id => long.Parse(id, CultureInfo.InvariantCulture)).ToHashSet();
        ApplyOverrides(overrides ?? [], texts, sids, knownSids);
        Directory.CreateDirectory(destination);
        Write(Path.Combine(destination, TextFile), texts.Select(p => (p.Key, p.Value)));
        Write(Path.Combine(destination, SidFile), sids.Select(p => (p.Key.ToString(CultureInfo.InvariantCulture), p.Value)));
        return new(simplified.Keys.Union(english.Keys).Count(), sids.Count, texts.Count,
            votes.Count(p => p.Value.Count > 1), missing.ToArray(), mismatches.ToArray(),
            knownSids.Where(id => !sids.ContainsKey(id)).Order().Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray());
    }

    private static Dictionary<string, string> ReadTable(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in document.RootElement.EnumerateObject())
        {
            if (!long.TryParse(row.Name, NumberStyles.None, CultureInfo.InvariantCulture, out long sid) || sid <= 0 ||
                !row.Value.TryGetProperty("_kDes", out var text) || text.ValueKind != JsonValueKind.String ||
                !result.TryAdd(row.Name, text.GetString()!))
                throw new InvalidDataException("不完整或重複的本地化 SID：" + row.Name);
        }
        if (result.Count == 0) throw new InvalidDataException("本地化表不可為空。");
        return result;
    }

    private static bool Usable(string id, string text) =>
        !string.IsNullOrWhiteSpace(text) && text != id && text != "None" && !text.StartsWith("Not Find Key", StringComparison.Ordinal);

    private static void AddVote(string source, string target, string id,
        Dictionary<string, Dictionary<string, int>> votes, List<string> mismatches)
    {
        if (!Usable(id, source)) return;
        var sourceTokens = Tokens().Matches(source).Select(m => m.Value).Order(StringComparer.Ordinal);
        var targetTokens = Tokens().Matches(target).Select(m => m.Value).Order(StringComparer.Ordinal);
        if (!sourceTokens.SequenceEqual(targetTokens)) { mismatches.Add(id); return; }
        if (!votes.TryGetValue(source, out var candidates)) votes[source] = candidates = new(StringComparer.Ordinal);
        candidates[target] = candidates.GetValueOrDefault(target) + 1;
    }

    /// <summary>人工修正使用 text 或 sid 範圍；同字異義必須使用 sid。</summary>
    private static void ApplyOverrides(IEnumerable<string> sources, SortedDictionary<string, string> texts,
        SortedDictionary<long, string> sids, HashSet<long> knownSids)
    {
        foreach (string source in sources)
            foreach (string raw in source.TrimStart('\uFEFF').Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var cells = line.Split('\t');
                if (cells.Length != 3 || string.IsNullOrWhiteSpace(cells[2])) throw new InvalidDataException("繁中覆寫須為 scope、key、value 三欄。");
                string key = Unescape(cells[1]), value = Unescape(cells[2]);
                if (cells[0] == "text") texts[key] = value;
                else if (cells[0] == "fallback-text") texts.TryAdd(key, value);
                else if (cells[0] == "sid" && long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out long sid) && knownSids.Contains(sid))
                    sids[sid] = value;
                else throw new InvalidDataException("未知的繁中覆寫範圍或 SID：" + key);
            }
    }

    private static void Write(string path, IEnumerable<(string Key, string Value)> rows)
    {
        var text = new StringBuilder(Header + "\n");
        foreach (var (key, value) in rows) text.Append(Escape(key)).Append('\t').Append(Escape(value)).Append('\n');
        string temporary = path + ".new";
        File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
    private static string Unescape(string value) => Regex.Replace(value, @"\\(.)", m => m.Groups[1].Value switch { "t" => "\t", "r" => "\r", "n" => "\n", var c => c });

    [GeneratedRegex(@"%(?:\d+\$)?[-+ 0#]*\d*(?:\.\d+)?[sdfiuxX]|\{\d+\}|[$@]\{\d+\}")]
    private static partial Regex Tokens();
}
