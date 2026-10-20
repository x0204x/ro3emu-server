using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

public sealed partial class TradeService
{
    private async Task List(GameSession s, TradePutOnItemReq req)
    {
        Listing? created = null;
        DBUserTradeInfo? next = null;
        int listingFee = 0;
        int code = await market.RunAsync(s, tx =>
        {
            created = null;
            next = null;
            listingFee = 0;
            var owner = tx.Role(s.Role!.Id);
            var item = owner.Items.FirstOrDefault(i => i.Guid == req.ItemGuid && i.Grid == 0);
            if (item == null) return ErrorCodes.TradeItemMissing;
            if (TransactionItemError(s, item) is var itemError and not 0) return itemError;
            var config = Eligible(item.Entry);
            if (config == null || !functions.IsOpen(s, 2)) return ErrorCodes.TradeRequest;
            var row = config.Value;
            if (req.CurrencyType != GameData.Int(row, "_iMoney")) return ErrorCodes.TradeRequest;
            if (req.Quantity < 1 || req.Quantity > item.Stack || req.Quantity > GameData.Int(row, "_iShelveLimit")
                || req.Quantity > GameData.Int(row, "_iTradeStackMaxNum")) return ErrorCodes.TradeSaleQuantity;
            if (!ValidPrice(row, req.ItemPrice)) return ErrorCodes.TradePrice;
            if (Active(tx).Count(l => l.SellerId == owner.RoleId) >= s.Trade!.StallsNum) return ErrorCodes.TradeSpace;
            listingFee = ListingFee(req.ItemPrice, req.Quantity, req.CurrencyType);
            if (listingFee < 0) return ErrorCodes.TradeRequest;
            // UI_Sys_TradeSalePanelCtrl:476-491 checks the fee against item 2, regardless of sale currency.
            // (inferred) Debit the displayed ceil fee at successful listing commit.
            if (owner.Currency.GetValueOrDefault(2) < listingFee) return ErrorCodes.TradeCurrencyShort;
            owner.Currency[2] -= listingFee;
            if (listingFee > 0) tx.Economy.Sink(new(EconomyAssetKind.Currency, 2), listingFee, EconomyReason.MarketTax,
                EconomyCompartment.Currency, new(EconomyOwnerKind.Role, owner.RoleId.ToString(CultureInfo.InvariantCulture)));
            var escrow = item.Clone();
            escrow.Stack = req.Quantity;
            if (item.Stack == req.Quantity) owner.Items.Remove(item);
            else item.Stack -= req.Quantity;
            string sku = NewId();
            long now = NowMs;
            // (inferred) Top class is public for TradeItemHideTime minimum 1800 s; the config
            // has a 1800..5400 range but no recovered selection algorithm.
            long publicity = GameData.Int(row, "_iTradeType") == 1 ? now + HideSeconds() * 1000 : 0;
            long showHours = Public(publicity > 0 ? "TradeListingsTopShowTime" : "TradeListingsGeneralShowTime",
                publicity > 0 ? 168 : 24);
            created = new Listing
            {
                Sku = sku, Spu = Spu(item.Entry), SellerId = owner.RoleId, Entry = item.Entry,
                Currency = req.CurrencyType, UnitPrice = req.ItemPrice, Quantity = req.Quantity,
                SaleTimeMs = now, PublicUntilMs = publicity,
                ExpiresAtMs = now + Math.Max(1, showHours) * 3_600_000, // (inferred) config display times are hours.
                Item = escrow,
                SpecialType = SpecialTypeOf(item),
            };
            tx.Put(Listings, sku, created);
            // (inferred) tradeTimes counts successful listed units by entry. No reset period is
            // established, so it is informational and does not enforce a lifetime quota.
            next = s.Trade!.Clone();
            next.TradeTimes.TryGetValue(item.Entry, out int previous);
            next.TradeTimes[item.Entry] = (int)Math.Min(int.MaxValue, (long)previous + req.Quantity);
            tx.SaveBlob(owner.RoleId, "trade_info", next.ToByteArray());
            return 0;
        });
        var response = new TradePutOnItemResp { Result = code, ItemId = created?.Entry ?? 0, Tax = code == 0 ? listingFee : 0 };
        if (code == 0)
        {
            // (inferred) 32613 tax mirrors the listing fee; the exact original response field meaning is unverified.
            var rows = await OwnRows(s);
            response.BoothInfos.AddRange(rows.Select(Commodity));
            log.LogInformation("{Role} lists {Entry} x{Count} at {Price}, sku {Sku}", s.Role!.Id, created!.Entry, created.Quantity, created.UnitPrice, created.Sku);
        }
        await s.SendGameAsync(PutOn, response);
    }

