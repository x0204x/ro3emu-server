using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class TradeService
{
    private async Task QueryProducts(GameSession s, SQueryProductByParamRequest req)
    {
        CQueryProductByParamWithPage page = new()
        {
            ItemId = Entry(req.ItemId), IsPublicity = req.IsPublicity,
            ItemSpecialType = req.ItemSpecialType, PageNo = Page(req.PageNo), PageSize = Size(req.PageSize),
            TaxRate = TaxBasisPoints,
        };
        page.SortFiled["item_price"] = req.SortFiled.GetValueOrDefault("item_price", "Asc");
        await market.RunAsync(s, tx =>
        {
            page.TotalCount = 0;
            page.DataList.Clear();
            var rows = FilterRows(Active(tx), req.ItemId, req.ItemSpecialType, req.IsPublicity,
                req.UserRoleId, req.ShareSkuId, req.WhereFiled).ToList();
            if (page.SortFiled["item_price"] == "Desc") rows = rows.OrderByDescending(l => l.UnitPrice).ThenBy(l => l.SaleTimeMs).ToList();
            else rows = rows.OrderBy(l => l.UnitPrice).ThenBy(l => l.SaleTimeMs).ToList();
            page.TotalCount = rows.Count;
            page.DataList.AddRange(rows.Skip((page.PageNo - 1) * page.PageSize).Take(page.PageSize).Select(Commodity));
            return 0;
        });
        await s.SendGameAsync(QueryProduct, new CQueryProductByParamWithPageResp { Data = page });
    }

    private IEnumerable<Listing> FilterRows(IEnumerable<Listing> rows, string itemId, ETradeItemSpecialType type,
        bool publicity, string roleId = "", string sku = "", IEnumerable<SFieldWhereFilter>? filters = null)
    {
        if (Entry(itemId) is var entry and > 0) rows = rows.Where(l => l.Entry == entry);
        if (type != ETradeItemSpecialType.None) rows = rows.Where(l => l.SpecialType == (int)type);
        if (!string.IsNullOrEmpty(roleId)) rows = rows.Where(l => l.SellerId.ToString(CultureInfo.InvariantCulture) == roleId);
        if (!string.IsNullOrEmpty(sku)) rows = rows.Where(l => l.Sku == sku);
        rows = rows.Where(l => Visible(l, publicity));
        foreach (var f in filters ?? [])
        {
            // LC_TradeServer sends typed fields/operators, some `in` operands JSON arrays.
            // Unknown filters fail closed rather than broadening a market search (inferred).
            string field = f.FieldName;
            string op = f.LogicalOperator;
            var values = FilterValues(f.LogicalValue).ToHashSet(StringComparer.OrdinalIgnoreCase);
            rows = rows.Where(l =>
            {
                string candidate = field switch
                {
                    "item_id" => l.Entry.ToString(CultureInfo.InvariantCulture),
                    "item_type" => data.Row("TradeConfig", l.Entry) is { } row ? GameData.Int(row, "_iType").ToString(CultureInfo.InvariantCulture) : "",
                    "sku_id" => l.Sku,
                    "spu_id" => l.Spu,
                    "item_price" => l.UnitPrice.ToString(CultureInfo.InvariantCulture),
                    "currency_type" => l.Currency.ToString(CultureInfo.InvariantCulture),
                    _ => "",
                };
                if (candidate.Length == 0) return false;
                if (op == "in") return values.Contains(candidate);
                if (op == "=") return values.Contains(candidate);
                if (!double.TryParse(candidate, CultureInfo.InvariantCulture, out var actual)
                    || !double.TryParse(f.LogicalValue, CultureInfo.InvariantCulture, out var wanted)) return false;
                return op == ">=" ? actual >= wanted : op == "<=" && actual <= wanted;
            });
        }
        return rows;
    }

    private static IEnumerable<string> FilterValues(string value)
    {
        if (!value.StartsWith('[')) return [value];
        try
        {
            using var doc = JsonDocument.Parse(value);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Select(e => e.ToString()).ToArray() : [];
        }
        catch (JsonException) { return []; }
    }

    private async Task Inventory(GameSession s, QueryItemInventoryInfoByItemIdRequest req)
    {
        var dataRows = new ItemInventoryInfoByItemId();
        await market.RunAsync(s, tx =>
        {
            dataRows.ItemInventoryInfos.Clear();
            foreach (string id in req.ItemIds)
            {
                int entry = Entry(id);
                var rows = Active(tx).Where(l => l.Entry == entry && Visible(l, req.IsPublicity)
                    && (req.ItemSpecialType == ETradeItemSpecialType.None || l.SpecialType == (int)req.ItemSpecialType)).ToList();
                dataRows.ItemInventoryInfos.Add(new ItemInventoryInfo
                {
                    ItemId = id, InventoryQuantity = rows.Sum(l => l.Quantity),
                    MinPrice = rows.Count == 0 ? 0 : rows.Min(l => l.UnitPrice),
                    MaxPrice = rows.Count == 0 ? 0 : rows.Max(l => l.UnitPrice),
                    IsPublicity = req.IsPublicity, ItemSpecialType = req.ItemSpecialType,
                });
            }
            return 0;
        });
        await s.SendGameAsync(QueryInventory, new ItemInventoryInfoByItemIdResponse { Data = dataRows });
    }

    private async Task AllInventory(GameSession s, CQueryAllItemInventoryInfoByItemIdRequest req)
    {
        var result = new CAllItemInventoryInfoByItemId();
        await market.RunAsync(s, tx =>
        {
            result.ItemInfo.Clear();
            result.PublicityItemInfo.Clear();
            var rows = Active(tx);
            foreach (var (kind, requested, target) in new[]
            {
                (false, req.ItemInfo, result.ItemInfo), (true, req.PublicityItemInfo, result.PublicityItemInfo),
            })
                foreach (var (special, list) in requested)
                {
                    var group = new ItemInventoryInfoByItemId();
                    foreach (var id in list.Itemid)
                    {
                        int entry = Entry(id);
                        var matches = rows.Where(l => l.Entry == entry && l.SpecialType == special && Visible(l, kind)).ToList();
                        group.ItemInventoryInfos.Add(new ItemInventoryInfo
                        {
                            ItemId = id, ItemSpecialType = (ETradeItemSpecialType)special, IsPublicity = kind,
                            InventoryQuantity = matches.Sum(l => l.Quantity),
                            MinPrice = matches.Count == 0 ? 0 : matches.Min(l => l.UnitPrice),
                            MaxPrice = matches.Count == 0 ? 0 : matches.Max(l => l.UnitPrice),
                        });
                    }
                    target[special] = group;
                }
            return 0;
        });
        await s.SendGameAsync(QueryAllInventory, new CAllItemInventoryInfoByItemIdResponse { Data = result });
    }

    private async Task Price(GameSession s, FindItemPriceRequest req)
    {
        int entry = Entry(req.ItemId);
        var row = Eligible(entry);
        if (row == null)
        {
            await s.SendGameAsync(FindPrice, new FindItemPriceResponse { Code = ErrorCodes.TradeRequest, Errno = ErrorCodes.TradeRequest, Errmsg = "Item cannot be traded" });
            return;
        }
        var rule = PriceRule(row.Value);
        var result = new FindItemPrice
        {
            ItemId = req.ItemId, SpuId = string.IsNullOrEmpty(req.SpuId) ? Spu(entry) : req.SpuId,
            ItemExtInfo = req.ItemExtInfo, MinPrice = rule.Min, MaxPrice = rule.Max,
            RecommendedPrice = rule.Base,
        };
        result.PriceLadder.AddRange(rule.Ladder.Select(n => (double)n));
        await s.SendGameAsync(FindPrice, new FindItemPriceResponse { Data = result });
    }

    private async Task Booth(GameSession s)
    {
        var page = new CQueryProductByParamWithPage { PageNo = 1, PageSize = 10, TaxRate = TaxBasisPoints };
        await market.RunAsync(s, tx =>
        {
            page.TotalCount = 0;
            page.DataList.Clear();
            var rows = Active(tx).Where(l => l.SellerId == s.Role!.Id).OrderBy(l => l.SaleTimeMs).ToList();
            page.TotalCount = rows.Count;
            page.DataList.AddRange(rows.Select(Commodity));
            return 0;
        });
        await s.SendGameAsync(OwnBooth, new CQueryPlayerBoothInfoResp { Data = page });
    }

    private async Task Skus(GameSession s, QueryProductBySkuIdReq req)
    {
        var result = new CQueryProductBySkuId();
        await market.RunAsync(s, tx =>
        {
            result.CommodityInfos.Clear();
            result.CommodityInfos.AddRange(Active(tx).Where(l => req.SkuIds.Contains(l.Sku)).Select(Commodity));
            return 0;
        });
        await s.SendGameAsync(QuerySkus, new CQueryProductBySkuIdResp { Data = result });
    }

    private async Task SearchTop(GameSession s, CSearchTopPropsRequest req)
    {
        var page = new CQueryProductByParamWithPage
        {
            ItemId = Entry(req.ItemId), IsPublicity = req.IsPublicity,
            PageNo = Page(req.PageNo), PageSize = Size(req.PageSize), TaxRate = TaxBasisPoints,
        };
        page.SortFiled["item_price"] = req.SortFiled.GetValueOrDefault("item_price", "Asc");
        await market.RunAsync(s, tx =>
        {
            page.TotalCount = 0;
            page.DataList.Clear();
            var rows = FilterRows(Active(tx), req.ItemId, ETradeItemSpecialType.None,
                req.IsPublicity, req.UserRoleId, filters: req.WhereFiled).ToList();
            rows = page.SortFiled["item_price"] == "Desc" ? rows.OrderByDescending(l => l.UnitPrice).ToList() : rows.OrderBy(l => l.UnitPrice).ToList();
            page.TotalCount = rows.Count;
            page.DataList.AddRange(rows.Skip((page.PageNo - 1) * page.PageSize).Take(page.PageSize).Select(Commodity));
            return 0;
        });
        await s.SendGameAsync(Search, new CSearchTopPropsResp { Data = page });
    }

    private async Task Multi(GameSession s, SQueryMultiProductByParamRequest req)
    {
        var result = new CQueryMultiProductByParamWithPageRespData();
        await market.RunAsync(s, tx =>
        {
            result.Items.Clear();
            var all = Active(tx);
            foreach (var q in req.Items)
            {
                var page = new CQueryProductByParamWithPage
                {
                    ItemId = Entry(q.ItemId), IsPublicity = q.IsPublicity, ItemSpecialType = q.ItemSpecialType,
                    PageNo = Page(q.PageNo), PageSize = Size(q.PageSize), TaxRate = TaxBasisPoints,
                };
                page.SortFiled["item_price"] = q.SortFiled.GetValueOrDefault("item_price", "Asc");
                var rows = FilterRows(all, q.ItemId, q.ItemSpecialType, q.IsPublicity, q.UserRoleId, q.ShareSkuId, q.WhereFiled).ToList();
                rows = page.SortFiled["item_price"] == "Desc" ? rows.OrderByDescending(l => l.UnitPrice).ToList() : rows.OrderBy(l => l.UnitPrice).ToList();
                page.TotalCount = rows.Count;
                page.DataList.AddRange(rows.Skip((page.PageNo - 1) * page.PageSize).Take(page.PageSize).Select(Commodity));
                result.Items.Add(page);
            }
            return 0;
        });
        await s.SendGameAsync(MultiQuery, new CQueryMultiProductByParamWithPageResp { Data = result });
    }

    private async Task CreditScore(GameSession s, CFindUserCreditScoreRequest req)
    {
        long roleId = long.TryParse(req.UserRoleId, CultureInfo.InvariantCulture, out var requested) && requested > 0
            ? requested : s.Role!.Id;
        DBUserTradeInfo? state = null;
        await market.RunAsync(s, tx =>
        {
            state = null;
            var blob = tx.LoadBlob(roleId, "trade_info");
            state = blob is { Length: > 0 } ? DBUserTradeInfo.Parser.ParseFrom(blob) : null;
            return 0;
        });
        if (state == null)
        {
            await s.SendGameAsync(Credit, new CFindUserCreditScoreResponse
            { Code = ErrorCodes.TradeRequest, Errno = ErrorCodes.TradeRequest, Errmsg = "Role trade state missing", QueryReason = req.QueryReason });
            return;
        }
        var info = new CFindUserCreditScore
        {
            UserRoleId = roleId.ToString(CultureInfo.InvariantCulture),
            UserCreditScore = state.CreditPoint, CreditScoreRatio = state.CreditScoreRatio,
            UserCreditScoreRate = state.CreditScoreRatio,
            LowCreditScoreThreshold = (int)Public("TradeCreditMin", 0),
        };
        await s.SendGameAsync(Credit, new CFindUserCreditScoreResponse { Data = info, QueryReason = req.QueryReason });
    }

    private Task PenaltyOrders(GameSession s, CQueryPenaltyOrdersRequest req) =>
        s.SendGameAsync(Penalties, new CQueryPenaltyOrdersResponse
        {
            // No review/penalty rule is recovered, so no invented penalty entries (inferred empty state).
            Data = new CQueryPenaltyOrdersPage { PageNo = Page(req.PageNo), TotalCount = 0 },
        });

    private async Task Recent(GameSession s, CFindSpuRecentTransationPriceRequest req)
    {
        var result = new CFindSpuRecentTransationPrice();
        await market.RunAsync(s, tx =>
        {
            result.PointPrice.Clear();
            result.MinPrice = 0;
            result.MaxPrice = 0;
            result.AvgPrice = 0;
            var rows = tx.All<Sale>(History).Where(x => x.Entry == Entry(req.ItemId)).OrderByDescending(x => x.TimeMs).Take(20).ToList();
            result.SpuId = Spu(Entry(req.ItemId));
            if (rows.Count > 0)
            {
                result.MinPrice = rows.Min(x => x.UnitPrice); result.MaxPrice = rows.Max(x => x.UnitPrice);
                result.AvgPrice = rows.Average(x => x.UnitPrice);
                result.PointPrice.AddRange(rows.OrderBy(x => x.TimeMs).Select(x => (double)x.UnitPrice));
            }
            return 0;
        });
        await s.SendGameAsync(RecentPrice, new CFindSpuRecentTransationPriceResponse { Data = result });
    }

    private async Task Buys(GameSession s, QueryBuyTradeInfoRequest req)
    {
        var result = new BuyTradeInfoEntity();
        await market.RunAsync(s, tx =>
        {
            result.BuyTradeInfos.Clear();
            var rows = tx.All<Sale>(History).Where(x => x.BuyerId == s.Role!.Id
                && (req.ItemId.Length == 0 || x.Entry == Entry(req.ItemId)))
                .OrderByDescending(x => x.TimeMs).Skip((Page(req.PageNo) - 1) * Size(req.PageSize)).Take(Size(req.PageSize));
            foreach (var row in rows) result.BuyTradeInfos.Add(new BuyTradeInfo
            {
                OrderId = row.Id, SerialNumber = row.Id, ItemId = row.Entry.ToString(CultureInfo.InvariantCulture),
                SpuId = row.Spu, TotalPrice = (long)row.UnitPrice * row.Quantity,
                Quantity = row.Quantity, SaleTime = row.TimeMs,
                UserRoleId = row.BuyerId.ToString(CultureInfo.InvariantCulture),
                ItemBaseInfo = ByteString.CopyFrom(character.ItemInfoOf(row.Item).ToByteArray()),
            });
            return 0;
        });
        await s.SendGameAsync(BuyHistory, new BuyTradeInfoResponse { Data = result });
    }

    private async Task Sales(GameSession s, CQuerySaleTradeInfoRequest req)
    {
        var result = new CSaleTradeInfoEntity();
        await market.RunAsync(s, tx =>
        {
            result.SaleTradeInfos.Clear();
            var rows = tx.All<Sale>(History).Where(x => x.SellerId == s.Role!.Id
                && (req.ItemIds.Count == 0 || req.ItemIds.Contains(x.Entry.ToString(CultureInfo.InvariantCulture))))
                .OrderByDescending(x => x.TimeMs).Skip((Page(req.PageNo) - 1) * Size(req.PageSize)).Take(Size(req.PageSize));
            foreach (var row in rows) result.SaleTradeInfos.Add(new CSaleTradeInfo
            {
                DetailId = row.Id, OrderId = row.Id, SerialNumber = row.Id,
                ItemId = row.Entry.ToString(CultureInfo.InvariantCulture), SpuId = row.Spu, SkuId = row.Sku,
                SalePrice = row.UnitPrice, TotalPrice = (long)row.UnitPrice * row.Quantity,
                Quantity = row.Quantity, SaleTime = row.TimeMs,
                UserRoleId = row.SellerId.ToString(CultureInfo.InvariantCulture),
                ItemBaseInfo = ByteString.CopyFrom(character.ItemInfoOf(row.Item).ToByteArray()),
            });
            return 0;
        });
        await s.SendGameAsync(SaleHistory, new CSaleTradeInfoResponse { Data = result });
    }
}
