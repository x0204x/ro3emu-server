using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

public sealed partial class AuctionService
{
    private static string PrebuyKey(AuctionType type, int src, int entry) => $"{(int)type}:{src}:{entry}";

    /// <summary>
    /// The 82014 sender supplies explicit row IDs and AUTO max prices even in Public. BidCore reserves those prices
    /// on each row; PreBuyNum is the durable UI count only. Public reservations activate when Start arrives.
    /// The original server's exact public conflict ordering is unrecovered; existing proxy priority is (inferred).
    /// </summary>
    private async Task<int> SetPrebuyAsync(GameSession s, AuctionType type, int src, int entry, long target)
    {
        if (s.Role == null || entry <= 0 || target < 0 || target > Math.Max(1, Param("AUCTION_BATCH_NUM", 100)))
            return ErrorCodes.AuctionParameter;
        src = RowSrc(type, src);
        long requestRole = s.Role.Id;
        var requestLease = s.Attachment;
        int? committedResult = null;
        try
        {
            int actual;
            DBAuction personal;
            using (var scope = await EnterSessionAuctionAsync(s, "auction/prebuy-count", role => RoleKeys([role])))
            {
                actual = store.Prebuys().Count(p => p.Role == s.Role.Id && p.Type == type
                    && p.SrcType == src && p.Entry == entry);
                byte[]? before = roles.LoadAuction(s.Role.Id);
                personal = before is { } saved ? DBAuction.Parser.ParseFrom(saved) : new DBAuction();
                personal.PreBuyNum[PrebuyKey(type, src, entry)] = actual;
                var transaction = roles.NewTransaction();
                EnlistPersonal(s.Role.Id, before, personal, transaction);
                transaction.WithEscrowEconomy(AuctionIntent("auction/prebuy-count", s.Role.Id, s.Account ?? "")
                    .Evidence(new { Type = type, SrcType = src, Entry = entry, Requested = target, Actual = actual }));
                RequireScope(scope, transaction);
                await roles.CommitAsync(transaction, "auction/prebuy-count");
                committedResult = actual == target ? 0 : ErrorCodes.AuctionPriceChanged;
            }
            if (s.Role?.Id == requestRole && ReferenceEquals(s.Attachment, requestLease)
                && requestLease?.Cancelled != true)
                await CharacterService.SyncAsync(s, Sync, personal.Clone());
            return committedResult.Value;
        }
        catch (Exception e)
        {
            log.LogError(e, "Auction prebuy count for {Role} failed (committed before the failure: {Committed})",
                s.Role.Id, committedResult.HasValue);
            return committedResult ?? ErrorCodes.Unknown;
        }
    }

    internal async Task ProcessPrebuysAsync()
    {
        if (!loop.IsOnLoop) { await loop.InvokeAsync(ProcessPrebuysAsync); return; }
        foreach (long id in store.Prebuys().Select(order => order.Id).ToArray())
            await ProcessPrebuyAsync(id);
        foreach (long id in store.Items().Where(row => row.State == AuctionState.Public && row.Start <= Now)
            .Select(row => row.Id).ToArray())
        {
            AuctionStore.Item? opened = null;
            using (var scope = await EnterAuctionAsync("auction/public-open", () => RowKeys(store.Get(id))))
            {
                var row = store.Get(id);
                if (row is not { State: AuctionState.Public } || row.Start > Now) continue;
                row.State = AuctionState.Bidding;
                var transaction = roles.NewTransaction();
                store.EnlistSave(row, transaction);
                transaction.WithEscrowEconomy(AuctionIntent("auction/public-open")
                    .Evidence(new { Lot = LotEvidence(row) }));
                RequireScope(scope, transaction);
                await roles.CommitAsync(transaction, "auction/public-open");
                opened = row;
            }
            if (opened != null) await ChangedAsync(opened);
        }
    }

    private (AuctionStore.Prebuy? Order, AuctionStore.Item? Row, bool Wait) PrebuyStage(long id)
    {
        var order = store.Prebuys().FirstOrDefault(p => p.Id == id);
        if (order == null) return (null, null, false);
        var requested = store.Get(order.RequestedId);
        if (requested != null && requested.Start > Now && requested.End > Now) return (order, null, true);
        var scoped = store.Items().Where(r => r.Type == order.Type && r.SrcType == order.SrcType
            && r.Entry == order.Entry && r.State != AuctionState.Settled && r.End > Now).ToArray();
        var matching = scoped.Where(r => r.Start <= Now).ToArray();
        if (matching.Length == 0 && scoped.Any(r => r.Start > Now)) return (order, null, true);
        var player = Online(order.Role);
        if (player?.Auction == null) player = null;
        if (player == null || order.Paused) return (order, null, false);
        var row = matching.Where(r => Visible(player, r) && !r.GiveUp.Contains(order.Role)
            && r.Owner != order.Role && (long)order.UnitPrice >= (long)r.Price
                + (r.Owner == 0 ? 0 : Math.Max(1, r.Raise)))
            .OrderBy(r => r.Price).ThenBy(r => r.Id).FirstOrDefault();
        return (order, row, false);
    }

