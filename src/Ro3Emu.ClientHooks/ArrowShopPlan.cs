using System;
using System.Collections.Generic;
using Ro3Emu.Shared;

namespace Ro3Emu.ClientHooks
{
    /// <summary>歷史補售計畫；官方無限箭矢啟用後不再產生商店或商品。</summary>
    internal static class ArrowShopPlan
    {
        internal readonly struct Shop
        {
            internal readonly long Id, ShopId, ClassId, Type;
            internal Shop(long id, long shopId, long classId, long type)
            { Id = id; ShopId = shopId; ClassId = classId; Type = type; }
        }

        internal readonly struct Commodity
        {
            internal readonly long Id, ClassId, ItemId;
            internal Commodity(long id, long classId, long itemId)
            { Id = id; ClassId = classId; ItemId = itemId; }
        }

        internal static bool NeedsShop(IReadOnlyList<Shop> existing)
        {
            if (!ArrowShopCatalog.Enabled) return false;
            bool found = false;
            foreach (var row in existing)
            {
                if (row.Id == ArrowShopCatalog.ShopConfigId && row.ClassId != ArrowShopCatalog.ClassId)
                    throw new InvalidOperationException("Arrow shop config id is already owned by another class.");
                if (row.ClassId != ArrowShopCatalog.ClassId) continue;
                if (row.ShopId != ArrowShopCatalog.ShopId || row.Type != ArrowShopCatalog.ShopType)
                    throw new InvalidOperationException("Arrow shop class is already owned by another shop.");
                found = true;
            }
            return !found;
        }

        internal static IReadOnlyList<ArrowShopOffer> MissingOffers(IReadOnlyList<Commodity> existing)
        {
            if (!ArrowShopCatalog.Enabled) return [];
            var missing = new List<ArrowShopOffer>();
            foreach (var offer in ArrowShopCatalog.Offers)
            {
                bool found = false;
                foreach (var row in existing)
                {
                    if (row.Id == offer.CommodityId &&
                        (row.ClassId != ArrowShopCatalog.ClassId || row.ItemId != offer.ItemId))
                        throw new InvalidOperationException("Arrow commodity id is already owned by another item or class.");
                    if (row.ClassId == ArrowShopCatalog.ClassId && row.ItemId == offer.ItemId) found = true;
                }
                if (!found) missing.Add(offer);
            }
            return missing;
        }
    }
}
