using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

public sealed partial class GuildService
{
    private long MarketNowMs() => clock.GetUtcNow().ToUnixTimeMilliseconds();
    internal Func<Task>? AfterMarketNoticeSendForTest { get; set; }

    private EscrowEconomyIntent MarketEconomy(GameSession? session, string command, object evidence) =>
        new EscrowEconomyIntent("guild-market/" + command, session?.Account ?? "", session?.Role?.Id ?? 0,
            effectiveUtcMs: MarketNowMs(), transferReason: EconomyReason.GuildResourceTransfer).Evidence(evidence);

    private static void MarketDiscard(EscrowEconomyIntent economy, long amount)
    {
        if (amount > 0) economy.Sink(new(EconomyAssetKind.GuildResource, EconomyDenominations.GuildMaterials),
            amount, EconomyReason.GuildMarketCapDiscard);
    }

    private long MarketParam(string name, long fallback)
    {
        var table = data.Table("PublicParamConfig");
        return table.TryGetValue(name, out JsonElement value) && GameData.Number(value) is { } number
            ? (long)number : fallback;
    }

    private long[] MarketArray(string name, params long[] fallback)
    {
        var table = data.Table("PublicParamConfig");
        return table.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray() : fallback;
    }

    private int MarketGate(GameSession s, GuildStore.State? guild, bool mutation)
    {
        if (guild == null) return ErrorCodes.NotInGuild;
        if (guild.Status != (int)GuildStatus.Normal) return ErrorCodes.FeatureUnavailable;
        if (!functions.IsOpen(s, 78)) return ErrorCodes.FeatureUnavailable;
        return mutation && !HasRight(guild, s.Role!.Id, 25) ? ErrorCodes.GuildPermission : 0;
    }

    private int CheckMarketPrice(long price, GuildStore.MarketState market)
    {
        var adjust = MarketArray("BlackMarketPriceAdjust", 500, 4, 4);
        if (adjust.Length < 3 || adjust[0] <= 0 || price <= 0 || price % adjust[0] != 0) return ErrorCodes.Parameter;
        long lower = Math.Max(0, market.RecentAvgPrice - adjust[0] * adjust[1]);
        long upper = market.RecentAvgPrice + adjust[0] * adjust[2];
        return price >= lower && price <= upper ? 0 : ErrorCodes.Parameter;
    }

    private void CaptureMarketResources(OwnerNotices notices, params GuildStore.State[] guilds)
    {
        foreach (var guild in guilds.DistinctBy(g => g.Id))
        {
            var packet = new GuildBaseInfoChangeNtf
            { Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeResource, Resource = Resource(guild) };
            foreach (var member in guild.Members.Values)
                if (Online(member.RoleId) is { } peer)
                    notices.Add(peer, () => peer.SendGameAsync(BaseChange, packet));
        }
    }

