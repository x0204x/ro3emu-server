using System.Globalization;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

public sealed partial class TradeService
{
    /// <summary>
    /// (inferred) Publicity orders reserve the exact quoted payment. At the config's minimum hide time,
    /// FIFO orders at the listed price consume escrow; nonwinners receive mail template 1065 with a refund.
    /// Original winner selection and credit-point costs are unavailable in the client extraction.
    /// </summary>
    private async Task Reserve(GameSession s, CPreTradeTopRequest req)
    {
        long saleMs = 0;
        int code = await market.RunAsync(s, tx =>
        {
            saleMs = 0;
            if (!functions.IsOpen(s, 2)) return ErrorCodes.FunctionLocked;
            if (req.TotalQuantity < 1 || req.Price < 1 || (long)req.Price * req.TotalQuantity != req.TotalPrice)
                return ErrorCodes.TradeRequest;
            var listing = tx.Get<Listing>(Listings, req.SkuId);
            if (listing == null || listing.PublicUntilMs <= NowMs || listing.Entry != req.ItemId
                || listing.Spu != req.SpuId || listing.UnitPrice != req.Price || listing.SellerId == s.Role!.Id)
                return ErrorCodes.TradeRequest;
            if (req.TotalQuantity > listing.Quantity || data.Row("TradeConfig", req.ItemId) is not { } row
                || req.TotalQuantity > GameData.Int(row, "_iBuyLimit"))
                return ErrorCodes.TradeQuantity;
            if (tx.All<PreorderRow>(Orders).Count(o => o.BuyerId == s.Role!.Id && o.Status == 0)
                >= Public("TradePreMaxNum", 30)) return ErrorCodes.TradeQuantity;
            var buyer = tx.Role(s.Role!.Id);
            if (buyer.Currency.GetValueOrDefault(listing.Currency) < req.TotalPrice) return ErrorCodes.CurrencyShort;
            buyer.Currency[listing.Currency] -= req.TotalPrice;
            // A durable counter, allocated under the market gate, preserves reservation order
            // even when several requests share the same clock millisecond (inferred FIFO policy).
            long sequence = tx.Get<long>("trade_sequence", "preorder") + 1;
            tx.Put("trade_sequence", "preorder", sequence);
            var order = new PreorderRow
            {
                Id = NewId(), Sku = listing.Sku, Spu = listing.Spu, BuyerId = buyer.RoleId,
                Entry = listing.Entry, Quantity = req.TotalQuantity, Price = req.Price,
                Currency = listing.Currency, Item = listing.Item.Clone(), TimeMs = NowMs,
                Sequence = sequence,
            };
            tx.Put(Orders, order.Id, order);
            saleMs = listing.PublicUntilMs;
            return 0;
        });
        await s.SendGameAsync(Preorder, new CPreTradeTopResponse { Result = code });
        // 32622 skuQueueNtf: LC_DAL_Trade._notifyInQueue (lua:2151-2384) pops UIWords 21090 "You have joined the Lineup
        // to purchase this item. In ${1} seconds, a random player from the Lineup will be selected..." counting down to
        // saleTime (seconds, ignored once past) and closes itself then. The lineup is this pre-purchase (21172 "one buyer
        // will be randomly selected from all purchase Lineups"); pushing it on the accepted 32626 is (inferred).
        if (code == 0 && saleMs > NowMs)
            await s.SendGameAsync(InQueue, new skuQueueNtf
            {
                ItemId = req.ItemId, Price = req.Price, Quantity = req.TotalQuantity,
                SaleTime = (saleMs + 999) / 1000, SkuId = req.SkuId,
            });
    }

    private CPreTradeTopOrderDTO OrderInfo(PreorderRow o, Listing? listing)
    {
        var result = new CPreTradeTopOrderDTO
        {
            PreTradeId = o.Id, UserRoleId = o.BuyerId.ToString(CultureInfo.InvariantCulture),
            ItemId = o.Entry.ToString(CultureInfo.InvariantCulture), SkuId = o.Sku,
            Quantity = o.Quantity, Price = o.Price, CreateTime = o.TimeMs,
            Status = (PreTradeStatusEnums)o.Status, SaleBeginTime = listing?.PublicUntilMs ?? 0,
            ItemBaseInfo = ByteString.CopyFrom(character.ItemInfoOf(o.Item).ToByteArray()),
        };
        return result;
    }

