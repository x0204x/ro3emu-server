using System.Globalization;
using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// Reads a field of a config row as one <see cref="ConfigKind"/>. An absent or differently shaped value reads as the
/// kind's empty value (0, "", []), as the server always read the tables; <see cref="ConfigValidator"/> reports such
/// values in a client export at startup. Lua stores an empty list as {}, which reads as [].
/// </summary>
public static class ConfigValue
{
    public static long Integral(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) ? IntegralValue(value) ?? 0 : 0;

    public static int Integral32(JsonElement row, string field) =>
        (int)Math.Clamp(Integral(row, field), int.MinValue, int.MaxValue);

    public static double Number(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) ? Number(value) ?? 0 : 0;

    public static bool Bool(JsonElement row, string field) => row.TryGetProperty(field, out var value) && IsTrue(value);

    public static string Text(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) ? TextOf(value) : "";

    public static long[] Ints(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) ? IntList(value) : [];

    public static double[] Numbers(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) ? NumberList(value) : [];

    public static long[][] IntRows(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(IntList)] : [];

    public static double[][] NumberRows(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(NumberList)] : [];

    public static string[] Texts(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(TextOf)] : [];

    public static (long Id, double Value)[] Pairs(JsonElement row, string field) => [.. GameData.Pairs(row, field)];

    public static JsonElement? Raw(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    /// <summary>An integer number exactly, also beyond 2^53 (a double would round it); a fraction is
    /// truncated.</summary>
    public static long? IntegralValue(JsonElement value) => value.ValueKind != JsonValueKind.Number ? null
        : value.TryGetInt64(out long exact) ? exact : (long)value.GetDouble();

    /// <summary>A number, also when the Lua table stored it as a string ("2000").</summary>
    public static double? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture,
            out double parsed) => parsed,
        _ => null,
    };

    private static bool IsTrue(JsonElement value) => value.ValueKind == JsonValueKind.True
        || value.ValueKind == JsonValueKind.Number && value.GetDouble() != 0;

    private static string TextOf(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static long[] IntList(JsonElement list) => list.ValueKind == JsonValueKind.Array
        ? [.. list.EnumerateArray().Select(v => IntegralValue(v) ?? (long)(Number(v) ?? 0))] : [];

    private static double[] NumberList(JsonElement list) => list.ValueKind == JsonValueKind.Array
        ? [.. list.EnumerateArray().Select(v => Number(v) ?? 0)] : [];
}
