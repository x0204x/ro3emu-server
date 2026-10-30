using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Shared;

namespace Ro3Emu.Tests;

public sealed partial class ArrowShopTests
{
    private const int OriginalCommodity = 906981, OtherClass = 906982, OtherShop = 906983, Gate = 906984;

    private static string CreateFixture()
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-arrow-shop-" + Guid.NewGuid().ToString("N"));
        foreach (string source in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(root, Path.GetRelativePath(SyntheticData.Dir, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target);
        }
        // 只沿用產品共用目錄的身分；所有配置列與數值自造，沒有複製原件物品或商店列。
        foreach (var offer in ArrowShopCatalog.Offers)
            Row(root, "ItemConfig", offer.ItemId, new
            {
                _iID = offer.ItemId, _iType = 2, _iSubType = 207,
                _iBagTab = 1, _iStackLimit = 7, _kParam1 = "1"
            });
        Row(root, "FunctionConfig", ArrowShopCatalog.FunctionId, new { _iID = ArrowShopCatalog.FunctionId, _kOpenCondition = Gate });
        Row(root, "LC_ConditionList", Gate, new { m_kSubConditionList = new[] { new { m_kData = new[] { 1002, 3 } } } });
        return root;
    }

    private static void Row(string root, string table, int id, object row)
    {
        string path = Path.Combine(root, "config", table + ".json");
        var rows = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
        rows[id.ToString(CultureInfo.InvariantCulture)] = JsonSerializer.SerializeToNode(row);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, rows.ToJsonString());
    }

    private void NativeShop(int id, int classId, int shopId) => Row(_fixture, "ShopConfig", id, new
    { _iID = id, _iClassID = classId, _iShopId = shopId, _iShopType = 1, _iFunctionId = 0 });

    private void NativeCommodity(int id, int item, int classId) => Row(_fixture, "ShopCommodityConfig", id, new
    {
        _iID = id, _iClassID = classId, _iItemId = item, _iItemPerMount = 1, _kItemPerPrice = new[] { 1, 7 },
        _kMultiverseArray = new[] { 0 }
    });
}
