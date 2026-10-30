using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Ro3Emu.Server.Data;

/// <summary>
/// The client's own config tables, exported to data/config/*.json by tools/ConfigExport. <see cref="Preload"/> parses
/// every table and skill template before the gates open (a cold table cost seconds inside a player's request); after
/// that, lookups are lock-free reads of a frozen snapshot. Without a preload (tests, tools) tables load on first use.
/// A missing table is empty, except that a client export must carry every table and field the server declares
/// (Data/Config, checked by <see cref="Config.ConfigValidator"/> during the preload).
/// </summary>
public sealed class GameData(IOptions<ServerOptions> options, ILogger<GameData> log)
{
    private readonly Dictionary<string, Dictionary<string, JsonElement>> _tables = [];
    private readonly object _sync = new();
    private volatile Dictionary<long, string>? _texts;
    private volatile Dictionary<long, string>? _clientTexts;
    private volatile FrozenDictionary<string, Dictionary<string, JsonElement>>? _frozen;
    private readonly string[] _clientTextTables = ClientTextTables(options.Value.ClientTextLanguage);

    private static string[] ClientTextTables(string language) => language switch
    {
        "zh_TW" => ["Localization_zh_TW", "Localization_zh_CN"],
        "zh_CN" => ["Localization_zh_CN"],
        "en" => [],
        _ => throw new ArgumentException("ClientTextLanguage 僅支援 zh_TW、zh_CN 或 en。", nameof(language)),
    };

    private string Root => options.Value.GameDataDir ?? options.Value.DataDir;

    /// <summary>Rows of a table keyed as in the client (usually the id as a string).</summary>
    public IReadOnlyDictionary<string, JsonElement> Table(string name)
    {
        if (_frozen is { } frozen && frozen.TryGetValue(name, out var hit)) return hit;
        lock (_sync)
        {
            if (_tables.TryGetValue(name, out var table)) return table;
            var path = Path.Combine(Root, "config", name + ".json");
            if (File.Exists(path)) table = LoadConfig(path);
            else
            {
                table = new();
                log.LogWarning("No {Path}; run tools/ConfigExport", path);
            }
            _tables[name] = table;
            return table;
        }
    }