    private long HideSeconds()
    {
        // (inferred) The emulator has no authoritative server level progression yet. Use the
        // first threshold rather than treating the listing role's base level as server level.
        var row = data.Table("TradeItemHideTime").Values.OrderBy(r => GameData.Int(r, "_kServerLev")).FirstOrDefault();
        return row.ValueKind == JsonValueKind.Object ? Math.Max(0, GameData.Int(row, "_kHideTimeMin")) : 1800;
    }

    private async Task Buy(GameSession s, TradeBuyItemsReq req) => await Purchase(s, req.ItemId, req.SpuId,
        req.SkuId, req.TotalQuantity, req.Price, req.TotalPrice, true);

    private async Task Buy(GameSession s, TradeBuyNormalItemsReq req) => await Purchase(s, req.ItemId, req.SpuId,
        "", req.TotalQuantity, req.Price, req.TotalPrice, false);

    private async Task Purchase(GameSession s, int entry, string spu, string sku, int quantity, int quote, int total, bool premium)
    {
        uint id = premium ? BuyTop : BuyNormal;
        int code = await market.RunAsync(s, tx =>
        {
            if (quantity < 1 || quote < 1 || (long)quantity * quote != total) return ErrorCodes.TradeRequest;
            var config = Eligible(entry);
            if (config == null || !functions.IsOpen(s, 2)) return ErrorCodes.TradeRequest;
            // TradeConfig class 1 is the individually addressed premium route; class 2 is
            // ordinary pooled stock. Keep the opcode from bypassing publicity or queue rules.
            if (GameData.Int(config.Value, "_iTradeType") != (premium ? 1 : 2)) return ErrorCodes.TradeRequest;
            if (quantity > GameData.Int(config.Value, "_iBuyLimit")) return ErrorCodes.TradeQuantity;
            var available = Active(tx).Where(l => l.Entry == entry && l.UnitPrice == quote);
            if (premium)
            {
                if (string.IsNullOrEmpty(sku)) return ErrorCodes.TradeRequest;
                available = available.Where(l => l.Sku == sku);
            }
            else
            {
                if (string.IsNullOrEmpty(spu)) return ErrorCodes.TradeRequest;
                available = available.Where(l => l.Spu == spu);
            }
            var candidates = available.ToList();
            if (candidates.Count == 0) return ErrorCodes.TradeBuyFailed;
            if (candidates.All(l => l.SellerId == s.Role!.Id)) return ErrorCodes.TradeOwnListing;
            if (candidates.Where(l => l.SellerId != s.Role!.Id).All(l => l.PublicUntilMs > NowMs))
                return ErrorCodes.TradePublicity;
            var rows = candidates.Where(l => l.SellerId != s.Role!.Id && l.PublicUntilMs <= NowMs)
                .OrderBy(l => l.SaleTimeMs).ThenBy(l => l.Sku).ToList();
            if (rows.Sum(l => (long)l.Quantity) < quantity) return ErrorCodes.TradeBuyFailed;
            int currency = rows[0].Currency;
            if (rows.Any(l => l.Currency != currency)) return ErrorCodes.TradeRequest;
            var buyer = tx.Role(s.Role!.Id);
            if (buyer.Currency.GetValueOrDefault(currency) < total) return ErrorCodes.TradeCurrencyShort;
            buyer.Currency[currency] -= total;
            int remaining = quantity;
            foreach (var listing in rows)
            {
                if (remaining == 0) break;
                int take = Math.Min(remaining, listing.Quantity);
                var item = listing.Item.Clone();
                item.Guid = 0; // (inferred) a claimed escrow part gets a new bag GUID; all item fields survive.
                item.Stack = take; item.Grid = 0;
                long gross = (long)take * listing.UnitPrice;
                long fee = MarketFee(gross, currency);
                if (fee > 0) tx.Economy.Sink(new(EconomyAssetKind.Currency, currency), fee, EconomyReason.MarketTax,
                    EconomyCompartment.Currency, new(EconomyOwnerKind.Role, buyer.RoleId.ToString(CultureInfo.InvariantCulture)));
                var sale = new Sale
                {
                    Id = NewId(), Sku = listing.Sku, Spu = listing.Spu, Entry = listing.Entry,
                    SellerId = listing.SellerId, BuyerId = buyer.RoleId, Quantity = take,
                    UnitPrice = listing.UnitPrice, TimeMs = NowMs, Item = item,
                };
                tx.DeliverMail(buyer.RoleId, 1064, [item], contentParams: SaleMailParams(sale, gross, false));
                tx.DeliverMail(listing.SellerId, 1063, currency: new Dictionary<int, long> { [currency] = gross - fee },
                    contentParams: SaleMailParams(sale, gross, true));
                tx.Put(History, sale.Id, sale);
                listing.Quantity -= take;
                listing.Item.Stack -= take;
                if (listing.Quantity == 0) tx.Delete(Listings, listing.Sku);
                else tx.Put(Listings, listing.Sku, listing);
                remaining -= take;
            }
            return 0;
        });
        log.LogInformation("{Role} buys {Entry} x{Count} via {Opcode}: {Result}", s.Role!.Id, entry, quantity, id, code);
        if (premium) await s.SendGameAsync(id, new TradeBuyItemsResp { Result = code });
        else await s.SendGameAsync(id, new TradeBuyNormalItemsResp { Result = code });
    }

