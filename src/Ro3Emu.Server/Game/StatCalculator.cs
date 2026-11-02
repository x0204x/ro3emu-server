using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>Final attribute values (property data) and the pre-percentage part the client previews from (baseData).</summary>
public sealed class StatSheet
{
    public Dictionary<int, long> Data { get; } = [];
    public Dictionary<int, long> Base { get; } = [];

    public long this[int id]
    {
        get => Data.GetValueOrDefault(id);
        set => Data[id] = value;
    }
}

/// <summary>
/// The client's attribute pipeline (LC_DAL_PlayerInfo._refreshFinalLocalVal): every attribute
/// belongs to the group of its AttributeConfig._iPreAttri root and has a _iCalculateType (1 Base, 2 BasePerAdd,
/// 3 BuffValueAdd, 4 BuffPerAdd, 6 OtherPerAdd). Values are scaled by _iDataType (1: /10000, 2: /100) and summed per
/// type, then <c>final = B1 * (1 + B2 + B6) * (1 + B4) + B3</c> without intermediate rounding; integer attributes are
/// rounded up like the client's display (Mathf.Ceil).
/// </summary>
public static class StatCalculator
{
    public static StatSheet Compute(IReadOnlyDictionary<int, double> raw, GameData data)
    {
        var sheet = new StatSheet();
        var groups = new Dictionary<int, double[]>();
        foreach (var (id, value) in raw)
        {
            var row = data.Row("AttributeConfig", id);
            int root = row is { } r1 ? (int)GameData.Int(r1, "_iPreAttri") : 0;
            if (row == null || root == 0)
            {
                // Not part of a group (client-derived values such as 117-119): sent as is.
                sheet.Data[id] = (long)Math.Round(value);
                sheet.Base[id] = (long)Math.Floor(value);
                continue;
            }
            int calc = (int)GameData.Int(row.Value, "_iCalculateType");
            if (calc is < 1 or > 6 or 5) calc = 1;
            if (!groups.TryGetValue(root, out var b)) groups[root] = b = new double[7];
            b[calc] += Scale(DataType(id, data), value);
            if (id != root)
            {
                // Members (per_atk 1031 ...) travel as their own properties so the client's preview can redo the group.
                sheet.Data[id] = (long)Math.Floor(value);
                sheet.Base[id] = (long)Math.Floor(value);
            }
        }
        foreach (var (root, b) in groups)
        {
            double final = b[1] * (1 + b[2] + b[6]) * (1 + b[4]) + b[3];
            int dt = DataType(root, data);
            sheet.Data[root] = dt == 0 ? (long)Math.Ceiling(final - 1e-6) : (long)Math.Round(Unscale(dt, final));
            sheet.Base[root] = (long)Math.Floor(raw.GetValueOrDefault(root));
        }
        return sheet;
    }

    private static int DataType(int id, GameData data) =>
        data.Row("AttributeConfig", id) is { } row ? (int)GameData.Int(row, "_iDataType") : 0;

    private static double Scale(int dt, double v) => dt switch { 1 => v / 10000, 2 => v / 100, _ => v };

    private static double Unscale(int dt, double v) => dt switch { 1 => v * 10000, 2 => v * 100, _ => v };
}
