using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>A client export that does not match the declared config views (<see cref="ConfigValidator"/>).</summary>
public sealed class ConfigValidationException(IReadOnlyList<string> errors) : InvalidOperationException(
    "The game data does not match the server's config tables (Data/Config):" + Environment.NewLine
    + string.Join(Environment.NewLine, errors.Select(e => "  " + e)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Checks the loaded game data against every declared config view (<c>ConfigTables.All</c>) before players are
/// admitted, so a misspelt field or a client update that renames a column stops the server with the table and field
/// named, instead of reading as 0 in game. Every present value must have its field's kind. For a client export (one
/// with export.json, <paramref name="strict"/>) every declared table must exist and every row must carry every
/// declared field, unless the declaration marks it optional; hand-made data sets (tests) may leave fields out.
/// </summary>
public static class ConfigValidator
{
    private const int MaxErrors = 50;

    public static IReadOnlyList<string> Validate(GameData data, IEnumerable<ConfigTableInfo> tables, bool strict)
    {
        var errors = new List<string>();
        foreach (var table in tables)
        {
            var rows = data.Table(table.Name);
            if (rows.Count == 0)
            {
                if (strict && !table.Optional) errors.Add($"{table.Name}: the table is missing or empty");
                continue;
            }
            var entries = table.Grouped ? Entries(rows) : rows;
            foreach (var field in table.Fields) CheckField(table.Name, entries, field, strict, errors);
            if (errors.Count >= MaxErrors) break;
        }
        return errors.Count > MaxErrors ? [.. errors.Take(MaxErrors), "..."] : errors;
    }

    /// <summary><see cref="Validate"/>, throwing <see cref="ConfigValidationException"/> on any error.</summary>
    public static void Check(GameData data, IEnumerable<ConfigTableInfo> tables, bool strict)
    {
        var errors = Validate(data, tables, strict);
        if (errors.Count > 0) throw new ConfigValidationException(errors);
    }

    // A grouped table's rows are the entries of each key's list, named key[index].
    private static Dictionary<string, JsonElement> Entries(IReadOnlyDictionary<string, JsonElement> groups)
    {
        var entries = new Dictionary<string, JsonElement>();
        foreach (var (key, group) in groups)
        {
            if (group.ValueKind == JsonValueKind.Object && !group.EnumerateObject().Any()) continue;
            if (!GameData.TryGroupedRows(group, out var rows)) { entries[key] = group; continue; }
            int index = 0;
            foreach (var entry in rows) entries[$"{key}[{index++}]"] = entry;
        }
        return entries;
    }

    private static void CheckField(string table, IReadOnlyDictionary<string, JsonElement> rows, ConfigFieldInfo field,
        bool strict, List<string> errors)
    {
        int missing = 0;
        string? firstMissing = null;
        foreach (var (key, row) in rows)
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(field.Name, out var value)
                || value.ValueKind == JsonValueKind.Null)
            {
                missing++;
                firstMissing ??= key;
                continue;
            }
            if (!Fits(value, field.Kind))
            {
                errors.Add($"{table}.{field.Name}: row {key} holds {Describe(value)}, not {Expected(field.Kind)}");
                return; // one example per field is enough to find the cause
            }
        }
        if (strict && !field.Optional && missing > 0)
            errors.Add($"{table}.{field.Name}: missing in {missing} of {rows.Count} rows (first: {firstMissing})");
    }

    /// <summary>Whether a present value reads as the kind's value rather than its empty fallback.</summary>
    internal static bool Fits(JsonElement value, ConfigKind kind) => kind switch
    {
        ConfigKind.Integral => IsInteger(value),
        ConfigKind.Integral32 => IsInteger(value)
            && ConfigValue.IntegralValue(value) is >= int.MinValue and <= int.MaxValue,
        ConfigKind.Number => ConfigValue.Number(value) != null,
        ConfigKind.Bool => value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number,
        ConfigKind.Text => value.ValueKind == JsonValueKind.String,
        ConfigKind.Ints => Items(value).All(v => IsInteger(v) || IsIntegerText(v)),
        ConfigKind.Numbers => Items(value).All(v => ConfigValue.Number(v) != null),
        ConfigKind.IntRows => Items(value).All(r => IsList(r) && Items(r).All(v => IsInteger(v) || IsIntegerText(v))),
        ConfigKind.NumberRows => Items(value).All(r => IsList(r) && Items(r).All(v => ConfigValue.Number(v) != null)),
        ConfigKind.Texts => Items(value).All(v => v.ValueKind == JsonValueKind.String),
        ConfigKind.Pairs => Items(value).All(p => p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2
            && ConfigValue.Number(p[0]) != null && ConfigValue.Number(p[1]) != null),
        ConfigKind.Raw => true,
        _ => false,
    } && (!IsListKind(kind) || IsList(value));

    private static bool IsListKind(ConfigKind kind) => kind is ConfigKind.Ints or ConfigKind.Numbers
        or ConfigKind.IntRows or ConfigKind.NumberRows or ConfigKind.Texts or ConfigKind.Pairs;

    // Lua exports an empty list as an empty object.
    private static bool IsList(JsonElement value) => value.ValueKind == JsonValueKind.Array
        || value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any();

    private static JsonElement[] Items(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray()] : [];

    private static bool IsInteger(JsonElement value) => value.ValueKind == JsonValueKind.Number
        && (value.TryGetInt64(out _) || Math.Abs(value.GetDouble() % 1) == 0);

    private static bool IsIntegerText(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && ConfigValue.Number(value) is { } n && Math.Abs(n % 1) == 0;

    private static string Describe(JsonElement value)
    {
        string raw = value.GetRawText();
        return value.ValueKind.ToString().ToLowerInvariant() + " " + (raw.Length > 40 ? raw[..40] + "..." : raw);
    }

    private static string Expected(ConfigKind kind) => kind switch
    {
        ConfigKind.Integral => "an integer",
        ConfigKind.Integral32 => "a 32-bit integer",
        ConfigKind.Number => "a number",
        ConfigKind.Bool => "a boolean",
        ConfigKind.Text => "a text",
        ConfigKind.Ints => "a list of integers",
        ConfigKind.Numbers => "a list of numbers",
        ConfigKind.IntRows => "a list of integer lists",
        ConfigKind.NumberRows => "a list of number lists",
        ConfigKind.Texts => "a list of texts",
        ConfigKind.Pairs => "a list of [id, value] lists",
        _ => kind.ToString(),
    };
}
