using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

public sealed partial class AuctionService
{
    private int BidError(GameSession s, AuctionStore.Item? row, int price, BiddingType op, long guildId)
    {
        if (s.Role == null || s.Progress == null || row == null) return ErrorCodes.ItemMissing;
        if (!Visible(s, row) || guildId != 0 && row.GuildId != guildId)
            return row.Type == AuctionType.Guild ? ErrorCodes.AuctionGuild : ErrorCodes.NoPermission;
        if (row.State is not (AuctionState.Bidding or AuctionState.Public) || Now >= row.End
            || row.GiveUp.Contains(s.Role.Id)) return ErrorCodes.AuctionNotForSale;
        if (op is not (BiddingType.Fixed or BiddingType.Manual or BiddingType.Auto)) return ErrorCodes.AuctionParameter;
        if (row.State == AuctionState.Public && (op != BiddingType.Auto || price >= row.FixedPrice))
            return ErrorCodes.AuctionParameter;
        if (price <= 0 || (op == BiddingType.Fixed && price != row.FixedPrice)) return ErrorCodes.AuctionParameter;
        if (price > row.MaxPrice) return ErrorCodes.AuctionMaxPrice;
        int min = row.Owner == 0 ? row.Price : checked(row.Price + Math.Max(1, row.Raise));
        if (op != BiddingType.Fixed && price < min && !(row.Owner == s.Role.Id && price == row.Reserve))
            return ErrorCodes.AuctionPriceChanged;
        if (row.Owner == s.Role.Id && price < row.Reserve) return ErrorCodes.AuctionPriceChanged;
        int priorPublic = row.State == AuctionState.Public
            ? store.Prebuys().FirstOrDefault(p => p.Role == s.Role.Id && p.RequestedId == row.Id)?.UnitPrice ?? 0 : 0;
        long debit = row.State == AuctionState.Public ? Math.Max(0, price - priorPublic)
            : row.Owner == s.Role.Id ? Math.Max(0, price - row.Reserve) : price;
        return character.CountOf(s, row.Currency) < debit ? ErrorCodes.AuctionCurrencyShort : 0;
    }

    private BiddingAuctionRes BidResponse(GameSession s, AuctionStore.Item? row, BiddingType op, int extra, int result)
    {
        bool allowed = row != null && Visible(s, row);
        return new BiddingAuctionRes
        {
            Result = result, AuctionId = row?.Id ?? 0, OpType = op, ExtraPrice = extra,
            AuctionType = allowed ? row!.Type : AuctionType.Tnone,
            EndTime = allowed ? ClientEnd(row!) : 0, CurPrice = allowed ? row!.Price : 0,
            AuctionItem = allowed ? DbView(row!, s.Role!.Id) : null,
            BiddingAuctionItem = allowed ? View(s, row!) : null,
        };
    }

    private static DBAuctionItem DbView(AuctionStore.Item row, long viewer)
    {
        var item = new DBAuctionItem
        {
            AuctionId = row.Id, AuctionType = row.Type, GuildId = row.GuildId,
            SrcType = row.SrcType, ItemId = row.Entry, ItemPrice = row.Price, StartTime = row.Start,
            EndTime = row.End, State = row.State, OwnerId = row.Owner, LastBiddingTime = row.LastBid
        };
        if (row.Auto && row.Owner == viewer) item.CurMaxAutoBidding = new DBAutoBidding
        { RoleId = row.Owner, MaxPrice = row.Reserve, BiddingTime = row.LastBid, IsPaused = row.AutoPaused };
        foreach (long role in row.Joined) item.JoinBiddingRoleIds[role] = role;
        if (row.Type == AuctionType.Dungeon) item.DungeonaucData = new DungeonAuctionData
        { QuUniqueId = row.RunId, DungeonBeginTime = row.Start, GiveupUsers = { row.GiveUp } };
        return item;
    }

    public async Task BidAsync(GameSession s, BiddingAuctionReq req)
    {
        long requestRole = s.Role?.Id ?? 0;
        var requestLease = s.Attachment;
        var result = await BidCoreAsync(s, req.AuctionId, req.Price, req.OpType, req.GuildId, req.CheckOnly, req.ExtraPrice);
        await SendSessionAuctionAsync(s, requestRole, requestLease, Bid, result);
    }

