using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

public sealed partial class AuctionService
{
    private void SettleLetters(AuctionStore.Item row, long[] dividendRoles, RoleItemPlanBuilder tx)
    {
        if (row.State != AuctionState.Settled) throw new InvalidOperationException("Only a terminal row can settle.");
        long winner = row.Winner;
        if (winner != 0)
        {
            var attachments = new List<MailService.Attachment> { new(row.Entry, 1, row.Bind) };
            if (row.Reserve > row.Price) attachments.Add(new(row.Currency, row.Reserve - row.Price));
            int template = row.Type == AuctionType.Dungeon ? 1053 : row.Auto && row.Reserve > row.Price ? 1045 : 1042;
            if (row.Type == AuctionType.Dungeon)
                mail.QueueTemplateLocked(winner, $"auction:{row.Id}:award", template, attachments, tx,
                    SourceName(row), data.ItemName(row.Entry));
            else
                // LC_DAL_Mail._decodeGetAuction reads four integers: price, currency item (its icon), auction tab
                // (AuctionTabConfig name) and the won item (its name). Strings arrived as 0: a Lua error per letter.
                mail.QueueTemplateLocked(winner, $"auction:{row.Id}:award", template, attachments, tx,
                    row.Price, row.Currency, MailTab(row), row.Entry);
        }
        if (row.Type == AuctionType.Guild && winner != 0 && row.GuildId != 0)
            guilds.EnlistEvents(row.GuildId, [GuildService.AuctionWonRow(winner, roles.Get(winner)?.Name ?? "", row.Entry)], tx);
        if (row.Type is not (AuctionType.Dungeon or AuctionType.Guild or AuctionType.World or AuctionType.Act)) return;
        string key = $"{row.Type.ToString().ToLowerInvariant()}:{row.Id}";
        // DBAuction keeps only the guild participation history (MG_DB.proto DBAuction); World/Act history is the
        // global 82004 list.
        if (row.Type is AuctionType.Dungeon or AuctionType.Guild)
            foreach (long role in row.Joined.Append(winner).Where(r => r != 0).Distinct())
            {
                byte[]? before = roles.LoadAuction(role);
                var own = before is { } saved ? DBAuction.Parser.ParseFrom(saved) : new DBAuction();
                own.GuildHistoryJoinAuctionItems[row.Id] = HistoryOf(row);
                while (own.GuildHistoryJoinAuctionItems.Count > RecordLimit)
                    own.GuildHistoryJoinAuctionItems.Remove(own.GuildHistoryJoinAuctionItems.Keys.Min());
                own.GuildHistoryOldestId = own.GuildHistoryJoinAuctionItems.Count == 0 ? 0 : own.GuildHistoryJoinAuctionItems.Keys.Min();
                EnlistPersonal(role, before, own, tx);
            }
        if (row.RandomAward || row.Price <= 0 || row.Reserve <= 0 || dividendRoles.Length == 0) return;
        long budget = DividendBudget(row);
        int share = (int)(budget / dividendRoles.Length), remainder = (int)(budget % dividendRoles.Length);
        for (int i = 0; i < dividendRoles.Length; i++)
        {
            int amount = share + (i < remainder ? 1 : 0);
            if (amount <= 0) continue;
            store.EnlistBonus(dividendRoles[i], key, amount, row.GuildId, tx);
            string dividendKey = $"auction:{row.Id}:dividend:{dividendRoles[i]}";
            var payout = new[] { new MailService.Attachment(row.Currency, amount) };
            if (row.Type is AuctionType.World or AuctionType.Act)
                // LC_DAL_Mail._decodeGetDungeonAuctionBonus (1054) names FunctionConfig param 1: 402 "World
                // Auction" for World rows, 4 "Auction" for Event rows (no Event function exists).
                mail.QueueTemplateLocked(dividendRoles[i], dividendKey, 1054, payout, tx,
                    row.Type == AuctionType.World ? 402 : 4);
            else
                mail.QueueTemplateLocked(dividendRoles[i], dividendKey,
                    row.Type == AuctionType.Guild ? 1100 : 1054, payout, tx, SourceName(row));
        }
    }

    /// <summary>The share of the realized price paid out as dividends. Dungeon: GamePlayAuctionDividend (8000/10000).
    /// Guild and guild-sourced World lots: the whole price. (inferred) Event lots follow the game-mode (dungeon)
    /// fraction, since their letters are the game-mode family 1053-1056.</summary>
    private long DividendBudget(AuctionStore.Item row) => row.Type is AuctionType.Dungeon or AuctionType.Act
        ? row.Price * Math.Clamp(Param("GamePlayAuctionDividend", 8000), 0, 10000) / 10000
        : row.Price;

    /// <summary>The tab id for the award letter: the row's leaf tab; a World row always names AuctionTabConfig row 2
    /// "World Auction" (the client queries World by item type, UI_Sys_AuctionWidgetCtrl.lua:1145-1168).</summary>
    private int MailTab(AuctionStore.Item row) => row.Type == AuctionType.World ? WorldTab : row.SrcType;