    /// <summary>
    /// 839xx: guild funds 104 buy materials 126.
    /// FIFO same-price matching, basis-point fees, minimum configured listing delay, whole-order fills,
    /// cap-confirm excess discard and Unix-hour history buckets are (inferred) policies.
    /// </summary>
    public async Task MarketAsync(GameSession s, uint id, ReadOnlyMemory<byte> body)
    {
        var owners = s.Owners ?? throw new InvalidOperationException("Guild market requires an owner registry.");
        var owner = s.Owner;
        var attachment = s.Attachment;
        long? roleId = s.Role?.Id, epoch = owner?.LifecycleEpoch;
        bool Attached() => s.Role?.Id == roleId && ReferenceEquals(s.Owner, owner) &&
            ReferenceEquals(s.Attachment, attachment) && attachment?.Cancelled != true && owner?.LifecycleEpoch == epoch;
        long? queuedAt = null;
        while (true)
        {
            if (!Attached()) throw new OperationCanceledException("Guild market requester attachment changed.");
            var staged = store.LoadMarket();
            long now = MarketNowMs();
            // E03 §8: a missing guild has no authorized refund/burn destination. Retain its escrow
            // for recovery (the economic decoder reports that debt); never silently delete the stock.
            var expired = staged.Stalls.Where(x => x.ExpiresMs <= now || store.Get(x.GuildId) == null).ToArray();
            // Expiry commits first. A subsequent request stages against the refunded balance.
            var stagedGuild = expired.Length == 0 ? Own(s) : null;
            var guildIds = expired.Length != 0 ? expired.Select(x => x.GuildId) :
                MarketParticipants(staged, stagedGuild, id, body, now);
            var keys = guildIds.Distinct().Select(x => ReservationKey.Entity("guild", x.ToString(), "state"))
                .Append(ReservationKey.Entity("guild_market", "1", "state"))
                .Concat(roleId is { } role ? [ReservationKey.Role(role)] : Array.Empty<ReservationKey>())
                .Concat(s.Account is { } account ? [ReservationKey.Account(account)] : Array.Empty<ReservationKey>())
                .ToArray();
            var scope = owners.TryEnter("guild/market", keys);
            if (scope == null)
            {
                queuedAt ??= System.Diagnostics.Stopwatch.GetTimestamp();
                await owners.Reservations.Changed;
                continue;
            }
            if (queuedAt is { } started) { owners.RecordQueueAge(started); queuedAt = null; }
            var notices = new OwnerNotices(scope.Context);
            Func<Task>? reply = null;
            bool restage = false;
            try
            {
                if (!Attached()) throw new OperationCanceledException("Guild market requester attachment changed.");
                if (owner != null) scope.Context.Pin(owner);
                var market = store.LoadMarket();
                if (market.CapturedRevision != staged.CapturedRevision) restage = true;
                else if (expired.Length != 0)
                {
                    await ExpireMarketAsync(market, expired.Select(x => x.Id).ToHashSet(StringComparer.Ordinal), notices);
                    restage = true;
                }
                else
                {
                    var guild = Own(s);
                    if (guild?.Id != stagedGuild?.Id) restage = true;
                    else reply = await MarketCoreAsync(s, id, body, market, guild, now, notices);
                }
            }
            finally { notices.Dispose(); scope.Dispose(); }
            await notices.FlushAsync(SendMarketNoticeAsync);
            if (restage) continue;
            if (reply != null && Attached()) await reply();
            return;
        }
    }

    private async Task SendMarketNoticeAsync(Func<Task> notice)
    {
        try { await notice(); }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Guild market notice failed: {0}", error); }
        if (AfterMarketNoticeSendForTest is { } afterSend) await afterSend();
    }

    private long[] MarketParticipants(GuildStore.MarketState market, GuildStore.State? buyer,
        uint id, ReadOnlyMemory<byte> body, long now)
    {
        if (buyer == null) return [];
        var participants = new List<long> { buyer.Id };
        if (id != MarketBuy) return [.. participants];
        var request = BuyBlackMarketItemReq.Parser.ParseFrom(body.Span);
        if (request.UnitPrice <= 0 || request.BuyCount <= 0) return [.. participants];
        long remaining = request.BuyCount;
        foreach (var stall in market.Stalls.Where(x => x.UnitPrice == request.UnitPrice && x.AvailableMs <= now
            && x.ExpiresMs > now && x.RemainCount > 0 && x.GuildId != buyer.Id)
            .OrderBy(x => x.CreatedMs).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            if (remaining <= 0) break;
            participants.Add(stall.GuildId);
            remaining -= Math.Min(remaining, stall.RemainCount);
        }
        return [.. participants];
    }