    private long MarketFee(long amount, int currency)
    {
        // Lua_CFG_TradeConfigTax selects the last row whose threshold is strictly below amount.
        // (inferred) Apply the selected basis points to seller proceeds at settlement, rounding down.
        if (currency is not (2 or 3)) return 0;
        string suffix = currency == 2 ? "Zeny" : "Miao";
        long rate = 0;
        foreach (var row in data.Table("TradeConfigTax").Values.OrderBy(r => GameData.Int(r, "_kAmount" + suffix)))
            if (amount > GameData.Int(row, "_kAmount" + suffix)) rate = GameData.Int(row, "_kTax" + suffix);
        return amount * rate / 10_000;
    }

    private int ListingFee(int price, int count, int currency)
    {
        string suffix = currency switch { 1 => "Miao", 2 => "Zeny", _ => "" };
        if (suffix.Length == 0) return -1;
        int rate = (int)Public("ShelvesTaxation_" + suffix, currency == 1 ? 700 : 800);
        var interval = data.Table("PublicParamConfig").GetValueOrDefault("ShelvesTaxationInterval_" + suffix);
        long min = interval.ValueKind == JsonValueKind.Array && interval.GetArrayLength() > 1
            ? (long)(GameData.Number(interval[0]) ?? 0) : 0;
        long max = interval.ValueKind == JsonValueKind.Array && interval.GetArrayLength() > 1
            ? (long)(GameData.Number(interval[1]) ?? long.MaxValue) : long.MaxValue;
        long raw = ((long)price * count * rate + 9_999) / 10_000; // UI shows ceil.
        return (int)Math.Clamp(raw, min, max);
    }

    private async Task Delist(GameSession s, TradePutOffItemReq req)
    {
        int code = await market.RunAsync(s, tx =>
        {
            var listing = tx.Get<Listing>(Listings, req.SkuId);
            if (listing == null || listing.SellerId != s.Role!.Id || listing.Entry != req.ItemId)
                return ErrorCodes.TradeDelisted;
            if (req.Quantity > 0 && req.Quantity != listing.Quantity) return ErrorCodes.TradeSaleQuantity;
            var back = listing.Item.Clone(); back.Guid = 0; back.Grid = 0; back.Stack = listing.Quantity;
            tx.DeliverMail(listing.SellerId, 1066, [back], contentParams: [data.ItemName(back.Entry)]); // ${1}
            foreach (var order in tx.All<PreorderRow>(Orders).Where(o => o.Sku == listing.Sku && o.Status == 0))
            {
                tx.DeliverMail(order.BuyerId, 1065,
                    currency: new Dictionary<int, long> { [order.Currency] = (long)order.Quantity * order.Price },
                    contentParams: [data.ItemName(order.Entry)]);
                order.Status = (int)PreTradeStatusEnums.NotSelected;
                tx.Put(Orders, order.Id, order);
            }
            tx.Delete(Listings, listing.Sku);
            return 0;
        });
        var response = new TradePutOffItemResp { Result = code };
        if (code == 0) response.BoothInfos.AddRange((await OwnRows(s)).Select(Commodity));
        await s.SendGameAsync(PutOff, response);
    }

    private async Task ChangePrice(GameSession s, ChangeSkuPriceReq req)
    {
        Listing? changed = null;
        double old = 0;
        int price = double.IsFinite(req.SkuPrice) && req.SkuPrice == Math.Floor(req.SkuPrice)
            && req.SkuPrice <= int.MaxValue ? (int)req.SkuPrice : 0;
        int code = await market.RunAsync(s, tx =>
        {
            changed = null;
            old = 0;
            var listing = tx.Get<Listing>(Listings, req.SkuId);
            if (listing == null || listing.SellerId != s.Role!.Id) return ErrorCodes.TradeRepriceGone;
            if (tx.All<PreorderRow>(Orders).Any(o => o.Sku == listing.Sku && o.Status == 0))
                return ErrorCodes.TradeRequest; // (inferred) keep reserved quote/escrow coherent.
            var config = Eligible(listing.Entry);
            if (config == null || !ValidPrice(config.Value, price)) return ErrorCodes.TradePrice;
            old = listing.UnitPrice;
            listing.UnitPrice = price;
            tx.Put(Listings, listing.Sku, listing);
            changed = listing;
            return 0;
        });
        var response = new ChangeSkuPriceResp
        {
            Code = code, Errno = code,
            Errmsg = code == 0 ? "" : "Price change refused"
        };
        if (changed != null)
        {
            var rows = await OwnRows(s);
            var d = new SChangeSkuPriceResp
            {
                SkuId = changed.Sku, SpuId = changed.Spu,
                ItemId = changed.Entry.ToString(CultureInfo.InvariantCulture),
                FinalPrice = changed.UnitPrice, PriceDiff = changed.UnitPrice - old,
                SkuPrice = changed.UnitPrice, CommodityInfo = Commodity(changed),
                RecommendedPrice = Eligible(changed.Entry) is { } row ? PriceRule(row).Base : changed.UnitPrice,
            };
            d.BoothInfos.AddRange(rows.Select(Commodity)); response.Data = d;
        }
        await s.SendGameAsync(Reprice, response);
    }