    /// <summary>Outbid refund letter. LC_DAL_Mail._decodeBackSelfAuction (lines 2107-2149) reads two integers: the
    /// item (its name) and the auction type for the "openauction/&lt;type&gt;" link. Dungeon keeps 1055.</summary>
    private void QueueOutbidLocked(AuctionStore.Item row, long role, int reserve, RoleItemPlanBuilder tx)
    {
        string key = $"auction:{row.Id}:outbid:{row.Revision}:{role}";
        if (row.Type == AuctionType.Dungeon)
            mail.QueueTemplateLocked(role, key, 1055, [new MailService.Attachment(row.Currency, reserve)], tx,
                SourceName(row), data.ItemName(row.Entry));
        else
            mail.QueueTemplateLocked(role, key, 1041, [new MailService.Attachment(row.Currency, reserve)], tx,
                row.Entry, (int)row.Type);
    }

    private string SourceName(AuctionStore.Item row) => data.Row("AuctionTabConfig", row.SrcType) is { } tab
        ? data.TextOf(tab, "_iName") : row.Type.ToString();

    /// <summary>Commit award, surplus refund, personal history and bonus ledger with the terminal row.</summary>
    // (inferred) A guild-sourced World lot keeps its guild's rule: leaving the guild before settlement forfeits.
    private long[] DividendRoles(AuctionStore.Item row) => row.Eligible
        .Where(role => !(row.Type == AuctionType.Guild || row.Type == AuctionType.World && row.GuildId != 0)
            || guilds.ForRole(role)?.Id == row.GuildId)
        .Distinct().Order().ToArray();

    public async Task SettleAsync(long id)
    {
        if (!loop.IsOnLoop) { await loop.InvokeAsync(() => SettleAsync(id)); return; }
        AuctionStore.Item? settled = null, moved = null;
        long[] dividendRoles = [];
        long winner = 0;
        using (var scope = await EnterAuctionAsync("auction/settle", () => RowKeys(store.Get(id))))
        {
            var row = store.Get(id);
            if (row == null || row.State == AuctionState.Settled || row.End > Now
                || row.SettlementRecoveryReason.Length != 0) return;
            object priorLot = LotEvidence(row);
            winner = row.Owner;
            if (winner == 0 && row.Type == AuctionType.Dungeon)
            {
                var eligible = row.Eligible.Where(r => roles.Get(r) != null).ToArray();
                if (eligible.Length > 0)
                {
                    winner = eligible[Random.Shared.Next(eligible.Length)]; // (inferred) uniform unsold draw.
                    row.RandomAward = true;
                }
            }
            if (winner == 0 && WorldTransferAllowed(row))
            {
                // (inferred) An unbid guild lot moves on to the World Auction in place, keeping its escrowed item,
                // source identity and participant dividend rights (MG_Auction.proto DBAuctionGoodRecord counts
                // guild-generated World goods per guild). The row is not settled, so it mails nothing now.
                MoveToWorld(row);
                var move = roles.NewTransaction();
                store.EnlistSave(row, move);
                move.WithEscrowEconomy(AuctionIntent("auction/world-transfer", sourceKey: $"auction/lot/{id}/world")
                    .Evidence(new { PriorLot = priorLot, NewLot = LotEvidence(row) }));
                RequireScope(scope, move);
                await roles.CommitAsync(move, "auction/world-transfer");
                moved = row;
            }
            else
            {
                // Non-dungeon rows are never server-minted without a source; retain their durable row on unsold expiry.
                dividendRoles = winner != 0 && !row.RandomAward ? DividendRoles(row) : [];
                var recipients = dividendRoles.Append(winner)
                    .Concat(winner != 0 && !row.RandomAward && row.Type is (AuctionType.Dungeon or AuctionType.Guild)
                        ? row.Joined : Array.Empty<long>()).Where(role => role != 0).Distinct().ToArray();
                if (recipients.FirstOrDefault(role => roles.Get(role) == null) is var missing and > 0)
                {
                    // Retain the paid escrow and source row until a product policy resolves a deleted claimant.
                    row.SettlementRecoveryReason = $"deleted-recipient:{missing}";
                    row.Revision++;
                    var recovery = roles.NewTransaction();
                    store.EnlistSave(row, recovery);
                    recovery.WithEscrowEconomy(AuctionIntent("auction/settlement-recovery")
                        .Evidence(new { PriorLot = priorLot, NewLot = LotEvidence(row), MissingRecipient = missing }));
                    RequireScope(scope, recovery);
                    await roles.CommitAsync(recovery, "auction/settlement-recovery");
                    log.LogWarning("Auction {Auction} requires settlement recovery for deleted recipient {Role}", id, missing);
                    return;
                }
                row.Winner = winner;
                row.State = AuctionState.Settled;
                row.End = Math.Min(row.End, Now);
                var transaction = roles.NewTransaction();
                store.EnlistSave(row, transaction);
                if (winner != 0)
                {
                    if (row.RandomAward)
                        mail.QueueTemplateLocked(winner, $"auction:{id}:unsold", 1113,
                            [new MailService.Attachment(row.Entry, 1, row.Bind)], transaction);
                    else SettleLetters(row, dividendRoles, transaction);
                }
                transaction.WithEscrowEconomy(SettlementIntent("auction/settle", row, dividendRoles,
                    sourceKey: $"auction/lot/{id}/settle",
                    details: new
                    {
                        PriorLot = priorLot, NewLot = LotEvidence(row), Winner = winner,
                        DividendRoles = dividendRoles
                    }));
                RequireScope(scope, transaction);
                await roles.CommitAsync(transaction, "auction/settle");
                settled = row;
            }
        }
        if (moved != null)
        {
            try { await WorldUploadNoticeAsync([moved]); }
            catch (Exception e) { log.LogWarning(e, "Committed World transfer {Auction} notification failed", id); }
            return;
        }
        try
        {
            if (winner != 0) await mail.NotifyRecipientAsync(winner);
            foreach (long recipient in dividendRoles) await mail.NotifyRecipientAsync(recipient);
            await ChangedAsync(settled!);
            await SettlementNoticesAsync(settled!);
        }
        catch (Exception e) { log.LogWarning(e, "Committed auction settlement {Auction} notification failed", id); }
    }

