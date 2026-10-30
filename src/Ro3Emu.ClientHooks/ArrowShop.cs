using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Ro3Emu.Shared;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// 玩家指定的箭矢補售，每支 100 Zeny（本地政策，非原服售價）。原件 Item._iAcquire=1014
    /// 指向 ShopPanel type1/shop102/class10202，但原件兩張商店表沒有這個分類或箭矢商品。
    /// 只補記憶體資料，保留 Function70104、原商品及原生快取建立流程，不改簽章 Lua。
    /// </summary>
    public static class ArrowShop
    {
        private const string SupplementMarker = "__ro3emuArrowShopVersion";
        public delegate void SearchMethod(Obf_R.Obf_ff self, long shopType);

        // 每個 LuaEnv 各自保留原函式和 delegate；不跨環境共用 reload 指標。
        private static readonly ConditionalWeakTable<object, Installation> Installations = new ConditionalWeakTable<object, Installation>();

        internal static void Install(object env, LA_LuaManager manager, Obf_R.Obf_ff global)
        {
            // 歷史相容入口保留，但官方 v1.0.24 的無限箭矢不需要付費補售。
            if (!ArrowShopCatalog.Enabled) return;
            if (env == null || global == null || Installations.TryGetValue(env, out _)) return;
            try
            {
                var shops = LuaPatches.Module(manager, global, "lua_cfg_shopconfig");
                var goods = LuaPatches.Module(manager, global, "lua_cfg_shopcommodityconfig");
                var helper = LuaPatches.Module(manager, global, "lc_shophelper");
                var db = global.Obf_RuA<Obf_R.Obf_ff>("Lua_DBManager");
                var install = new Installation(global, shops, goods, db, helper);
                Installations.Add(env, install);
                install.Apply(shops, goods, db);
            }
            catch (Exception error)
            {
                Debug.LogWarning("[Ro3Emu.ArrowShop] install failed: " + error.GetType().Name + ": " + error.Message);
            }
        }

        private sealed class Installation
        {
            private readonly Obf_R.Obf_ff _global;
            private readonly Obf_R.Obf_Ef _shopInit, _goodsInit, _reload, _rawset, _rawget, _pairs, _search, _shopOpen;
            private readonly LuaPatches.SelfMethod _shopWrapper, _goodsWrapper, _reloadWrapper;
            private readonly SearchMethod _searchWrapper;

            internal Installation(Obf_R.Obf_ff global, Obf_R.Obf_ff shops, Obf_R.Obf_ff goods, Obf_R.Obf_ff db, Obf_R.Obf_ff helper)
            {
                _global = global;
                _shopInit = shops?.Obf_RuA<Obf_R.Obf_Ef>("OnInitDatas");
                _goodsInit = goods?.Obf_RuA<Obf_R.Obf_Ef>("OnInitDatas");
                _reload = db?.Obf_RuA<Obf_R.Obf_Ef>("ReLoadAllCfg");
                _rawset = global.Obf_LuA<Obf_R.Obf_Ef>("rawset");
                _rawget = global.Obf_LuA<Obf_R.Obf_Ef>("rawget");
                _pairs = global.Obf_LuA<Obf_R.Obf_Ef>("pairs");
                _search = goods?.Obf_RuA<Obf_R.Obf_Ef>("GenerateSearchMap");
                _shopOpen = helper?.Obf_RuA<Obf_R.Obf_Ef>("CheckSubshopIsOpenOrNot");
                if (_shopInit == null || _goodsInit == null || _reload == null || _rawset == null || _rawget == null ||
                    _pairs == null || _search == null || _shopOpen == null)
                    throw new InvalidOperationException("Native shop initialization or reload binding is unavailable.");
                _shopWrapper = InitShops;
                _goodsWrapper = InitGoods;
                _reloadWrapper = Reload;
                _searchWrapper = GenerateSearchMap;
            }

            internal void Apply(Obf_R.Obf_ff shops, Obf_R.Obf_ff goods, Obf_R.Obf_ff db)
            {
                shops.Obf_luA<string, LuaPatches.SelfMethod>("OnInitDatas", _shopWrapper);
                goods.Obf_luA<string, LuaPatches.SelfMethod>("OnInitDatas", _goodsWrapper);
                goods.Obf_luA<string, SearchMethod>("GenerateSearchMap", _searchWrapper);
                db.Obf_luA<string, LuaPatches.SelfMethod>("ReLoadAllCfg", _reloadWrapper);
                Refresh();
                Debug.Log("[Ro3Emu.ArrowShop] arrow supplement installed; native gates and existing goods preserved");
            }

            private void InitShops(Obf_R.Obf_ff self)
            {
                try { InjectShop(self); }
                catch (Exception error) { Report(error); }
                _shopInit.Obf_HuA(self);
            }

            private void InitGoods(Obf_R.Obf_ff self)
            {
                try { InjectGoods(self); }
                catch (Exception error) { Report(error); }
                _goodsInit.Obf_HuA(self);
            }

            private void Reload(Obf_R.Obf_ff self)
            {
                // 保留先前 PassChallenges 等 wrapper；只在完整原鏈完成後補回目前資料。
                _reload.Obf_HuA(self);
                try { Refresh(); }
                catch (Exception error) { Report(error); }
            }

            private void Refresh()
            {
                var owner = Owner();
                var shops = owner.Obf_RuA<Obf_R.Obf_ff>("m_kCFG_ShopConfig");
                var goods = owner.Obf_RuA<Obf_R.Obf_ff>("m_kCFG_ShopCommodityConfig");
                if (InjectShop(shops)) _shopInit.Obf_HuA(shops);
                if (InjectGoods(goods)) _goodsInit.Obf_HuA(goods);
            }

            private Obf_R.Obf_ff Owner()
            {
                var db = _global.Obf_RuA<Obf_R.Obf_ff>("Lua_DBManager");
                var result = db?.Obf_RuA<Obf_R.Obf_Ef>("GetInstance")?.Obf_HuA(db);
                var owner = result != null && result.Length > 0 ? result[0] as Obf_R.Obf_ff : null;
                if (owner == null) throw new InvalidOperationException("Native config owner is unavailable.");
                return owner;
            }

            private void GenerateSearchMap(Obf_R.Obf_ff self, long shopType)
            {
                _search.Obf_HuA(self, shopType);
                try { CompleteSearchMap(self, shopType); }
                catch (Exception error) { Report(error); }
            }

            private void CompleteSearchMap(Obf_R.Obf_ff self, long shopType)
            {
                if (shopType != ArrowShopCatalog.ShopType) return;
                var open = _shopOpen.Obf_HuA(ArrowShopCatalog.ClassId);
                if (open == null || open.Length == 0 || !(open[0] is bool allowed) || !allowed) return;
                var names = self.Obf_RuA<Obf_R.Obf_ff>("ShopItemNameMap")?.Obf_ruA<long, Obf_R.Obf_ff>(shopType);
                if (names == null) return;
                var itemConfig = Owner().Obf_RuA<Obf_R.Obf_ff>("m_kCFG_ItemConfig");
                var getName = itemConfig?.Obf_RuA<Obf_R.Obf_Ef>("GetName");
                if (getName == null) return;
                var values = Values(self);
                foreach (var offer in ArrowShopCatalog.Offers) AddSearchEntry(values, names, itemConfig, getName, offer);
            }

            private void AddSearchEntry(Obf_R.Obf_ff values, Obf_R.Obf_ff names, Obf_R.Obf_ff itemConfig,
                Obf_R.Obf_Ef getName, ArrowShopOffer offer)
            {
                // 原 GenerateSearchMap 使用 ipairs，無法走到這些高 ID 補列；只補本次實際存在的 row。
                var existing = _rawget.Obf_HuA(names, offer.CommodityId);
                if (existing != null && existing.Length > 0 && existing[0] != null) return;
                var entry = _rawget.Obf_HuA(values, offer.CommodityId);
                var row = entry != null && entry.Length > 0 ? entry[0] as Obf_R.Obf_ff : null;
                if (row == null || Number(row, SupplementMarker) != ArrowShopCatalog.Version ||
                    Number(row, "_iClassID") != ArrowShopCatalog.ClassId || Number(row, "_iItemId") != offer.ItemId) return;
                var name = getName.Obf_HuA(itemConfig, offer.ItemId);
                if (name == null || name.Length == 0 || name[0] == null) return;
                _rawset.Obf_HuA(names, offer.CommodityId, List(name[0], (long)offer.ItemId, row));
            }

            private bool InjectShop(Obf_R.Obf_ff config)
            {
                var values = Values(config);
                var existing = new List<ArrowShopPlan.Shop>();
                Visit(values, row => existing.Add(new ArrowShopPlan.Shop(Number(row, "_iID"),
                    Number(row, "_iShopId"), Number(row, "_iClassID"), Number(row, "_iShopType"))));
                if (!ArrowShopPlan.NeedsShop(existing)) return false;
                var row = Row("_iID", (long)ArrowShopCatalog.ShopConfigId, "_iShopId", (long)ArrowShopCatalog.ShopId,
                    "_iClassID", (long)ArrowShopCatalog.ClassId, "_iShopType", (long)ArrowShopCatalog.ShopType,
                    "_iClassName", List((long)ArrowShopCatalog.ShopNameSid), "_iShopName", List((long)ArrowShopCatalog.ShopNameSid),
                    "_iClassType", 0L, "_iFunctionId", (long)ArrowShopCatalog.FunctionId, "_kCurrencyDisplay", List((long)ArrowShopCatalog.CurrencyId),
                    "_iNpcId", 0L, "_kStartTime", List(), "_kEndTime", List(), "_kRefreshType", List(),
                    "_kRefreshTime", "", "_kRefreshExpend", List(), "_kIsBindGive", List(), "_iIndependentTab", 0L,
                    "_kGroup", List(), "_kMultiverseArray", List(0L));
                _rawset.Obf_HuA(values, ArrowShopCatalog.ShopConfigId, row);
                config.Obf_luA<string, object>("m_kAllIDList", null);
                return true;
            }

            private bool InjectGoods(Obf_R.Obf_ff config)
            {
                var values = Values(config);
                var existing = new List<ArrowShopPlan.Commodity>();
                Visit(values, row => existing.Add(new ArrowShopPlan.Commodity(Number(row, "_iID"),
                    Number(row, "_iClassID"), Number(row, "_iItemId"))));
                var missing = ArrowShopPlan.MissingOffers(existing);
                // 先檢查整份計畫並建立完整 row；ID 衝突時不先寫入一半商品。
                var made = new List<Obf_R.Obf_ff>();
                foreach (var offer in missing) made.Add(Commodity(offer));
                for (int i = 0; i < missing.Count; i++) _rawset.Obf_HuA(values, missing[i].CommodityId, made[i]);
                if (missing.Count > 0) config.Obf_luA<string, object>("m_kAllIDList", null);
                return missing.Count > 0;
            }

            private Obf_R.Obf_ff Commodity(ArrowShopOffer offer) => Row(
                SupplementMarker, (long)ArrowShopCatalog.Version,
                "_iID", (long)offer.CommodityId, "_iClassID", (long)ArrowShopCatalog.ClassId, "_iItemId", (long)offer.ItemId,
                "_iSortId", (long)offer.SortId, "_iItemPerMount", 1L, "_kItemPerPrice", List((long)ArrowShopCatalog.CurrencyId, (long)ArrowShopCatalog.UnitPrice),
                "_iAppranceId", 0L, "_iAppearanceId", 0L, "_iAppranceAwardId", 0L, "_iAppearanceAwardId", 0L,
                "_kAppearanceParts", List(), "_kItemPic", "", "_kItemPicFemale", "", "_iRecommendID", 0L,
                "_kBooth", List(), "_kReBanner", "", "_iIsShowCount", 1L, "_iDiscount", 0L,
                "_kRecommended", "", "_kRecommendedText", List(0L), "_iIsLock", 0L, "_iIsBuy", 0L,
                "_iSiegeSettlementLabel", 0L, "_kTimeLimit", List(), "_iGlobalPurchase", 0L, "_iPurchaseNum", 0L,
                "_iPurchaseTimesLimit", 0L, "_kIsBindGive", List(0L), "_iMerchantDiscount", 0L, "_iDescribe", List(0L),
                "_kStartTime", List(), "_kEndTime", List(), "_kBackground", "", "_iTrack", List(List("")),
                "_kAnimation", "", "_iGroup", 0L, "_iWeight", 0L, "_kMultiverseArray", List(0L));

            private static Obf_R.Obf_ff Values(Obf_R.Obf_ff config) =>
                config?.Obf_RuA<Obf_R.Obf_ff>("m_kConfigData")?.Obf_RuA<Obf_R.Obf_ff>("m_kValues")
                ?? throw new InvalidOperationException("Native shop values are unavailable.");

            private static long Number(Obf_R.Obf_ff row, string field) => LuaPatches.ToLong(row.Obf_RuA<object>(field));

            private void Visit(Obf_R.Obf_ff values, Action<Obf_R.Obf_ff> visit)
            {
                var iterator = _pairs.Obf_HuA(values);
                // 原件 configdefine.pairsFunc 只回傳 iterator、state，省略的控制值等同 nil。
                if (iterator == null || iterator.Length < 2 || !(iterator[0] is Obf_R.Obf_Ef next))
                    throw new InvalidOperationException("Native config iterator is unavailable.");
                object key = iterator.Length > 2 ? iterator[2] : null;
                for (int count = 0; count < 100000; count++)
                {
                    var entry = next.Obf_HuA(iterator[1], key);
                    if (entry == null || entry.Length == 0 || entry[0] == null) return;
                    key = entry[0];
                    if (entry.Length < 2 || !(entry[1] is Obf_R.Obf_ff row))
                        throw new InvalidOperationException("Native shop iterator returned a non-row value.");
                    visit(row);
                }
                throw new InvalidOperationException("Native shop iterator exceeded its bounded row limit.");
            }

            private Obf_R.Obf_ff List(params object[] values)
            {
                var made = _global.Obf_LuA<Obf_R.Obf_Ef>("table.pack").Obf_HuA();
                var table = (Obf_R.Obf_ff)made[0];
                _rawset.Obf_HuA(table, "n", null);
                _rawset.Obf_HuA(table, "__paramslen", null);
                for (int i = 0; i < values.Length; i++) table.Obf_luA<long, object>(i + 1, values[i]);
                return table;
            }

            private Obf_R.Obf_ff Row(params object[] fields)
            {
                var row = List();
                for (int i = 0; i < fields.Length; i += 2) row.Obf_luA<string, object>((string)fields[i], fields[i + 1]);
                return row;
            }

            private static void Report(Exception error) =>
                Debug.LogWarning("[Ro3Emu.ArrowShop] supplement refused: " + error.GetType().Name + ": " + error.Message);
        }
    }
}
