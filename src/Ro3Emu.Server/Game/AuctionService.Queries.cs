using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

public sealed partial class AuctionService
{
    public async Task CountAsync(GameSession s, QueryAuctionCountReq req)
    {
        long requestRole = s.Role?.Id ?? 0;
        var requestLease = s.Attachment;
        QueryAuctionCountRes res;
        using (var scope = await EnterSessionAuctionAsync(s, "auction/count-read", role => ReadKeys(role,
            store.OpenItems().Where(r => req.AuctionTypes.Contains((long)r.Type)
                && r.End > Now && Visible(s, r)))))
        {
            res = new QueryAuctionCountRes();
            var all = store.OpenItems();
            foreach (var type in req.AuctionTypes.Distinct())
            {
                if (!Enum.IsDefined(typeof(AuctionType), (int)type)) continue;
                var rows = all.Where(r => (long)r.Type == type
                    && r.End > Now && Visible(s, r)).ToArray();
                var counts = new QueryAuctionSrcTypeCount();
                foreach (var group in rows.GroupBy(r => (long)r.SrcType)) counts.SrcTypeCount[group.Key] = group.LongCount();
                foreach (var group in rows.GroupBy(r => GameData.Int(data.Row("AuctionConfig", r.Entry)!.Value, "_iType")))
                    counts.ItemTypeCount[group.Key] = group.LongCount();
                res.AuctionSrcTypeCount[type] = counts;
            }
        }
        await SendSessionAuctionAsync(s, requestRole, requestLease, Count, res);
    }

    public async Task PreStackAsync(GameSession s, PreAuctionItemStackQueryReq req)
    {
        long requestRole = s.Role?.Id ?? 0;
        var requestLease = s.Attachment;
        PreAuctionItemStackQueryRes res;
        using (var scope = await EnterSessionAuctionAsync(s, "auction/prestack-read", role => ReadKeys(role,
            store.OpenItems().Where(r => r.Type == req.AuctionType
                && (req.SrcType == 0 || r.SrcType == req.SrcType) && Visible(s, r)
                && (req.QueryItemTypes.Count == 0 || req.QueryItemTypes.Contains((int)GameData.Int(
                    data.Row("AuctionConfig", r.Entry)!.Value, "_iType")))),
            store.Prebuys().Where(p => p.Role == role))))
        {
            res = new PreAuctionItemStackQueryRes();
            var pending = store.Prebuys().Where(p => p.Role == s.Role!.Id).ToDictionary(p => p.RequestedId);
            foreach (var group in store.OpenItems().Where(r => r.Type == req.AuctionType
                && (req.SrcType == 0 || r.SrcType == req.SrcType) && Visible(s, r)
                && (req.QueryItemTypes.Count == 0 || req.QueryItemTypes.Contains((int)GameData.Int(data.Row("AuctionConfig", r.Entry)!.Value, "_iType"))))
                .GroupBy(r => (long)r.Entry))
            {
                var stack = new PreAuctionItemStack
                {
                    ItemId = group.Key,
                    OwnCount = group.LongCount(r => r.Owner == s.Role!.Id || pending.ContainsKey(r.Id)),
                    FirstEndTime = group.Any(r => r.State == AuctionState.Public)
                        ? group.Where(r => r.State == AuctionState.Public).Min(r => r.Start) : group.Min(r => r.End),
                    State = group.Any(r => r.State == AuctionState.Public)
                        ? AuctionState.Public : AuctionState.Bidding
                };
                string key = PrebuyKey(req.AuctionType, RowSrc(req.AuctionType, (int)req.SrcType), (int)group.Key);
                stack.PreBuyNum = s.Auction?.PreBuyNum.GetValueOrDefault(key) ?? 0;
                foreach (var row in group)
                {
                    bool queued = pending.TryGetValue(row.Id, out var reservation);
                    int selfBudget = row.Owner == s.Role!.Id && row.Auto ? row.Reserve
                        : queued ? reservation!.UnitPrice : 0;
                    stack.PreAuctionItem[row.Id] = new PreBiddingAuctionItem
                    {
                        AuctionId = row.Id, BiddingPrice = row.Price, OwnerId = row.Owner,
                        SelfIsAutoBidding = row.Owner == s.Role.Id && row.Auto || queued,
                        AutoBidding = selfBudget > 0 ? new DBAutoBidding
                        {
                            RoleId = s.Role.Id, MaxPrice = selfBudget,
                            BiddingTime = row.LastBid, IsPaused = row.AutoPaused
                        } : null
                    };
                }
                res.PreItems[group.Key] = stack;
            }
        }
        await SendSessionAuctionAsync(s, requestRole, requestLease, PreStack, res);
    }

