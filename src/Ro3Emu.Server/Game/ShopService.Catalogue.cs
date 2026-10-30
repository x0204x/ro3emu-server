using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Shared;

namespace Ro3Emu.Server.Game;

public sealed partial class ShopService
{
    private readonly Dictionary<int, JsonElement> _arrowCommodities = ArrowShopCatalog.Offers
        .ToDictionary(offer => offer.CommodityId, offer => ArrowShopRows.Commodity(offer).Row);
    private readonly JsonElement _arrowShop = ArrowShopRows.Shop().Row;

    /// <summary>原件優先；只補明確缺少的箭矢列，同 ID 撞不同物品時拒絕補售。</summary>
    private JsonElement? FindCommodity(int id)
    {
        var original = _data.Row("ShopCommodityConfig", id);
        if (!ArrowShopCatalog.TryGetOffer(id, out var offer)) return original;
        if (original is { } existing)
            return new ShopCommodityConfig(existing) is var native && native.ItemId == offer.ItemId
                && native.ClassId == ArrowShopCatalog.ClassId ? existing : null;
        if (_data.Table("ShopCommodityConfig").Values.Any(row => new ShopCommodityConfig(row) is var native
            && native.ItemId == offer.ItemId && native.ClassId == ArrowShopCatalog.ClassId)) return null;
        if (ItemConfig.Get(_data, offer.ItemId) is not { Type: 2, SubType: 207 }
            || ItemConfig.Get(_data, ArrowShopCatalog.CurrencyId) is not { Type: 1 }
            || FindShop(ArrowShopCatalog.ClassId) == null) return null;
        return _arrowCommodities[id];
    }

    /// <summary>原件1014取得入口所需的Shop102／Class10202；不修改預載的配置快照。</summary>
    private JsonElement? FindShop(long classId)
    {
        foreach (var row in _data.Table("ShopConfig").Values)
        {
            var view = new ShopConfig(row);
            if (view.ClassId != classId) continue;
            if (classId == ArrowShopCatalog.ClassId
                && (view.ShopId != ArrowShopCatalog.ShopId || view.ShopType != ArrowShopCatalog.ShopType)) return null;
            return row;
        }
        if (classId != ArrowShopCatalog.ClassId || _data.Row("ShopConfig", ArrowShopCatalog.ShopConfigId) != null) return null;
        return _arrowShop;
    }
}