    private async Task Preorders(GameSession s, CPreTradeTopQueryRequest req)
    {
        var result = new CPreTradeTopQueryPage { PageNo = Page(req.PageNo), PageSize = Size(req.PageSize) };
        await market.RunAsync(s, tx =>
        {
            result.TotalCount = 0;
            result.PreTradeOrder.Clear();
            var rows = tx.All<PreorderRow>(Orders).Where(o => o.BuyerId == s.Role!.Id && !o.Hidden)
                .OrderByDescending(o => o.TimeMs).ToList();
            result.TotalCount = rows.Count;
            result.PreTradeOrder.AddRange(rows.Skip((result.PageNo - 1) * result.PageSize).Take(result.PageSize)
                .Select(o => OrderInfo(o, tx.Get<Listing>(Listings, o.Sku))));
            return 0;
        });
        await s.SendGameAsync(QueryPreorders, new CPreTradeTopQueryResponse { Data = result });
    }

    private async Task Refund(GameSession s, CRefundPreTradeTopRequest req)
    {
        var refunded = new List<PreorderRow>();
        int code = await market.RunAsync(s, tx =>
        {
            refunded.Clear();
            if (req.PreTradeOrderIds.Count == 0
                || req.PreTradeOrderIds.Distinct(StringComparer.Ordinal).Count() != req.PreTradeOrderIds.Count)
                return ErrorCodes.TradeRequest;
            foreach (string id in req.PreTradeOrderIds)
            {
                var order = tx.Get<PreorderRow>(Orders, id);
                if (order == null || order.BuyerId != s.Role!.Id || order.Hidden) return ErrorCodes.TradeRequest;
                if (req.IsReward)
                {
                    // (inferred) "receive" acknowledges already-settled failed/offline rows.
                    // Their refund was attached during settlement, so no second payment is issued.
                    if (order.Status != (int)PreTradeStatusEnums.NotSelected
                        && order.Status != (int)PreTradeStatusEnums.Offline)
                        return ErrorCodes.TradeRequest;
                }
                else if (order.Status != (int)PreTradeStatusEnums.Init) return ErrorCodes.TradeRequest;
                refunded.Add(order);
            }
            foreach (var order in refunded)
            {
                if (!req.IsReward)
                {
                    tx.DeliverMail(order.BuyerId, 1065,
                        currency: new Dictionary<int, long> { [order.Currency] = (long)order.Quantity * order.Price },
                        contentParams: [data.ItemName(order.Entry)]);
                    order.Status = (int)PreTradeStatusEnums.Refunded;
                }
                order.Hidden = true;
                tx.Put(Orders, order.Id, order);
            }
            return 0;
        });
        var response = new CRefundPreTradeTopResponse
        {
            Code = code, Errno = code,
            Errmsg = code == 0 ? "" : "Refund refused"
        };
        if (code == 0)
        {
            response.Data = new CRefundPreTradeTopDTO { IsReward = req.IsReward };
            response.Data.Orders.AddRange(refunded.Select(o => OrderInfo(o, null)));
        }
        await s.SendGameAsync(RefundPreorder, response);
    }

    /// <summary>Advance publicity and timed listings. The host also invokes this once per second.</summary>
    public Task TickAsync(GameSession s) => TickMaintenanceAsync();

    // Stage G: every trade message and the 1 s stall maintenance call this. As a full market transaction each time it
    // put two transactions on every trade request (p50 0.5-0.9 s at 500 players). It now runs only when a listing can
    // have matured or expired: a market commit since the last pass, or the earliest due time reached.
    private long _maintainedVersion = -1, _nextDueMs;
    internal long MaintenancePasses { get; private set; }