    public async Task ResumeAsync(GameSession s)
    {
        long requestRole = s.Role?.Id ?? 0;
        var requestLease = s.Attachment;
        int result = s.Role == null ? ErrorCodes.Parameter : 0;
        if (result == 0)
        {
            bool committed = false;
            try
            {
                AuctionStore.Item[] rows;
                using (var scope = await EnterSessionAuctionAsync(s, "auction/resume-auto", role =>
                    store.OpenItems().Where(r => r.Owner == role && r.Auto && r.AutoPaused
                        && r.State == AuctionState.Bidding && r.End > Now)
                        .SelectMany(r => RowKeys(r)).Concat(RowKeys(null, [role],
                            store.Prebuys().Where(p => p.Role == role && p.Paused))).Distinct().ToArray()))
                {
                    rows = store.OpenItems().Where(r => r.Owner == s.Role!.Id && r.Auto && r.AutoPaused
                        && r.State == AuctionState.Bidding && r.End > Now).ToArray();
                    var orders = store.Prebuys().Where(p => p.Role == s.Role!.Id && p.Paused).ToArray();
                    if (rows.Length != 0 || orders.Length != 0)
                    {
                        var transaction = roles.NewTransaction();
                        foreach (var row in rows) { row.AutoPaused = false; store.EnlistSave(row, transaction); }
                        foreach (var order in orders) store.EnlistPrebuy(order with { Paused = false }, transaction);
                        transaction.WithEscrowEconomy(AuctionIntent("auction/resume-auto", s.Role!.Id, s.Account ?? "")
                            .Evidence(new
                            {
                                Lots = rows.Select(LotEvidence).ToArray(),
                                Orders = orders.Select(p => new { p.Id, p.Role, p.UnitPrice, p.Currency }).ToArray()
                            }));
                        RequireScope(scope, transaction);
                        await roles.CommitAsync(transaction, "auction/resume-auto");
                    }
                    committed = true;
                }
                foreach (var row in rows) await ChangedAsync(row);
            }
            catch (Exception e)
            {
                log.LogError(e, "Resume auto bidding failed (committed before the failure: {Committed})", committed);
                result = committed ? 0 : ErrorCodes.Unknown;
            }
        }
        await SendSessionAuctionAsync(s, requestRole, requestLease, Resume,
            new ResumeAutoBiddingAuctionRes { Result = result });
    }

    public async Task CancelAsync(GameSession s, CancelBiddingAuctionReq req)
    {
        long requestRole = s.Role?.Id ?? 0;
        var requestLease = s.Attachment;
        var (result, entry) = await CancelCoreAsync(s, req.AuctionId, req.GuildId, quit: false);
        await SendSessionAuctionAsync(s, requestRole, requestLease, Cancel,
            new CancelBiddingAuctionRes { Result = result, AuctionId = req.AuctionId, ItemId = entry });
    }

    public async Task QuitAsync(GameSession s, QuitAuctionReq req)
    {
        long requestRole = s.Role?.Id ?? 0;
        var requestLease = s.Attachment;
        var (result, entry) = await CancelCoreAsync(s, req.AuctionId, 0, quit: true);
        await SendSessionAuctionAsync(s, requestRole, requestLease, Quit,
            new QuitAuctionRes { Result = result, AuctionId = req.AuctionId, ItemId = entry });
    }