    private async Task<List<Listing>> OwnRows(GameSession s)
    {
        var result = new List<Listing>();
        await market.RunAsync(s, tx => { result = Active(tx).Where(l => l.SellerId == s.Role!.Id).ToList(); return 0; });
        return result;
    }

    private async Task Unlock(GameSession s)
    {
        DBUserTradeInfo? next = null;
        int code = await market.RunAsync(s, tx =>
        {
            int place = s.Trade!.StallsNum + 1;
            var row = data.Row("TradeConfigUnlockPlace", place);
            if (row == null) return ErrorCodes.TradeStallFull;
            var spend = row.Value.GetProperty("_kSpend");
            int currency = spend.GetArrayLength() >= 2 ? (int)(GameData.Number(spend[0]) ?? 0) : 0;
            int amount = spend.GetArrayLength() >= 2 ? (int)(GameData.Number(spend[1]) ?? 0) : 0;
            if (currency > 0 && amount > 0)
            {
                var owner = tx.Role(s.Role!.Id);
                if (owner.Currency.GetValueOrDefault(currency) < amount) return ErrorCodes.TradeCurrencyShort;
                owner.Currency[currency] -= amount;
                tx.Economy.Sink(new(EconomyAssetKind.Currency, currency), amount, EconomyReason.MarketSlotUnlock,
                    EconomyCompartment.Currency, new(EconomyOwnerKind.Role, owner.RoleId.ToString(CultureInfo.InvariantCulture)));
            }
            next = s.Trade.Clone(); next.StallsNum = place;
            tx.SaveBlob(s.Role!.Id, "trade_info", next.ToByteArray());
            return 0;
        });
        await s.SendGameAsync(UnlockSlot, new TradeUnLockCubeResp { Result = code });
    }

    private async Task Attention(GameSession s, TradeAttionReq req)
    {
        DBUserTradeInfo? next = null;
        int code = await market.RunAsync(s, tx =>
        {
            if (req.OpType is not (1 or 2) || req.Id.Length == 0) return ErrorCodes.TradeRequest;
            next = s.Trade!.Clone();
            bool add = req.OpType == 1;
            long now = NowMs;
            if (req.AttionType == 1)
            {
                var care = next.AttentionItemTypes.GetValueOrDefault(req.Key) ?? new TradeCareInfo();
                if (add) care.ItemIdList[req.Id] = now;
                else care.ItemIdList.Remove(req.Id);
                if (care.ItemIdList.Count == 0) next.AttentionItemTypes.Remove(req.Key);
                else next.AttentionItemTypes[req.Key] = care;
            }
            else if (req.AttionType == 2)
            {
                if (add) next.AttentionUniqueItems[req.Id] = now;
                else next.AttentionUniqueItems.Remove(req.Id);
            }
            else return ErrorCodes.TradeRequest;
            int count = next.AttentionUniqueItems.Count + next.AttentionItemTypes.Values.Sum(c => c.ItemIdList.Count);
            if (count > Public("TradeConcernNum", 15)) return ErrorCodes.TradeQuantity;
            tx.SaveBlob(s.Role!.Id, "trade_info", next.ToByteArray());
            return 0;
        });
        await s.SendGameAsync(Follow, new TradeAttionResp
        { Result = code, OpType = req.OpType, AttionType = req.AttionType, Id = req.Id });
    }

    private async Task RefreshMarket(GameSession s)
    {
        DBUserTradeInfo? next = null;
        int code = await market.RunAsync(s, tx =>
        {
            long now = NowMs;
            if (now - s.Trade!.LastClientRefreshTime < Public("TradeRefreshCd", 300) * 1000)
                return ErrorCodes.TradeRefreshCooldown;
            next = s.Trade.Clone(); next.LastRefreshTime = now; next.LastClientRefreshTime = now;
            tx.SaveBlob(s.Role!.Id, "trade_info", next.ToByteArray());
            return 0;
        });
        await s.SendGameAsync(Refresh, new RefreshTradeMarketResp { Result = code });
    }

}