    private async Task<BiddingAuctionRes> BidCoreAsync(GameSession s, long id, int price, BiddingType op, long guildId,
        bool checkOnly, int extra)
    {
        if (s.Role == null || s.Progress == null) return new BiddingAuctionRes { Result = ErrorCodes.Parameter, AuctionId = id };
        long requestRole = s.Role.Id;
        var requestLease = s.Attachment;
        bool committed = false;
        AuctionStore.Item? changed = null;
        long outbid = 0;
        long[] mailRecipients = [];
        bool syncPersonal = false, paidItems = false;
        Func<Task>? itemsNotice = null;
        long progressRevision = 0, inventoryRevision = 0;
        BiddingAuctionRes response;
        try
        {
            using (var scope = await EnterSessionAuctionAsync(s, "auction/bid", role =>
                RowKeys(store.Get(id), [role], store.Prebuys().Where(p => p.Role == role && p.RequestedId == id))))
            using (var state = await CharacterService.Lock(s, scope.Context))
            {
                var row = store.Get(id);
                int error = BidError(s, row, price, op, guildId);
                if (error != 0) return BidResponse(s, row, op, extra, error);
                if (checkOnly) return BidResponse(s, row, op, extra, 0);
                if (row!.State == AuctionState.Public)
                {
                    var publicBid = await QueuePublicBidLockedAsync(s, row, price, op, extra, scope, () => committed = true);
                    if (publicBid.Response.Result != 0 || !publicBid.Paid) return publicBid.Response;
                    response = publicBid.Response;
                    committed = paidItems = syncPersonal = true;
                    if (publicBid.Refund) mailRecipients = [s.Role.Id];
                }
                else
                {
                    long role = s.Role.Id;
                    if (row.Owner == role && row.Reserve == price && row.Auto == (op == BiddingType.Auto))
                        return BidResponse(s, row, op, extra, 0);
                    long oldOwner = row.Owner;
                    int oldReserve = row.Reserve;
                    bool priorAutoWins = oldOwner != 0 && oldOwner != role && row.Auto && !row.AutoPaused
                        && op != BiddingType.Fixed && row.Reserve >= price;
                    if (priorAutoWins)
                    {
                        row.Price = Math.Min(row.Reserve, price + Math.Max(1, row.Raise));
                        row.LastBid = Now;
                        row.Revision++;
                        if (!row.Joined.Contains(role)) row.Joined.Add(role);
                        var transaction = roles.NewTransaction();
                        store.EnlistSave(row, transaction);
                        transaction.WithEscrowEconomy(AuctionIntent("auction/proxy-retain", role, s.Account ?? "")
                            .Evidence(new { Lot = LotEvidence(row), Bidder = role, OfferedPrice = price, Operation = op }));
                        RequireScope(scope, transaction);
                        await roles.CommitAsync(transaction, "auction/proxy-retain");
                        changed = row;
                        outbid = role;
                        committed = true;
                    }
                    else
                    {
                        int debit = oldOwner == role ? price - oldReserve : price;
                        var refunds = oldOwner != 0 && oldOwner != role ? new[] { oldOwner } : Array.Empty<long>();
                        long[] dividendRoles = op == BiddingType.Fixed ? DividendRoles(row) : [];
                        var next = System.Text.Json.JsonSerializer.Deserialize<AuctionStore.Item>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(row))!;
                        next.OriginalBytes = row.OriginalBytes is null ? null : (byte[])row.OriginalBytes.Clone();
                        next.SourceRevision = row.SourceRevision;
                        next.Owner = role;
                        next.Reserve = price;
                        next.Price = op == BiddingType.Fixed ? next.FixedPrice
                            : op == BiddingType.Auto ? oldOwner == role || oldOwner == 0 ? row.Price
                                : Math.Min(price, (row.Auto ? row.Reserve : row.Price) + Math.Max(1, row.Raise))
                            : price;
                        next.Auto = op == BiddingType.Auto;
                        next.AutoPaused = false;
                        next.LastBid = Now;
                        next.Revision++;
                        if (!next.Joined.Contains(role)) next.Joined.Add(role);
                        long countdown = Math.Max(0, Param("AuctionCountdown", 5));
                        if (next.End - Now <= countdown) next.End = checked(next.End + Math.Max(0, Param("AuctionExtendTime", 3)));
                        if (op == BiddingType.Fixed) { next.State = AuctionState.Settled; next.Winner = role; next.End = Now; }
                        var paid = await character.ExchangeLockedAsync(s, debit == 0 ? [] : [(next.Currency, debit)], [],
                            popup: false, sync: false, asyncCommit: true, committed: () => committed = true, persist: tx =>
                            {
                                store.EnlistSave(next, tx);
                                if (oldOwner != 0 && oldOwner != role) QueueOutbidLocked(next, oldOwner, oldReserve, tx);
                                if (op == BiddingType.Fixed) SettleLetters(next, dividendRoles, tx);
                                var economy = op == BiddingType.Fixed
                                    ? SettlementIntent("auction/fixed-buy", next, dividendRoles, role, s.Account ?? "",
                                        details: new
                                        {
                                            Before = LotEvidence(row), After = LotEvidence(next), Debit = debit,
                                            OutbidRole = oldOwner, OutbidRefund = oldReserve, Operation = op
                                        },
                                        walletDebit: debit)
                                    : AuctionIntent("auction/bid", role, s.Account ?? "")
                                        .Evidence(new
                                        {
                                            Before = LotEvidence(row), After = LotEvidence(next),
                                            Debit = debit, OutbidRole = oldOwner, OutbidRefund = oldReserve,
                                            Operation = op
                                        });
                                tx.WithEscrowEconomy(economy);
                                RequireScope(scope, tx);
                            });
                        if (paid == null) return BidResponse(s, row, op, extra, ErrorCodes.AuctionCurrencyShort);
                        changed = next;
                        outbid = refunds.FirstOrDefault();
                        mailRecipients = refunds.Append(role).Concat(dividendRoles).Distinct().ToArray();
                        paidItems = true;
                    }
                    response = BidResponse(s, changed, op, extra, 0);
                }
                if (paidItems)
                {
                    itemsNotice = character.CaptureEconomicItemsNotice(s);
                    progressRevision = s.Owner?.Revisions.GetValueOrDefault(RevisionStore.Progress) ?? 0;
                    inventoryRevision = s.Owner?.Revisions.GetValueOrDefault(RevisionStore.Inventory) ?? 0;
                }
            }
            if (committed && BeforeNoticeForTest is { } beforeNotice) await beforeNotice();
            if (itemsNotice != null && s.Role?.Id == requestRole && ReferenceEquals(s.Attachment, requestLease)
                && requestLease?.Cancelled != true
                && (s.Owner?.Revisions.GetValueOrDefault(RevisionStore.Progress) ?? 0) == progressRevision
                && (s.Owner?.Revisions.GetValueOrDefault(RevisionStore.Inventory) ?? 0) == inventoryRevision)
                await itemsNotice();
            foreach (long recipient in mailRecipients) await mail.NotifyRecipientAsync(recipient);
            if (changed != null) await ChangedAsync(changed, outbid);
            if (changed?.State == AuctionState.Settled) await SettlementNoticesAsync(changed);
            if (syncPersonal && s.Role?.Id == requestRole && ReferenceEquals(s.Attachment, requestLease)
                && requestLease?.Cancelled != true) await SyncPersonalAsync(requestRole);
            return response;
        }
        catch (Exception e)
        {
            log.LogError(e, "Auction bid for {Auction} failed (committed before the failure: {Committed})", id, committed);
            return BidResponse(s, store.Get(id), op, extra, committed ? 0 : ErrorCodes.Unknown);
        }
    }

    private async Task<(BiddingAuctionRes Response, bool Paid, bool Refund)> QueuePublicBidLockedAsync(GameSession s, AuctionStore.Item row,
        int price, BiddingType op, int extra, Ownership.LegacyOwnerScope scope, Action committed)
    {
        var pending = store.Prebuys();
        var prior = pending.FirstOrDefault(p => p.Role == s.Role!.Id && p.RequestedId == row.Id);
        if (prior?.UnitPrice == price) return (BidResponse(s, row, op, extra, 0), false, false);
        int delta = price - (prior?.UnitPrice ?? 0);
        var order = new AuctionStore.Prebuy(prior?.Id ?? 0, s.Role!.Id, row.Type, row.SrcType, row.Entry,
            1, price, row.Currency, (prior?.Revision ?? 0) + 1, false, row.Id, false)
        { Original = prior?.Original, SourceRevision = prior?.SourceRevision };
        byte[]? oldPersonal = roles.LoadAuction(s.Role.Id);
        var personal = oldPersonal is { } saved ? DBAuction.Parser.ParseFrom(saved) : new DBAuction();
        personal.PreBuyNum[PrebuyKey(row.Type, row.SrcType, row.Entry)] = pending.Count(p => p.Role == s.Role.Id
            && p.Type == row.Type && p.SrcType == row.SrcType && p.Entry == row.Entry) + (prior == null ? 1 : 0);
        var paid = await character.ExchangeLockedAsync(s, delta > 0 ? [(row.Currency, delta)] : [], [],
                popup: false, sync: false, asyncCommit: true, committed: committed, persist: tx =>
                {
                    store.EnlistPrebuy(order, tx);
                    EnlistPersonal(s.Role.Id, oldPersonal, personal, tx);
                    if (delta < 0)
                        mail.QueueTemplateLocked(s.Role.Id, $"prebuy:{prior!.Id}:revise:{order.Revision}", 1040,
                            [new MailService.Attachment(row.Currency, -delta)], tx, data.ItemName(row.Entry));
                    tx.WithEscrowEconomy(AuctionIntent("auction/public-prebuy", s.Role.Id, s.Account ?? "")
                        .Evidence(new
                        {
                            Lot = LotEvidence(row), order.Id, order.Revision, order.UnitPrice,
                            PreviousUnitPrice = prior?.UnitPrice ?? 0, Debit = delta
                        }));
                    RequireScope(scope, tx);
                });
        if (paid == null) return (BidResponse(s, row, op, extra, ErrorCodes.AuctionCurrencyShort), false, false);
        return (BidResponse(s, row, op, extra, 0), true, delta < 0);
    }

    /// <summary>82002 is the outbid notice: LC_DAL_Auction._updateSingleAuctionInfo pushes ChatConfig 5307
    /// ("You've been outbid") for any listed lot it names, without an owner check, so only the role that just lost
    /// the lead receives it. Other viewers refresh through the trade panel's QUERY_AUCTION polling. The dungeon
    /// panel's live row (82xxx DungeonAuctionItemChange) still goes to every eligible viewer.</summary>
    private async Task ChangedAsync(AuctionStore.Item row, long outbid = 0)
    {
        // A slow preceding notice must not publish an older lot after a newer bid has committed.
        if (store.Get(row.Id)?.SourceRevision != row.SourceRevision) return;
        var sends = new List<Task>();
        foreach (var s in sessions.All().Where(s => s.Role != null && Visible(s, row)))
        {
            long recipient = s.Role!.Id;
            var lease = s.Attachment;
            try
            {
                if (outbid != 0 && recipient == outbid && row.Owner != outbid
                    && AuctionRequestCurrent(s, recipient, lease))
                    sends.Add(ObserveAuctionNotice(s.SendGameAsync(Changed, new AuctionItemChangeNotify
                    {
                        AuctionType = row.Type, AuctionId = row.Id, SrcType = row.SrcType, ItemPrice = row.Price,
                        State = row.State, OwnerId = row.Owner, ItemId = row.Entry
                    }), recipient));
                if (row.Type == AuctionType.Dungeon && AuctionRequestCurrent(s, recipient, lease))
                    sends.Add(ObserveAuctionNotice(s.SendGameAsync(DungeonChanged, new DungeonAuctionItemChangeNotify
                    {
                        AuctionId = row.Id, ItemPrice = row.Price, OwnerId = row.Owner, EndTime = ClientEnd(row),
                        IsFix = row.State == AuctionState.Settled, LastBiddingTime = row.LastBid
                    }), recipient));
            }
            catch (Exception e) { log.LogWarning(e, "Auction price notification failed for {Role}", recipient); }
        }
        await Task.WhenAll(sends);
    }

    private async Task ObserveAuctionNotice(Task send, long recipient)
    {
        try { await send; }
        catch (Exception e) { log.LogWarning(e, "Auction price notification failed for {Role}", recipient); }
    }
}