    // The rows reference one document per table, which lives as long as the table (never disposed: its buffers are
    // ordinary GC memory then). Cloning every row made one document per row: ~2x the heap for the same data.
    private static Dictionary<string, JsonElement> LoadConfig(string path)
    {
        var table = new Dictionary<string, JsonElement>();
        var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
            foreach (var p in doc.RootElement.EnumerateObject()) table[p.Name] = p.Value;
        else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var (row, i) in doc.RootElement.EnumerateArray().Select((r, i) => (r, i)))
                table[row.ValueKind == JsonValueKind.Object && row.TryGetProperty("_iID", out var rid)
                    ? rid.ToString() : (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)] = row;
        return table;
    }

    private static Dictionary<string, JsonElement> LoadObject(string path)
    {
        var table = new Dictionary<string, JsonElement>();
        var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var p in doc.RootElement.EnumerateObject()) table[p.Name] = p.Value;
        return table;
    }

    /// <summary>
    /// Parses every config table and skill template in parallel, builds the text index and freezes the snapshot that
    /// lookups then read without a lock. Run once at startup, before players are admitted.
    /// </summary>
    public void Preload()
    {
        var watch = Stopwatch.StartNew();
        var loaded = new ConcurrentDictionary<string, Dictionary<string, JsonElement>>();
        var config = Path.Combine(Root, "config");
        var skills = Path.Combine(Root, "skills");
        var files = (Directory.Exists(config) ? Directory.GetFiles(config, "*.json").Select(p => (Key: Path.GetFileNameWithoutExtension(p), Path: p, Config: true)) : [])
            .Concat(Directory.Exists(skills) ? Directory.GetFiles(skills, "*.json").Select(p => (Key: "skills/" + Path.GetFileNameWithoutExtension(p), Path: p, Config: false)) : [])
            .ToArray();
        Parallel.ForEach(files, f => loaded[f.Key] = f.Config ? LoadConfig(f.Path) : LoadObject(f.Path));
        lock (_sync)
        {
            foreach (var (key, table) in loaded) _tables.TryAdd(key, table);
            _frozen = _tables.ToFrozenDictionary();
        }
        Text(0); // builds the English text index used by server rules and logs
        ClientText(0); // builds the source-language index used for strings sent to the client
        // A client export (with export.json) must carry every declared table and field; any data set must give each
        // declared field values of its kind. A mismatch stops the server here, before players are admitted.
        bool export = File.Exists(Path.Combine(Root, ExportManifest));
        Config.ConfigValidator.Check(this, Config.ConfigTables.All, strict: export);
        log.LogInformation(
            "Game data preloaded: {Tables} tables in {Ms} ms, managed heap {Mb} MB, {Views} config views checked{Strict}",
            files.Length, watch.ElapsedMilliseconds, GC.GetTotalMemory(false) >> 20, Config.ConfigTables.All.Count,
            export ? " against the client export" : "");
    }

    /// <summary>tools/ConfigExport writes this next to config/ for a complete client export.</summary>
    public const string ExportManifest = "export.json";

    public JsonElement? Row(string table, long id) => Table(table).TryGetValue(
        id.ToString(System.Globalization.CultureInfo.InvariantCulture), out var row) ? row : null;

    /// <summary>
    /// Repeated config rows are arrays in the Genesis export. D Asia also emits serial-keyed objects such as
    /// DropGroupConfig { "1": row, "2": row }; accept both representations while rejecting ordinary row objects.
    /// </summary>
    public static bool TryGroupedRows(JsonElement value, out JsonElement[] rows)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            rows = [.. value.EnumerateArray()];
            return rows.All(row => row.ValueKind == JsonValueKind.Object);
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keyed = new List<(long Serial, JsonElement Row)>();
            foreach (var property in value.EnumerateObject())
            {
                if (!long.TryParse(property.Name, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out long serial)
                    || property.Value.ValueKind != JsonValueKind.Object)
                {
                    rows = [];
                    return false;
                }
                keyed.Add((serial, property.Value));
            }
            rows = [.. keyed.OrderBy(row => row.Serial).Select(row => row.Row)];
            // Genesis may intentionally serialize an empty repeated group as [], while an empty object is the
            // exporter's placeholder for an absent/unresolved group. Keep those meanings distinct.
            return keyed.Count > 0;
        }
        rows = [];
        return false;
    }

    /// <summary>Localized text (Localization_en: id -> {_kDes}); config "names" are arrays of text ids.</summary>
    public string Text(long id)
    {
        var texts = _texts;
        if (texts == null)
            lock (_sync)
            {
                texts = _texts;
                if (texts == null)
                {
                    texts = [];
                    foreach (var (_, row) in Table("Localization_en"))
                    {
                        if (row.TryGetProperty("_iID", out var tid) && row.TryGetProperty("_kDes", out var des) && des.ValueKind == JsonValueKind.String)
                            texts[tid.GetInt64()] = des.GetString()!;
                    }
                    _texts = texts; // published only when complete; readers never see a half-built index
                }
            }
        return texts.TryGetValue(id, out var text) ? text : "";
    }

    /// <summary>
    /// Traditional Chinese text sent in client-visible string fields. Some overhead labels bypass the patched client
    /// text setters, so resolving zh_TW on the server is required. Missing zh_TW rows fall back through zh_CN to English.
    /// Server rules keep using <see cref="Text"/> so English searches and comparisons do not change.
    /// </summary>
    public string ClientText(long id)
    {
        var texts = _clientTexts;
        if (texts == null)
            lock (_sync)
            {
                texts = _clientTexts;
                if (texts == null)
                {
                    texts = new();
                    foreach (string table in _clientTextTables)
                    {
                        foreach (var (_, row) in Table(table))
                        {
                            var localized = new Config.LocalizationEn(row);
                            // G Language.lua 的 fallback：空白、純 SID 與 None 不是可用的譯文。
                            string value = localized.Des;
                            if (!string.IsNullOrWhiteSpace(value) && value != "None" &&
                                value != localized.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
                                !value.StartsWith("Not Find Key", StringComparison.Ordinal))
                                texts.TryAdd(localized.Id, value);
                        }
                    }
                    _clientTexts = texts;
                }
            }
        return texts.TryGetValue(id, out var text) ? text : Text(id);
    }

    /// <summary>Text of a field holding [textId, ...].</summary>
    /// <summary>
    /// A localized text field [textId, param1, param2, ...]: the text with @{n} replaced by the n-th parameter, as the
    /// client's GetWordNew does (NPCConfig 7010001 _kName [10470000361, "1"] is "Lv. 1 Ore Vein").
    /// </summary>
    public string TextOf(JsonElement row, string field)
        => TextOf(row, field, Text);

    /// <summary>Source-language text of a localized field for a client-visible wire string.</summary>
    public string ClientTextOf(JsonElement row, string field)
        => TextOf(row, field, ClientText);

    /// <summary>Source-language text of a typed localized field [textId, numeric parameters...].</summary>
    public string ClientTextOf(IReadOnlyList<long> values)
        => TextOf(values, ClientText);

    private static string TextOf(JsonElement row, string field, Func<long, string> resolve)
    {
        if (!row.TryGetProperty(field, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0 || !v[0].TryGetInt64(out var id))
            return "";
        string text = resolve(id);
        for (int i = 1; i < v.GetArrayLength(); i++)
        {
            string placeholder = "@{" + i + "}";
            if (text.Contains(placeholder))
                text = text.Replace(placeholder, v[i].ValueKind == JsonValueKind.String ? v[i].GetString() : v[i].GetRawText());
        }
        return text;
    }

    private static string TextOf(IReadOnlyList<long> values, Func<long, string> resolve)
    {
        if (values.Count == 0) return "";
        string text = resolve(values[0]);
        for (int i = 1; i < values.Count; i++)
            text = text.Replace("@{" + i + "}", values[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
        return text;
    }

    public string ItemName(long itemId) => Row("ItemConfig", itemId) is { } row ? TextOf(row, "_iName") : "";

    /// <summary>Items whose English name contains <paramref name="text"/> (case-insensitive), best match first.</summary>
    public IEnumerable<(long Id, string Name)> FindItems(string text) =>
        Table("ItemConfig").Values
            .Select(r => (Id: r.GetProperty("_iID").GetInt64(), Name: TextOf(r, "_iName")))
            .Where(i => i.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name.Length);

    /// <summary>JobConfig _kAttri: [[attrIdx, value], ...] base attributes of a career (Novice: Speed 113 = 700).</summary>
    public IReadOnlyDictionary<long, long> JobAttributes(int career)
    {
        var result = new Dictionary<long, long>();
        if (Row("JobConfig", career) is { } job && job.TryGetProperty("_kAttri", out var attri) && attri.ValueKind == JsonValueKind.Array)
        {
            foreach (var pair in attri.EnumerateArray())
                if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2) result[pair[0].GetInt64()] = pair[1].GetInt64();
        }
        return result;
    }

    /// <summary>An integer field of a row (0 when absent or not a number), exact beyond 2^53.</summary>
    public static long Int(JsonElement row, string field) => Config.ConfigValue.Integral(row, field);

    /// <summary>A field holding [[id, value, ...], ...] (attribute lists): (id, first value) pairs.</summary>
    public static IEnumerable<(long Id, double Value)> Pairs(JsonElement row, string field)
    {
        if (!row.TryGetProperty(field, out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var pair in list.EnumerateArray())
        {
            if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() >= 2 && Number(pair[0]) is { } id && Number(pair[1]) is { } value)
                yield return ((long)id, value);
        }
    }

    /// <summary>A number, also when the Lua table stored it as a string ("2000").</summary>
    public static double? Number(JsonElement e) => Config.ConfigValue.Number(e);

    /// <summary>
    /// A skill template (Config/Skill/skill_<scriptShow>.lua exported to data/skills): m_fAtkRange (m), m_fTotalTime (s),
    /// m_kAtkPoints (hit moments in s on the template timeline).
    /// </summary>
    public JsonElement? SkillTemplate(long scriptShow)
    {
        string key = "skills/skill_" + scriptShow;
        if (_frozen is { } frozen && frozen.TryGetValue(key, out var hit)) return hit.Values.Cast<JsonElement?>().FirstOrDefault();
        lock (_sync)
        {
            if (!_tables.TryGetValue(key, out var table))
            {
                var path = Path.Combine(Root, "skills", "skill_" + scriptShow + ".json");
                table = File.Exists(path) ? LoadObject(path) : new();
                _tables[key] = table;
            }
            return table.Values.Cast<JsonElement?>().FirstOrDefault();
        }
    }

    /// <summary>A whole table exported to data/skills (Lua_Config_EffectData: effect id -> definition).</summary>
    public IReadOnlyDictionary<string, JsonElement> SkillTemplateTable(string name)
    {
        string key = "skills/" + name;
        if (_frozen is { } frozen && frozen.TryGetValue(key, out var hit)) return hit;
        lock (_sync)
        {
            if (_tables.TryGetValue(key, out var table)) return table;
            var path = Path.Combine(Root, "skills", name + ".json");
            table = File.Exists(path) ? LoadObject(path) : new();
            _tables[key] = table;
            return table;
        }
    }

    public string MapName(long sceneEntry) => Row("MapConfig", sceneEntry) is { } row ? TextOf(row, "_iName") : "";

    /// <summary>Source-language map name for a client-visible wire string.</summary>
    public string ClientMapName(long sceneEntry) => Config.MapConfig.Get(this, sceneEntry) is { } row ? ClientTextOf(row.Name) : "";
}

/// <summary>Runs <see cref="GameData.Preload"/> at startup, before the game loop and the gates start.</summary>
public sealed class GameDataPreload(GameData data) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) { data.Preload(); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