    internal async Task TickMaintenanceAsync()
    {
        long version = market.Version;
        if (version == _maintainedVersion && NowMs < _nextDueMs) return;
        MaintenancePasses++;
        long due = long.MaxValue;
        bool wrote = false;
        var expiredCount = new Dictionary<long, int>();
        var successfulPreorders = new HashSet<long>();
        var failedPreorders = new HashSet<long>();
        int code = await market.RunMaintenanceAsync(tx =>
        {
            due = long.MaxValue;
            wrote = false;
            expiredCount.Clear();
            successfulPreorders.Clear();
            failedPreorders.Clear();
            long now = NowMs;
            var matured = Active(tx).Where(l => l.PublicUntilMs is > 0 && l.PublicUntilMs <= now).ToList();
            var allOrders = tx.All<PreorderRow>(Orders);
            foreach (var listing in matured)
            {
                foreach (var order in allOrders.Where(o => o.Sku == listing.Sku && o.Status == 0)
                    .OrderBy(o => o.Sequence).ThenBy(o => o.TimeMs).ThenBy(o => o.Id))
                {
                    int take = Math.Min(order.Quantity, listing.Quantity);
                    if (take > 0)
                    {
                        var item = listing.Item.Clone(); item.Guid = 0; item.Stack = take; item.Grid = 0;
                        long gross = (long)take * order.Price;
                        var sale = new Sale
                        {
                            Id = NewId(), Sku = listing.Sku, Spu = listing.Spu, Entry = listing.Entry,
                            SellerId = listing.SellerId, BuyerId = order.BuyerId, Quantity = take,
                            UnitPrice = order.Price, TimeMs = now, Item = item,
                        };
                        tx.DeliverMail(order.BuyerId, 1064, [item], contentParams: SaleMailParams(sale, gross, false));
                        long fee = MarketFee(gross, listing.Currency);
                        if (fee > 0) tx.Economy.Sink(new(EconomyAssetKind.Currency, listing.Currency), fee, EconomyReason.MarketTax,
                            EconomyCompartment.TradePreorder, new(EconomyOwnerKind.Role, order.BuyerId.ToString(CultureInfo.InvariantCulture)));
                        tx.DeliverMail(listing.SellerId, 1063,
                            currency: new Dictionary<int, long> { [listing.Currency] = gross - fee },
                            contentParams: SaleMailParams(sale, gross, true));
                        tx.Put(History, sale.Id, sale);
                        listing.Quantity -= take; listing.Item.Stack -= take;
                        successfulPreorders.Add(order.BuyerId);
                    }
                    int refund = order.Quantity - take;
                    if (refund > 0) tx.DeliverMail(order.BuyerId, 1065,
                        currency: new Dictionary<int, long> { [order.Currency] = (long)refund * order.Price },
                        contentParams: [data.ItemName(order.Entry)]);
                    if (take == 0) failedPreorders.Add(order.BuyerId);
                    order.Status = take > 0 ? (int)PreTradeStatusEnums.Success : (int)PreTradeStatusEnums.NotSelected;
                    tx.Put(Orders, order.Id, order);
                }
                listing.PublicUntilMs = 0;
                if (listing.Quantity == 0) tx.Delete(Listings, listing.Sku);
                else tx.Put(Listings, listing.Sku, listing);
            }
            // (inferred) PublicParam listing-display times are hours. Remaining unsold escrow
            // expires into the current exchange return template and can be claimed once.
            foreach (var listing in Active(tx).Where(l => l.ExpiresAtMs is > 0 && l.ExpiresAtMs <= now))
            {
                var back = listing.Item.Clone(); back.Guid = 0; back.Grid = 0; back.Stack = listing.Quantity;
                tx.DeliverMail(listing.SellerId, 1067, [back], contentParams: [data.ItemName(back.Entry)]);
                expiredCount[listing.SellerId] = expiredCount.GetValueOrDefault(listing.SellerId) + 1;
                foreach (var order in tx.All<PreorderRow>(Orders).Where(o => o.Sku == listing.Sku && o.Status == 0))
                {
                    tx.DeliverMail(order.BuyerId, 1065,
                        currency: new Dictionary<int, long> { [order.Currency] = (long)order.Quantity * order.Price },
                        contentParams: [data.ItemName(order.Entry)]);
                    order.Status = (int)PreTradeStatusEnums.NotSelected;
                    tx.Put(Orders, order.Id, order);
                    failedPreorders.Add(order.BuyerId);
                }
                tx.Delete(Listings, listing.Sku);
            }
            wrote = matured.Count > 0 || expiredCount.Count > 0;
            foreach (var listing in Active(tx))
            {
                if (listing.PublicUntilMs > 0) due = Math.Min(due, listing.PublicUntilMs);
                if (listing.ExpiresAtMs > 0) due = Math.Min(due, listing.ExpiresAtMs);
            }
            return 0;
        }, _ => NotifyTradeAsync(expiredCount, successfulPreorders, failedPreorders));
        if (code != 0) return;
        // A commit by anyone else meanwhile leaves the version ahead of this, so the next call passes again.
        _maintainedVersion = version + (wrote ? 1 : 0);
        _nextDueMs = due;
    }

    private async Task NotifyTradeAsync(IReadOnlyDictionary<long, int> expired,
        IReadOnlySet<long> succeeded, IReadOnlySet<long> failed)
    {
        // 32630 is a client reminder/red-dot invalidation; the durable mail remains authoritative.
        foreach (var session in sessions.All())
        {
            if (session.Role is not { } role) continue;
            var notice = new TradeCommonNtf();
            if (expired.TryGetValue(role.Id, out int count)) notice.ExpireNtf = new ExpiredItemsNtf { ExpiredCount = count };
            if (succeeded.Contains(role.Id)) notice.PreTradeSuccessNtf = new PreTradeSuccessNtf();
            if (failed.Contains(role.Id)) notice.PreTradeNtf = new PreTradeFailNtf();
            if (notice.ExpireNtf == null && notice.PreTradeSuccessNtf == null && notice.PreTradeNtf == null) continue;
            try { await session.SendGameAsync(32630, notice); }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
            { log.LogDebug(e, "Trade reminder recipient disconnected after commit"); }
        }
    }
}
