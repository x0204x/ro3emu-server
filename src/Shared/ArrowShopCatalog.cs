using System;
using System.Collections.Generic;

namespace Ro3Emu.Shared;

/// <summary>使用者指定的本地補售政策（inferred），不是原服售價；閃電箭尚未確認，不加入別名。</summary>
public static class ArrowShopCatalog
{
    public const int Version = 1;
    // 原件 ResourceObtainInfoConfig[1014]：type1／Shop102／Class10202／Function70104。
    public const int ShopConfigId = 9910200, ShopId = 102, ClassId = 10202, ShopType = 1, FunctionId = 70104;
    public const long ShopNameSid = 10160000268L;
    public const int CurrencyId = 1, UnitPrice = 100;

    public static IReadOnlyList<ArrowShopOffer> Offers { get; } = Array.AsReadOnly(new[]
    {
            new ArrowShopOffer(9910212, 1002012, 2), // 火箭矢
            new ArrowShopOffer(9910213, 1002013, 3), // 水靈箭矢
            new ArrowShopOffer(9910214, 1002014, 4), // 銀箭矢
            new ArrowShopOffer(9910215, 1002015, 5), // 風靈箭矢
            new ArrowShopOffer(9910216, 1002016, 6), // 地靈箭矢
        });

    public static bool TryGetOffer(int commodityId, out ArrowShopOffer offer)
    {
        foreach (var candidate in Offers)
            if (candidate.CommodityId == commodityId) { offer = candidate; return true; }
        offer = default;
        return false;
    }
}

/// <summary>雙端共用不可變的商品身分；原生欄位由各自的完整配置列建構器建立。</summary>
public readonly struct ArrowShopOffer(int commodityId, int itemId, int sortId)
{
    public int CommodityId { get; } = commodityId;
    public int ItemId { get; } = itemId;
    public int SortId { get; } = sortId;
}