    private async Task<(int Code, int Entry)> CancelCoreAsync(GameSession s, long id, long guildId, bool quit)
    {
        if (s.Role == null) return (ErrorCodes.Parameter, 0);
        long requestRole = s.Role.Id;
        var requestLease = s.Attachment;
        AuctionStore.Item? changed = null;
        bool refund = false, syncPersonal = false;
        bool committed = false;
        int entry = 0;
        try
        {
            using (var scope = await EnterSessionAuctionAsync(s, "auction/cancel", role => RowKeys(store.Get(id), [role],
                store.Prebuys().Where(p => p.Role == role && p.RequestedId == id))))
            {
                var row = store.Get(id);
                if (row == null) return (ErrorCodes.ItemMissing, 0);
                object priorLot = LotEvidence(row);
                entry = row.Entry;
                var pending = store.Prebuys().FirstOrDefault(p => p.Role == s.Role.Id && p.RequestedId == id);
                if (pending != null)
                {
                    var transaction = roles.NewTransaction();
                    store.EnlistPrebuy(pending with { Processed = true, Revision = pending.Revision + 1 }, transaction);
                    byte[]? before = roles.LoadAuction(s.Role.Id);
                    var personal = before is { } saved ? DBAuction.Parser.ParseFrom(saved) : new DBAuction();
                    string key = PrebuyKey(pending.Type, pending.SrcType, pending.Entry);
                    if (personal.PreBuyNum.TryGetValue(key, out long count) && count > 0)
                    {
                        personal.PreBuyNum[key] = count - 1;
                        EnlistPersonal(s.Role.Id, before, personal, transaction);
                    }
                    mail.QueueTemplateLocked(s.Role.Id, $"prebuy:{pending.Id}:cancel", 1040,
                        [new MailService.Attachment(pending.Currency, pending.UnitPrice)], transaction,
                        data.ItemName(pending.Entry));
                    if (quit)
                    {
                        row.GiveUp.Add(s.Role.Id);
                        if (row.Type == AuctionType.Dungeon && row.Eligible.All(row.GiveUp.Contains)) row.End = Now;
                        store.EnlistSave(row, transaction);
                        changed = row;
                    }
                    transaction.WithEscrowEconomy(AuctionIntent("auction/prebuy-cancel", s.Role.Id, s.Account ?? "",
                            $"auction/prebuy/{pending.Id}/cancel")
                        .Evidence(new
                        {
                            pending.Id, pending.Entry, pending.Currency, pending.UnitPrice,
                            Quit = quit, PriorLot = priorLot, NewLot = LotEvidence(row)
                        }));
                    RequireScope(scope, transaction);
                    await roles.CommitAsync(transaction, "auction/prebuy-cancel");
                    committed = true;
                    refund = syncPersonal = true;
                }
                else
                {
                    if (!Visible(s, row) || guildId != 0 && row.GuildId != guildId) return (ErrorCodes.AuctionGuild, row.Entry);
                    if (row.State is not (AuctionState.Bidding or AuctionState.Public) || row.End <= Now)
                        return (ErrorCodes.NotForSale, row.Entry);
                    if (quit && row.Type != AuctionType.Dungeon) return (ErrorCodes.Parameter, row.Entry);
                    if (!quit && (row.Owner != s.Role.Id || !row.Auto)) return (ErrorCodes.NoPermission, row.Entry);
                    if (quit && row.GiveUp.Contains(s.Role.Id)) return (0, row.Entry);
                    refund = row.Owner == s.Role.Id;
                    var transaction = roles.NewTransaction();
                    if (refund)
                    {
                        row.Revision++;
                        mail.QueueTemplateLocked(s.Role.Id, $"auction:{id}:cancel:{row.Revision}:{s.Role.Id}", 1040,
                            [new MailService.Attachment(row.Currency, row.Reserve)], transaction, data.ItemName(row.Entry));
                        row.Owner = 0; row.Reserve = 0; row.Auto = false; row.AutoPaused = false;
                        row.Price = (int)GameData.Int(data.Row("AuctionConfig", row.Entry)!.Value,
                            row.Type == AuctionType.Dungeon ? "_iDungeonPrice" : "_iAuctionPrice");
                    }
                    if (quit)
                    {
                        row.GiveUp.Add(s.Role.Id);
                        if (row.Type == AuctionType.Dungeon && row.Eligible.All(row.GiveUp.Contains)) row.End = Now;
                    }
                    store.EnlistSave(row, transaction);
                    transaction.WithEscrowEconomy(AuctionIntent("auction/cancel", s.Role.Id, s.Account ?? "")
                        .Evidence(new
                        {
                            PriorLot = priorLot, NewLot = LotEvidence(row), Quit = quit,
                            Refund = refund
                        }));
                    RequireScope(scope, transaction);
                    await roles.CommitAsync(transaction, "auction/cancel");
                    committed = true;
                    changed = row;
                }
            }
            if (refund) await mail.NotifyRecipientAsync(requestRole);
            if (syncPersonal && AuctionRequestCurrent(s, requestRole, requestLease))
                await SyncPersonalAsync(requestRole);
            if (changed != null) await ChangedAsync(changed);
            return (0, entry);
        }
        catch (Exception e)
        {
            log.LogError(e, "Auction cancellation of {Auction} failed (committed before the failure: {Committed})", id, committed);
            return (committed ? 0 : ErrorCodes.Unknown, committed ? entry : 0);
        }
    }