    private async Task<Func<Task>?> MarketCoreAsync(GameSession s, uint id, ReadOnlyMemory<byte> body,
        GuildStore.MarketState market, GuildStore.State? guild, long now, OwnerNotices notices)
    {
        Func<Task>? reply = null;
        int gate = MarketGate(s, guild, id is MarketList or MarketUnlist or MarketBuy or MarketUpdate);
        switch (id)
        {
            case MarketBooth:
                {
                    GuildStore.MarketStall[] booths = guild == null ? [] : market.Stalls.Where(x => x.GuildId == guild.Id).ToArray();
                    var rsp = new GetBlackMarketBoothRsp
                    {
                        Result = gate != 0 ? gate : booths.Length == 0 ? ErrorCodes.GuildMissing : 0,
                        RecentAvgPrice = market.RecentAvgPrice
                    };
                    rsp.Booths.AddRange(booths.Select(x => new BlackMarketBoothInfo
                    {
                        StallUniqueId = x.Id,
                        UnitPrice = x.UnitPrice, TotalCount = x.TotalCount, RemainCount = x.RemainCount,
                        DisplayAvailableTimeMs = x.AvailableMs, ExpireTimeMs = x.ExpiresMs
                    }));
                    rsp.PriceHistory.AddRange(market.PriceHistory.Select(x => new BlackMarketPriceHistoryInfo
                    { AvgPrice = x.Average, DateHour = x.DateHour }));
                    reply = () => s.SendGameAsync(id, rsp);
                    break;
                }
            case MarketItems:
                {
                    var items = market.Stalls.Where(x => x.AvailableMs <= now && x.ExpiresMs > now && x.RemainCount > 0)
                        .GroupBy(x => x.UnitPrice).OrderBy(x => x.Key).ToArray();
                    var rsp = new GetBlackMarketItemsRsp
                    {
                        Result = gate != 0 ? gate : items.Length == 0 ? ErrorCodes.GuildMissing : 0,
                        RecentAvgPrice = market.RecentAvgPrice
                    };
                    rsp.Items.AddRange(items.Select(x => new BlackMarketBigStack
                    { UnitPrice = x.Key, TotalRemainCount = x.Sum(y => y.RemainCount) }));
                    rsp.PriceHistory.AddRange(market.PriceHistory.Select(x => new BlackMarketPriceHistoryInfo
                    { AvgPrice = x.Average, DateHour = x.DateHour }));
                    reply = () => s.SendGameAsync(id, rsp);
                    break;
                }
            case MarketList:
                {
                    var req = ListOnBlackMarketReq.Parser.ParseFrom(body.Span);
                    int code = gate != 0 ? gate : CheckMarketPrice(req.UnitPrice, market);
                    long step = MarketParam("BlackMarketSellStep", 10);
                    if (code == 0 && (step <= 0 || req.Count <= 0 || req.Count % step != 0)) code = ErrorCodes.Parameter;
                    if (code == 0 && market.Stalls.Count(x => x.GuildId == guild!.Id) >= MarketParam("BlackMarketStallCount", 6)) code = ErrorCodes.GuildFull;
                    if (code == 0 && market.LastListedMs.GetValueOrDefault(guild!.Id) + MarketParam("BlackMarketListCooldownSec", 1) * 1000 > MarketNowMs()) code = ErrorCodes.Cooldown;
                    if (code == 0 && guild!.Materials < req.Count) code = ErrorCodes.ItemsShort;
                    if (code == 0)
                    {
                        long listedAt = MarketNowMs();
                        var delay = MarketArray("BlackMarketDelayRangeSec", 300, 600);
                        long seconds = delay.Length == 0 ? 300 : Math.Max(0, delay[0]);
                        guild!.Materials -= req.Count;
                        market.Stalls.Add(new GuildStore.MarketStall
                        {
                            Id = $"stall-{++market.NextId}", GuildId = guild.Id,
                            UnitPrice = req.UnitPrice, TotalCount = req.Count, RemainCount = req.Count, CreatedMs = listedAt,
                            AvailableMs = listedAt + seconds * 1000, ExpiresMs = listedAt + MarketParam("BlackMarketItemValidSec", 86400) * 1000
                        });
                        market.LastListedMs[guild.Id] = listedAt;
                        if (!await store.SaveMarketAsync(market, [guild], MarketEconomy(s, "list", req))) code = ErrorCodes.GuildMissing;
                        else CaptureMarketResources(notices, guild);
                    }
                    var response = new ListOnBlackMarketRsp { Result = code };
                    reply = () => s.SendGameAsync(id, response);
                    break;
                }
            case MarketUnlist:
                {
                    var req = UnlistFromBlackMarketReq.Parser.ParseFrom(body.Span);
                    var stall = market.Stalls.FirstOrDefault(x => x.Id == req.StallUniqueId && x.GuildId == guild?.Id);
                    int code = gate != 0 ? gate : stall == null ? ErrorCodes.Parameter : 0;
                    long cap = Setting("GuildMaterialsMax", 1000000)[0];
                    if (code == 0 && !req.Confirm && guild!.Materials > cap - stall!.RemainCount) code = ErrorCodes.Parameter;
                    if (code == 0)
                    {
                        long before = guild!.Materials;
                        guild!.Materials = Math.Min(cap, guild.Materials + stall!.RemainCount);
                        var economy = MarketEconomy(s, "unlist", new
                        {
                            Request = req, Cap = cap, Before = before,
                            Escrow = stall.RemainCount, Stored = guild.Materials
                        });
                        MarketDiscard(economy, checked(before + stall.RemainCount - guild.Materials));
                        market.Stalls.Remove(stall);
                        if (!await store.SaveMarketAsync(market, [guild], economy)) code = ErrorCodes.GuildMissing;
                        else CaptureMarketResources(notices, guild);
                    }
                    var response = new UnlistFromBlackMarketRsp { Result = code };
                    reply = () => s.SendGameAsync(id, response);
                    break;
                }
            case MarketBuy:
                {
                    var req = BuyBlackMarketItemReq.Parser.ParseFrom(body.Span);
                    int code = gate != 0 ? gate : req.UnitPrice <= 0 || req.BuyCount <= 0 ? ErrorCodes.Parameter : 0;
                    var stalls = market.Stalls.Where(x => x.UnitPrice == req.UnitPrice && x.AvailableMs <= now
                        && x.ExpiresMs > now && x.RemainCount > 0 && x.GuildId != guild?.Id)
                        .OrderBy(x => x.CreatedMs).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
                    if (code == 0 && stalls.Sum(x => (decimal)x.RemainCount) < req.BuyCount) code = ErrorCodes.ItemsShort;
                    long total = 0;
                    if (code == 0) { try { total = checked(req.UnitPrice * req.BuyCount); } catch (OverflowException) { code = ErrorCodes.Parameter; } }
                    if (code == 0 && guild!.Money < total) code = ErrorCodes.GuildFundsShort;
                    long cap = Setting("GuildMaterialsMax", 1000000)[0];
                    if (code == 0 && !req.Confirm && guild!.Materials > cap - req.BuyCount) code = ErrorCodes.Parameter;
                    if (code == 0)
                    {
                        var sellers = new Dictionary<long, GuildStore.State>();
                        long selectedRemain = req.BuyCount;
                        foreach (var stall in stalls)
                        {
                            if (selectedRemain == 0) break;
                            if (!sellers.ContainsKey(stall.GuildId))
                            {
                                var seller = store.Get(stall.GuildId);
                                if (seller == null) { code = ErrorCodes.GuildMissing; break; }
                                sellers.Add(stall.GuildId, seller);
                            }
                            selectedRemain -= Math.Min(selectedRemain, stall.RemainCount);
                        }
                        if (code == 0)
                        {
                            var prospectiveCredit = new Dictionary<long, decimal>();
                            long previewRemain = req.BuyCount;
                            foreach (var stall in stalls)
                            {
                                if (previewRemain == 0) break;
                                long count = Math.Min(previewRemain, stall.RemainCount);
                                decimal amount = (decimal)count * req.UnitPrice;
                                decimal fee = decimal.Truncate(amount * MarketParam("BlackMarketFeeRate", 250) / 10000m);
                                prospectiveCredit[stall.GuildId] = prospectiveCredit.GetValueOrDefault(stall.GuildId) + amount - fee;
                                previewRemain -= count;
                            }
                            if (prospectiveCredit.Any(x => x.Value < 0 || x.Value > long.MaxValue - sellers[x.Key].Money))
                                code = ErrorCodes.Parameter;
                        }
                        if (code == 0)
                        {
                            long remain = req.BuyCount, tradedAt = MarketNowMs(), totalFees = 0;
                            long beforeMaterials = guild!.Materials;
                            foreach (var stall in stalls)
                            {
                                if (remain == 0) break;
                                long count = Math.Min(remain, stall.RemainCount);
                                long amount = checked(count * req.UnitPrice);
                                // BlackMarketFeeRate=250 uses the project's 10000-unit percentage convention (inferred).
                                long fee = (long)((decimal)amount * MarketParam("BlackMarketFeeRate", 250) / 10000m);
                                totalFees = checked(totalFees + fee);
                                var seller = sellers[stall.GuildId];
                                seller.Money = checked(seller.Money + amount - fee);
                                stall.RemainCount -= count;
                                remain -= count;
                                market.Trades.Add(new GuildStore.MarketTrade
                                {
                                    Id = $"order-{++market.NextId}",
                                    SellerGuildId = seller.Id, BuyerGuildId = guild!.Id, UnitPrice = req.UnitPrice,
                                    Count = count, Fee = fee, TimeMs = tradedAt
                                });
                            }
                            market.Stalls.RemoveAll(x => x.RemainCount == 0);
                            guild!.Money -= total;
                            Journal(guild, EvPurchase, [s.Role!.Id], Str(s.Role.Name), Int(total), Int(req.BuyCount), Int(GuildMaterialItem));
                            guild.Materials = (long)Math.Min(cap, (decimal)guild.Materials + req.BuyCount);
                            market.RecentPrices.Add(req.UnitPrice);
                            int sample = (int)Math.Clamp(MarketParam("BlackMarketRecentAvgSample", 10), 1, 1000);
                            while (market.RecentPrices.Count > sample) market.RecentPrices.RemoveAt(0);
                            market.RecentAvgPrice = (long)(market.RecentPrices.Sum(x => (decimal)x) / market.RecentPrices.Count);
                            int hour = (int)(tradedAt / 3_600_000);
                            if (market.PriceHistory.LastOrDefault()?.DateHour == hour)
                                market.PriceHistory[^1].Average = market.RecentAvgPrice;
                            else market.PriceHistory.Add(new GuildStore.MarketPrice { Average = market.RecentAvgPrice, DateHour = hour });
                            if (market.PriceHistory.Count > 168) market.PriceHistory.RemoveRange(0, market.PriceHistory.Count - 168);
                            var economy = MarketEconomy(s, "buy", new
                            {
                                Request = req, Cap = cap,
                                Before = beforeMaterials, Stored = guild.Materials, Paid = total, Fee = totalFees,
                                Sellers = sellers.Keys.Order().ToArray()
                            });
                            if (totalFees > 0) economy.Sink(new(EconomyAssetKind.GuildResource, EconomyDenominations.GuildMoney),
                                totalFees, EconomyReason.GuildMarketFee);
                            MarketDiscard(economy, checked(beforeMaterials + req.BuyCount - guild.Materials));
                            if (!await store.SaveMarketAsync(market, [guild, .. sellers.Values], economy)) code = ErrorCodes.GuildMissing;
                            else
                            {
                                CaptureMarketResources(notices, [guild, .. sellers.Values]);
                                foreach (var seller in sellers.Values)
                                    foreach (var member in seller.Members.Values)
                                        if (Online(member.RoleId) is { } peer)
                                        {
                                            var packet = new BlackMarketSellRecordNtf();
                                            notices.Add(peer, () => peer.SendGameAsync(83907, packet));
                                        }
                            }
                        }
                    }
                    var response = new BuyBlackMarketItemRsp { Result = code };
                    reply = () => s.SendGameAsync(id, response);
                    break;
                }
            case MarketSellRecords:
                {
                    var records = market.Trades.Where(x => x.SellerGuildId == guild?.Id).OrderByDescending(x => x.TimeMs).ToArray();
                    var rsp = new GetBlackMarketSellRecordsRsp { Result = gate != 0 ? gate : records.Length == 0 ? ErrorCodes.GuildMissing : 0 };
                    rsp.Records.AddRange(records.Select(x => new BlackMarketSellRecordInfo
                    {
                        OrderId = x.Id,
                        UnitPrice = x.UnitPrice, Count = x.Count, Fee = x.Fee, CounterpartGuildId = x.BuyerGuildId,
                        TradeTimeMs = x.TimeMs, IsRead = x.Read
                    }));
                    if (gate == 0 && records.Length > 0)
                    {
                        foreach (var record in records) record.Read = true;
                        await store.SaveMarketAsync(market, [], MarketEconomy(s, "read-sales", new { Guild = guild!.Id }));
                    }
                    reply = () => s.SendGameAsync(id, rsp);
                    break;
                }
            case MarketBuyRecords:
                {
                    var records = market.Trades.Where(x => x.BuyerGuildId == guild?.Id).OrderByDescending(x => x.TimeMs).ToArray();
                    var rsp = new GetBlackMarketBuyRecordsRsp { Result = gate != 0 ? gate : records.Length == 0 ? ErrorCodes.GuildMissing : 0 };
                    rsp.Records.AddRange(records.Select(x => new BlackMarketBuyRecordInfo
                    {
                        OrderId = x.Id,
                        UnitPrice = x.UnitPrice, Count = x.Count, TradeTimeMs = x.TimeMs
                    }));
                    reply = () => s.SendGameAsync(id, rsp);
                    break;
                }
            case MarketUpdate:
                {
                    var req = UpdateBlackMarketBoothReq.Parser.ParseFrom(body.Span);
                    var stall = market.Stalls.FirstOrDefault(x => x.Id == req.StallUniqueId && x.GuildId == guild?.Id);
                    int code = gate != 0 ? gate : stall == null ? ErrorCodes.Parameter : CheckMarketPrice(req.UnitPrice, market);
                    long step = MarketParam("BlackMarketSellStep", 10);
                    if (code == 0 && (req.Count <= 0 || step <= 0 || req.Count % step != 0)) code = ErrorCodes.Parameter;
                    long difference = stall == null ? 0 : req.Count - stall.RemainCount;
                    if (code == 0 && difference > 0 && guild!.Materials < difference) code = ErrorCodes.ItemsShort;
                    long cap = Setting("GuildMaterialsMax", 1000000)[0];
                    if (code == 0 && difference < 0 && !req.Confirm && guild!.Materials > cap + difference) code = ErrorCodes.Parameter;
                    if (code == 0)
                    {
                        long before = guild!.Materials;
                        guild!.Materials = difference > 0 ? guild.Materials - difference : Math.Min(cap, guild.Materials - difference);
                        stall!.UnitPrice = req.UnitPrice;
                        stall.TotalCount += difference;
                        stall.RemainCount = req.Count;
                        var economy = MarketEconomy(s, "update", new
                        {
                            Request = req, Cap = cap, Before = before,
                            Difference = difference, Stored = guild.Materials
                        });
                        MarketDiscard(economy, checked(before - difference - guild.Materials));
                        if (!await store.SaveMarketAsync(market, [guild], economy)) code = ErrorCodes.GuildMissing;
                        else CaptureMarketResources(notices, guild);
                    }
                    var response = new UpdateBlackMarketBoothRsp { Result = code };
                    reply = () => s.SendGameAsync(id, response);
                    break;
                }
        }
        return reply;
    }