    private async Task SettlementNoticesAsync(AuctionStore.Item row)
    {
        foreach (long role in row.Joined.Append(row.Winner).Where(r => r != 0).Distinct()) await SyncPersonalAsync(role);
        if (row.Type == AuctionType.Dungeon)
        {
            var notice = new DungeonAuctionDoneNtf();
            notice.HistoryAuctions.Add(HistoryOf(row));
            foreach (long role in row.Eligible) if (Online(role) is { } s)
                try { await s.SendGameAsync(DungeonDone, notice); }
                catch (Exception e) { log.LogWarning(e, "Dungeon auction done notification failed for {Role}", role); }
        }
    }

    public async Task QueryBonusAsync(GameSession s, QueryAuctionBonusReq req)
    {
        long requestRole = s.Role?.Id ?? 0;
        var requestLease = s.Attachment;
        QueryAuctionBonusRes res;
        using (var scope = await EnterSessionAuctionAsync(s, "auction/bonus-read", role => RoleKeys([role])))
        {
            res = new QueryAuctionBonusRes();
            foreach (var bonus in store.Bonuses(s.Role!.Id).Where(b => req.GuildId == 0 || b.Guild == req.GuildId))
                res.GroupBonus[bonus.Key] = new DBBonus
                {
                    Bonus = bonus.Amount, GuildId = bonus.Guild,
                    State = bonus.Claimed ? GroupBonusState.Recieved : GroupBonusState.Finished
                };
        }
        await SendSessionAuctionAsync(s, requestRole, requestLease, QueryBonus, res);
    }

    public async Task GetBonusAsync(GameSession s, GetAuctionBonusReq req)
    {
        long requestRole = s.Role?.Id ?? 0;
        var requestLease = s.Attachment;
        int result = ErrorCodes.AuctionNoBonus, amount = 0;
        bool committed = false;
        if (s.Role != null)
        {
            try
            {
                using (var scope = await EnterSessionAuctionAsync(s, "auction/bonus-claim", role => RoleKeys([role])))
                {
                    var bonus = store.Bonuses(s.Role.Id).FirstOrDefault(b => b.Key == req.SrcTypeGroupKey);
                    if (bonus != null) result = bonus.Claimed ? ErrorCodes.AuctionBonusClaimed : 0;
                    if (bonus is { Claimed: false })
                    {
                        var transaction = roles.NewTransaction();
                        store.EnlistBonusClaim(s.Role.Id, bonus.Key, transaction);
                        mail.QueueTemplateLocked(s.Role.Id, $"auction:bonus:{bonus.Key}:{s.Role.Id}",
                            bonus.Guild == 0 ? 1054 : 1100,
                            [new MailService.Attachment((int)Param("AUCTION_CURRENCY", 1), bonus.Amount)], transaction,
                            bonus.Guild == 0 ? "Dungeon" : "Guild");
                        transaction.WithEscrowEconomy(AuctionIntent("auction/bonus-claim", s.Role.Id, s.Account ?? "",
                                $"auction/bonus/{s.Role.Id}/{bonus.Key}/claim")
                            .Evidence(new { bonus.Key, bonus.Amount, bonus.Guild, Currency = Param("AUCTION_CURRENCY", 1) }));
                        RequireScope(scope, transaction);
                        await roles.CommitAsync(transaction, "auction/bonus-claim");
                        amount = bonus.Amount;
                        committed = true;
                    }
                }
                if (amount > 0) await mail.NotifyRecipientAsync(requestRole);
            }
            catch (Exception e)
            {
                log.LogError(e, "Auction bonus claim failed (committed before the failure: {Committed})", committed);
                result = committed ? 0 : ErrorCodes.Unknown;
            }
        }
        await SendSessionAuctionAsync(s, requestRole, requestLease, GetBonus, new GetAuctionBonusRes
        { SrcTypeGroupKey = req.SrcTypeGroupKey, Result = result, Bonus = amount });
    }
}