    public async Task BatchBidAsync(GameSession s, BatchBiddingAuctionReq req)
    {
        long requestRole = s.Role?.Id ?? 0;
        var requestLease = s.Attachment;
        var res = new BatchBiddingAuctionRes { OptType = req.OptType };
        if (req.BiddingItems.Count == 0)
        {
            if (!AuctionRequestCurrent(s, requestRole, requestLease)) throw new OperationCanceledException();
            res.Result = req.ItemID > 0 && req.PreBuyNum == 0
                ? await SetPrebuyAsync(s, req.AuctionType, (int)req.SrcType, (int)req.ItemID, req.PreBuyNum)
                : ErrorCodes.AuctionParameter;
            await SendSessionAuctionAsync(s, requestRole, requestLease, BatchBid, res);
            return;
        }
        if (req.BiddingItems.Count > Math.Max(1, Param("AUCTION_BATCH_NUM", 100)) || req.PreBuyNum < 0)
            res.Result = ErrorCodes.AuctionParameter;
        else
        {
            foreach (var item in req.BiddingItems)
            {
                if (!AuctionRequestCurrent(s, requestRole, requestLease)) throw new OperationCanceledException();
                var row = store.Get(item.AuctionId);
                res.Results[item.AuctionId] = row == null || row.Type != req.AuctionType
                    || req.SrcType != 0 && row.SrcType != RowSrc(req.AuctionType, (int)req.SrcType)
                    || req.ItemID != 0 && row.Entry != req.ItemID
                    ? new BiddingAuctionRes { Result = ErrorCodes.Parameter, AuctionId = item.AuctionId }
                    : await BidCoreAsync(s, item.AuctionId, item.Price, item.OpType, 0, false, item.ExtraPrice);
            }
            res.Result = res.Results.Values.Any(r => r.Result == 0) ? 0 : res.Results.Values.First().Result;
            if (req.PreBuyNum > 0)
            {
                if (!AuctionRequestCurrent(s, requestRole, requestLease)) throw new OperationCanceledException();
                int count = await SetPrebuyAsync(s, req.AuctionType, (int)req.SrcType, (int)req.ItemID, req.PreBuyNum);
                if (count != 0 && res.Result == 0) res.Result = count; // actual escrow rows stay durable
            }
        }
        await SendSessionAuctionAsync(s, requestRole, requestLease, BatchBid, res);
    }

    public async Task BatchCancelAsync(GameSession s, BatchCancelBiddingAuctionReq req)
    {
        long requestRole = s.Role?.Id ?? 0;
        var requestLease = s.Attachment;
        var res = new BatchCancelBiddingAuctionRes { OptType = req.OptType };
        if (req.AuctionIds.Count > Math.Max(1, Param("AUCTION_BATCH_NUM", 100))) res.Result = ErrorCodes.Parameter;
        else
        {
            foreach (long id in req.AuctionIds.Distinct())
            {
                if (!AuctionRequestCurrent(s, requestRole, requestLease)) throw new OperationCanceledException();
                res.BatchResults[id] = (await CancelCoreAsync(s, id, req.GuildId, quit: false)).Code;
            }
            foreach (var (entry, remaining) in req.AfterCancelPreBuyNum)
            {
                if (!AuctionRequestCurrent(s, requestRole, requestLease)) throw new OperationCanceledException();
                int code = await SetPrebuyAsync(s, (AuctionType)req.AuctionType, (int)req.SrcType,
                    checked((int)entry), remaining);
                if (code != 0 && res.Result == 0) res.Result = code;
            }
            if (res.Result == 0 && res.BatchResults.Count > 0)
                res.Result = res.BatchResults.Values.Any(code => code == 0) ? 0 : res.BatchResults.Values.First();
        }
        await SendSessionAuctionAsync(s, requestRole, requestLease, BatchCancel, res);
    }
}