    private async Task ExpireMarketAsync(GuildStore.MarketState market, HashSet<string> expiredIds,
        OwnerNotices notices)
    {
        // Keep missing-owner escrow as recovery debt; only a surviving owner can receive the return.
        var stale = market.Stalls.Where(stall => expiredIds.Contains(stall.Id))
            .Select(stall => (Stall: stall, Guild: store.Get(stall.GuildId))).ToArray();
        if (stale.Length == 0) return;
        var guilds = new Dictionary<long, GuildStore.State>();
        var economy = MarketEconomy(null, "expire", new
        {
            Stalls = expiredIds.Order().ToArray(),
            Cap = Setting("GuildMaterialsMax", 1000000)[0]
        });
        foreach (var (stall, owner) in stale)
        {
            if (owner == null)
            {
                market.RecoveryStalls.Add(stall);
                market.Stalls.Remove(stall);
                continue;
            }
            if (!guilds.TryGetValue(owner.Id, out var guild)) guilds.Add(owner.Id, guild = owner);
            long before = guild.Materials;
            guild.Materials = Math.Min(Setting("GuildMaterialsMax", 1000000)[0],
                checked(guild.Materials + stall.RemainCount)); // expiry overflow is capped (inferred)
            MarketDiscard(economy, checked(before + stall.RemainCount - guild.Materials));
            market.Stalls.Remove(stall);
        }
        if (await store.SaveMarketAsync(market, guilds.Values, economy))
        {
            CaptureMarketResources(notices, [.. guilds.Values]);
        }
    }
}