    private async Task ProcessPrebuyAsync(long id)
    {
        AuctionStore.Item? changed = null;
        long oldOwner = 0, role = 0;
        bool accepted = false, refund = false;
        using (var scope = await EnterAuctionAsync("auction/process-prebuy", () =>
        {
            var (order, row, _) = PrebuyStage(id);
            return RowKeys(row, order == null ? [] : [order.Role], order == null ? [] : [order]);
        }))
        {
            var (order, row, wait) = PrebuyStage(id);
            if (order == null || wait) return;
            role = order.Role;
            var matching = store.Items().Where(r => r.Type == order.Type && r.SrcType == order.SrcType
                && r.Entry == order.Entry && r.State != AuctionState.Settled && r.End > Now && r.Start <= Now).ToArray();
            if (Online(role)?.Auction == null && matching.Length > 0 && !order.Paused)
            {
                var pause = roles.NewTransaction();
                store.EnlistPrebuy(order with { Paused = true }, pause);
                pause.WithEscrowEconomy(AuctionIntent("auction/prebuy-pause")
                    .Evidence(new { order.Id, order.Role, order.UnitPrice, order.Currency }));
                RequireScope(scope, pause);
                await roles.CommitAsync(pause, "auction/prebuy-pause");
                return;
            }
            if (matching.Length > 0 && order.Paused) return;
            oldOwner = row?.Owner ?? 0;
            int oldReserve = row?.Reserve ?? 0;
            object? priorLot = row == null ? null : LotEvidence(row);
            bool priorWins = row != null && oldOwner != 0 && row.Auto && !row.AutoPaused
                && row.Reserve >= order.UnitPrice;
            accepted = row != null && !priorWins;
            var transaction = roles.NewTransaction();
            store.EnlistPrebuy(order with { Processed = true, Revision = order.Revision + 1 }, transaction);
            byte[]? before = roles.LoadAuction(role);
            var personal = before is { } saved ? DBAuction.Parser.ParseFrom(saved) : new DBAuction();
            string countKey = PrebuyKey(order.Type, order.SrcType, order.Entry);
            if (personal.PreBuyNum.TryGetValue(countKey, out long remaining) && remaining > 0)
            {
                personal.PreBuyNum[countKey] = remaining - 1;
                EnlistPersonal(role, before, personal, transaction);
            }
            if (row != null)
            {
                row.Revision++;
                row.LastBid = Now;
                if (!row.Joined.Contains(role)) row.Joined.Add(role);
                if (priorWins) row.Price = Math.Min(row.Reserve, order.UnitPrice + Math.Max(1, row.Raise));
                else
                {
                    row.Price = oldOwner == 0 ? row.Price
                        : Math.Min(order.UnitPrice, (row.Auto ? row.Reserve : row.Price) + Math.Max(1, row.Raise));
                    row.Owner = role;
                    row.Reserve = order.UnitPrice;
                    row.Auto = true;
                    row.AutoPaused = false;
                }
                store.EnlistSave(row, transaction);
            }
            if (oldOwner != 0 && accepted) QueueOutbidLocked(row!, oldOwner, oldReserve, transaction);
            refund = !accepted;
            if (refund)
                mail.QueueTemplateLocked(role, $"prebuy:{order.Id}:unused", 1040,
                    [new MailService.Attachment(order.Currency, order.UnitPrice)], transaction,
                    data.ItemName(order.Entry));
            transaction.WithEscrowEconomy(AuctionIntent("auction/process-prebuy", sourceKey: $"auction/prebuy/{order.Id}/process")
                .Evidence(new
                {
                    order.Id, order.Role, order.Entry, order.Currency, order.UnitPrice,
                    Accepted = accepted, Refund = refund, PriorLot = priorLot,
                    NewLot = row == null ? null : LotEvidence(row), OutbidRole = oldOwner,
                    OutbidRefund = accepted ? oldReserve : 0
                }));
            RequireScope(scope, transaction);
            await roles.CommitAsync(transaction, "auction/process-prebuy");
            changed = row;
        }
        if (changed != null) await ChangedAsync(changed, accepted ? oldOwner : 0);
        if (oldOwner != 0 && accepted) await mail.NotifyRecipientAsync(oldOwner);
        if (refund) await mail.NotifyRecipientAsync(role);
        await SyncPersonalAsync(role);
    }
}
