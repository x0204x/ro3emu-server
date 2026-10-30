using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class ShopService
{
    /// <summary>只查現行原表；v1.0.24 已採原生無限箭矢，不再建立舊版 100 Zeny 補售列。</summary>
    private JsonElement? FindCommodity(int id) => _data.Row("ShopCommodityConfig", id);

    /// <summary>保留一般商城既有分類，不注入或覆蓋商店。</summary>
    private JsonElement? FindShop(long classId)
    {
        foreach (var row in _data.Table("ShopConfig").Values)
        {
            var view = new ShopConfig(row);
            if (view.ClassId != classId) continue;
            return row;
        }
        return null;
    }
}
