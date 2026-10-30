using System.Globalization;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Micro;
namespace Ro3Emu.Server.Game;

public sealed partial class TavernService
{
    private static long Number(object? o) => long.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), out long n) ? n : 0;
    private static object? Field(IDictionary<object, object?> map, string key) => map.TryGetValue(key, out var value) ? value : null;
    // 推定：沿既有 PartyService 的 SubType／Count 陣列契約；原件酒館 UI 僅證實接收
    // GroupCount，尚未由原服封包證實內部形狀，玩家端驗收仍需另行核對。
    public object DirectoryCounts(string area) => new Dictionary<string, object?>
    {
        ["Ret"] = MicroServiceRouter.Ret(true),
        ["GroupCount"] = _state.Rooms.Values.Where(r => !r.Closed && (area.Length == 0 || r.Area.ToString(CultureInfo.InvariantCulture) == area))
            .GroupBy(r => r.Type).Select(g => (object)new Dictionary<string, object?> { ["SubType"] = g.Key, ["Count"] = g.Count() }).ToArray()
    };
    public object Directory(object? args)
    {
        var list = args as object?[] ?? [];
        IEnumerable<Room> rooms = _state.Rooms.Values.Where(r => !r.Closed);
        if (list.Length > 1 && list[1] is object?[] strings)
            foreach (var c in strings.OfType<IDictionary<object, object?>>())
            {
                string key = Convert.ToString(Field(c, "Key"), CultureInfo.InvariantCulture) ?? "";
                string value = Convert.ToString(Field(c, "Value"), CultureInfo.InvariantCulture) ?? "";
                if (key == "area" && value.Length > 0) rooms = rooms.Where(r => r.Area.ToString(CultureInfo.InvariantCulture) == value);
            }
        if (list.Length > 2 && list[2] is object?[] integers)
            foreach (var c in integers.OfType<IDictionary<object, object?>>())
            {
                string key = Convert.ToString(Field(c, "Key"), CultureInfo.InvariantCulture) ?? "";
                long min = Number(Field(c, "MinValue")), max = Number(Field(c, "MaxValue"));
                if (key == "BaroomHeadCount") rooms = rooms.Where(r => r.Members.Count >= min && r.Members.Count <= max);
                if (key == "BaroomCreateTime") rooms = rooms.Where(r => r.CreatedMs / 1000 >= min && r.CreatedMs / 1000 <= max);
                if (key == "BarRoomType") rooms = rooms.Where(r => r.Type >= min && r.Type <= max);
            }
        int start = list.Length > 4 ? (int)Math.Clamp(Number(list[4]), 0, int.MaxValue) : 0;
        int size = list.Length > 5 ? (int)Math.Clamp(Number(list[5]), 0, 100) : 100;
        return new Dictionary<string, object?>
        {
            ["Ret"] = MicroServiceRouter.Ret(true),
            ["SummaryList"] = rooms.OrderBy(r => r.CreatedMs).ThenBy(r => r.Id).Skip(start).Take(size)
                .Select(r => (object)new Dictionary<string, object?> { ["ID"] = r.Id.ToString(CultureInfo.InvariantCulture), ["GameSummaryData"] = Summary(r).ToByteArray() }).ToArray()
        };
    }
}
