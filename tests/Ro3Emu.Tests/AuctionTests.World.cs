using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>World Auction (type 2) and Event Auction (type 3): creation, visibility on the client's own queries,
/// bidding, settlement, dividends and restart idempotency (AuctionService.World.cs).</summary>
public sealed partial class AuctionContentTests
{
    private const int NoviceActivity = 10000001, EventTab = 3001;

    private AuctionStore.Item[] Lots(AuctionType type) => Service<AuctionStore>().Items().Where(r => r.Type == type).ToArray();

    private static async Task<QueryAuctionCountRes> Count(BotClient bot, AuctionType type)
    {
        await bot.SendGameAsync(AuctionService.Count, new QueryAuctionCountReq { AuctionTypes = { (long)type } });
        return await Next(bot, AuctionService.Count, QueryAuctionCountRes.Parser);
    }

    private static async Task<QueryAuctionRes> Query(BotClient bot, AuctionType type, int srcType = 0, params long[] itemTypes)
    {
        await bot.SendGameAsync(AuctionService.Query, new QueryAuctionReq
        { AuctionType = type, SrcType = srcType, StartIndex = 1, EndIndex = 100, AuctionItemTypes = { itemTypes } });
        return await Next(bot, AuctionService.Query, QueryAuctionRes.Parser, r => r.AuctionType == type);
    }

    private static long[] IntParams(MailService.Letter letter) =>
        LanguageData.Parser.ParseFrom(letter.ContentParams!).Param.Select(p =>
        {
            Assert.Equal(LanguageParamType.Int, p.Tp);
            return p.IntParam;
        }).ToArray();

    // 2026-10-08 12:00 +02:00 is CEST; WorldAuctionEnd 18 closes World lots at 18:00 local the same day.
    private long SixPm(int days = 0) => new DateTimeOffset(2026, 10, 8 + days, 18, 0, 0, TimeSpan.FromHours(2)).ToUnixTimeSeconds();

    [ClientDataFact]
    public async Task WorldGmLotsListPerItemTypeAndBuyOutSettlesWithoutDividend()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm(bob, "!zeny 100000000");
        Assert.Empty(Lots(AuctionType.World));
        Assert.Empty((await Count(bob, AuctionType.World)).AuctionSrcTypeCount[(long)AuctionType.World].ItemTypeCount);

        await Gm(alice, "!worldauction open");
        var lots = Lots(AuctionType.World);
        Assert.Equal(4, lots.Length); // one lot per AuctionConfig._iType, the client's World sub-tabs
        Assert.All(lots, r =>
        {
            Assert.Equal(2, r.SrcType); // the World tab; World requests send srcType 0 for "any"
            Assert.Equal(0, r.GuildId);
            Assert.Empty(r.Eligible);
            Assert.Equal(AuctionState.Bidding, r.State);
            Assert.Equal(SixPm(), r.End);
        });

        // The client lists World sub-tabs from itemTypeCount and queries each by item type with srcType 0.
        var counts = (await Count(bob, AuctionType.World)).AuctionSrcTypeCount[(long)AuctionType.World];
        Assert.Equal([1L, 2L, 3L, 4L], counts.ItemTypeCount.Keys.Order().ToArray());
        var lot = lots.Single(r => r.Entry == 1200001);
        var listed = await Query(bob, AuctionType.World, 0, 2);
        Assert.Equal(lot.Id, Assert.Single(listed.Auctions).AuctionId);
        Assert.True(listed.Auctions[0].IsCanBidding);

        Assert.Equal(0, (await Bid(bob, lot.Id, lot.FixedPrice, BiddingType.Fixed)).Result);
        var sold = Service<AuctionStore>().Get(lot.Id)!;
        Assert.Equal(AuctionState.Settled, sold.State);
        Assert.Equal((long)bob.Self!.Uuid, sold.Winner);
        var award = Assert.Single(Mailbox(bob).Letters, m => m.DeliveryKey == $"auction:{lot.Id}:award");
        Assert.Equal(1042, award.TemplateId);
        Assert.Equal([(long)lot.FixedPrice, lot.Currency, 2L, lot.Entry], IntParams(award)); // tab 2 "World Auction"
        Assert.DoesNotContain(Mailbox(alice).Letters, m => m.DeliveryKey.Contains(":dividend:"));
        Assert.DoesNotContain(Mailbox(bob).Letters, m => m.DeliveryKey.Contains(":dividend:"));

        await bob.SendGameAsync(AuctionService.History, new QueryHistoryAuctionReq { AuctionType = AuctionType.World });
        var history = await Next(bob, AuctionService.History, QueryHistoryAuctionRes.Parser);
        Assert.Equal(lot.Id, Assert.Single(history.HistoryAuctions).AuctionId);

        await Gm(alice, "!worldauction close");
        Assert.All(Lots(AuctionType.World), r => Assert.Equal(AuctionState.Settled, r.State));
        Assert.Equal(3, Lots(AuctionType.World).Count(r => r.Winner == 0));
        Assert.Empty((await Count(bob, AuctionType.World)).AuctionSrcTypeCount[(long)AuctionType.World].ItemTypeCount);
    }

    [ClientDataFact]
    public async Task UnbidGuildLotMovesToWorldOnceAndPaysItsGuildParticipantsTheWholePrice()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol"); // no guild: World lots are open to everyone
        long guild = await AuctionGuild(alice, bob);
        await Gm(carol, "!zeny 100000000");
        long participant = (long)alice.Self!.Uuid;
        var opening = AuctionCut();
        var service = Service<AuctionService>();
        Assert.Equal(0, await service.PublishGuildChallengeAsync("clear-world", guild, 1001, 1, [participant]));
        var guildLots = Service<AuctionStore>().Items().Where(r => r.GuildId == guild).ToArray();
        Assert.NotEmpty(guildLots);

        clock.Advance(TimeSpan.FromMinutes(36)); // exhibit 5 + bidding 30: every lot ended without a bid
        foreach (var row in guildLots) await service.SettleAsync(row.Id);
        foreach (var row in guildLots) await service.SettleAsync(row.Id); // a second pass changes nothing
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        var after = guildLots.Select(r => Service<AuctionStore>().Get(r.Id)!).ToArray();
        var moved = after.Where(r => r.Type == AuctionType.World).ToArray();
        Assert.NotEmpty(moved);
        Assert.All(after.Where(r => r.Type == AuctionType.Guild), r =>
        {
            // Only an item already at its _iWorldAuctionCeiling stays behind as an unsold guild lot.
            Assert.Equal(AuctionState.Settled, r.State);
            Assert.Equal(0, r.Winner);
        });
        Assert.All(moved, r =>
        {
            Assert.Equal(AuctionState.Public, r.State);
            Assert.Equal(2, r.SrcType); // the World tab; World requests send srcType 0 for "any"
            Assert.Equal(guild, r.GuildId);
            Assert.Equal(participant, Assert.Single(r.Eligible));
            Assert.Equal(now + 5 * 60, r.Start);
            Assert.Equal(SixPm(), r.End);
            Assert.Empty(r.Joined);
        });
        Assert.All(moved.GroupBy(r => r.Entry), g => Assert.True(g.Count() <= 10));
        Assert.DoesNotContain(Mailbox(alice).Letters, m => m.DeliveryKey.StartsWith("auction:"));
        Assert.Empty((await Count(bob, AuctionType.Guild)).AuctionSrcTypeCount[(long)AuctionType.Guild].SrcTypeCount);
        Assert.NotEmpty((await Count(carol, AuctionType.World)).AuctionSrcTypeCount[(long)AuctionType.World].ItemTypeCount);

        // A pre-buy on a showing World lot arrives with the client's World srcType 0 and counts under the World tab.
        Assert.True(moved.Length >= 2);
        var queued = moved[^1];
        var prebuy = new BatchBiddingAuctionReq { AuctionType = AuctionType.World, SrcType = 0, ItemID = queued.Entry, PreBuyNum = 1 };
        prebuy.BiddingItems.Add(new BatchBiddingAuctionItem { AuctionId = queued.Id, Price = queued.Price, OpType = BiddingType.Auto });
        await carol.SendGameAsync(AuctionService.BatchBid, prebuy);
        Assert.Equal(0, (await Next(carol, AuctionService.BatchBid, BatchBiddingAuctionRes.Parser)).Result);
        Assert.Equal(1, Session(carol).Auction!.PreBuyNum[$"2:2:{queued.Entry}"]);
        await carol.SendGameAsync(AuctionService.PreStack, new PreAuctionItemStackQueryReq
        { AuctionType = AuctionType.World, QueryItemTypes = { (int)Ro3Emu.Server.Data.GameData.Int(Service<Ro3Emu.Server.Data.GameData>().Row("AuctionConfig", queued.Entry)!.Value, "_iType") } });
        var stack = (await Next(carol, AuctionService.PreStack, PreAuctionItemStackQueryRes.Parser)).PreItems[queued.Entry];
        Assert.Equal((1L, AuctionState.Public), (stack.PreBuyNum, stack.State));

        clock.Advance(TimeSpan.FromMinutes(5));
        await service.ProcessPrebuysAsync();
        // The pre-buy takes the cheapest open lot of its item (then the lowest id); the guild drop can hold the same
        // item twice, so that is not always the lot it was placed on.
        var bought = moved.Where(r => r.Entry == queued.Entry).OrderBy(r => r.Price).ThenBy(r => r.Id).First();
        Assert.Equal((long)carol.Self!.Uuid, Service<AuctionStore>().Get(bought.Id)!.Owner);
        var lot = Service<AuctionStore>().Get(moved.First(r => r.Id != bought.Id).Id)!;
        Assert.Equal(AuctionState.Bidding, lot.State);
        Assert.Equal(0, (await Bid(carol, lot.Id, lot.FixedPrice, BiddingType.Fixed)).Result);
        var award = Assert.Single(Mailbox(carol).Letters, m => m.DeliveryKey == $"auction:{lot.Id}:award");
        Assert.Equal([(long)lot.FixedPrice, lot.Currency, 2L, lot.Entry], IntParams(award));
        var dividend = Assert.Single(Mailbox(alice).Letters, m => m.DeliveryKey == $"auction:{lot.Id}:dividend:{participant}");
        Assert.Equal(1054, dividend.TemplateId);
        Assert.Equal([402L], IntParams(dividend)); // FunctionConfig 402 "World Auction"
        Assert.Equal(lot.FixedPrice, Assert.Single(dividend.Attachments).Count);
        var bonus = Assert.Single(Service<AuctionStore>().Bonuses(participant));
        Assert.Equal(($"world:{lot.Id}", lot.FixedPrice, true), (bonus.Key, bonus.Amount, bonus.Claimed));
        Assert.DoesNotContain(Mailbox(bob).Letters, m => m.DeliveryKey.Contains(":dividend:"));

        // The move keeps the escrowed lot in place: one source per lot, no residual across publish, move and sale.
        var closing = AuctionCut();
        var ledger = AuctionWindow(opening, closing);
        // (Other realm sources, e.g. stall upkeep, also run during the 41 simulated minutes.)
        var lotSources = ledger.Legs.Where(x => x.Kind == EconomyLegKind.Source && x.Location.Key.StartsWith("auction/lot/")).ToArray();
        Assert.Equal(guildLots.Select(r => $"auction/lot/{r.Id}").Order(), lotSources.Select(x => x.Location.Key).Order());
        Assert.Equal((long)guildLots.Length, lotSources.Sum(x => x.Delta));
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.All(check.Assets, value => Assert.Equal("0", value.Residual));
        Assert.All(check.Locations, value => Assert.Equal("0", value.Residual));
    }

    [ClientDataFact]
    public async Task WorldTransferStopsAtTheItemsWorldAuctionCeiling()
    {
        const int capped = 1251008; // AuctionConfig._iWorldAuctionCeiling 1
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        var rows = Enumerable.Range(0, 2).Select(_ => new AuctionStore.Item
        {
            SourceKey = "test:" + Guid.NewGuid().ToString("N"), Type = AuctionType.Guild, SrcType = 1003,
            GuildId = 123456, Entry = capped, State = AuctionState.Bidding, Start = now - 600, End = now - 1,
            Price = 500000, Raise = 100000, FixedPrice = 1999998, MaxPrice = 1999998, Currency = 1,
        }).ToArray();
        SaveRows(rows);
        foreach (var row in rows) await Service<AuctionService>().SettleAsync(row.Id);
        var first = Service<AuctionStore>().Get(rows[0].Id)!;
        var second = Service<AuctionStore>().Get(rows[1].Id)!;
        Assert.Equal((AuctionType.World, AuctionState.Public), (first.Type, first.State));
        Assert.Equal((AuctionType.Guild, AuctionState.Settled, 0L), (second.Type, second.State, second.Winner));
        Assert.Equal(ErrorCodes.RewardMissing,
            (await Service<AuctionService>().PublishWorldSystemAsync("capped", [capped], startNow: true)).Code);
    }

    [ClientDataFact]
    public async Task EventLotsNeedTheirActivityWindowAndShareTheGameModeDividend()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm(bob, "!zeny 100000000");
        long participant = (long)alice.Self!.Uuid;
        var service = Service<AuctionService>();
        // OpenContent off: the tab's activity 10000001 ended on 2025-08-01, so there is no Event Auction underway.
        Assert.Equal(ErrorCodes.ActivityClosed,
            await service.PublishEventAuctionAsync("closed", EventTab, [(1200000, 1, 0)], [participant]));
        Assert.Empty(Lots(AuctionType.Act));

        await Gm(alice, $"!actauction {EventTab} open");
        // The client lists Event sub-tabs only while LC_ActManager holds the tab's activity.
        var started = await Next(bob, TimedEventService.StatusNotify, ActivityDataNtf.Parser,
            n => n.ActivityId == NoviceActivity && n.ActivityStatus == ActivityStatus.Start);
        Assert.True(started.CloseTime > clock.GetUtcNow().ToUnixTimeMilliseconds());
        var lots = Lots(AuctionType.Act);
        Assert.Equal(4, lots.Length);
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        Assert.All(lots, r =>
        {
            Assert.Equal(EventTab, r.SrcType);
            Assert.Equal(participant, Assert.Single(r.Eligible));
            Assert.Equal(AuctionState.Bidding, r.State);
            Assert.Equal(now + 30 * 60, r.End);
        });
        Assert.Equal(4, (await Count(bob, AuctionType.Act)).AuctionSrcTypeCount[(long)AuctionType.Act].SrcTypeCount[EventTab]);
        Assert.Equal(4, (await Query(bob, AuctionType.Act, EventTab)).Auctions.Count);
        // A repeated completion key never publishes twice.
        Assert.Equal(0, await service.PublishEventAuctionAsync("once", EventTab, [(1200000, 1, 0)], [participant]));
        Assert.Equal(0, await service.PublishEventAuctionAsync("once", EventTab, [(1200000, 1, 0)], [participant]));
        Assert.Equal(5, Lots(AuctionType.Act).Length);

        var lot = lots.Single(r => r.Entry == 1200000);
        Assert.Equal(0, (await Bid(bob, lot.Id, lot.Price, BiddingType.Manual)).Result);
        clock.Advance(TimeSpan.FromMinutes(36)); // the queued "once" lot shows 5 minutes first
        foreach (var row in Lots(AuctionType.Act)) await service.SettleAsync(row.Id);
        var sold = Service<AuctionStore>().Get(lot.Id)!;
        Assert.Equal((AuctionState.Settled, (long)bob.Self!.Uuid), (sold.State, sold.Winner));
        var award = Assert.Single(Mailbox(bob).Letters, m => m.DeliveryKey == $"auction:{lot.Id}:award");
        Assert.Equal([(long)lot.Price, lot.Currency, EventTab, lot.Entry], IntParams(award)); // tab "North-South Brown King"
        var dividend = Assert.Single(Mailbox(alice).Letters, m => m.DeliveryKey == $"auction:{lot.Id}:dividend:{participant}");
        Assert.Equal(1054, dividend.TemplateId);
        Assert.Equal([4L], IntParams(dividend));
        Assert.Equal(lot.Price * 8000 / 10000, Assert.Single(dividend.Attachments).Count);
        Assert.All(Lots(AuctionType.Act).Where(r => r.Id != lot.Id), r =>
            Assert.Equal((AuctionState.Settled, 0L), (r.State, r.Winner)));

        await Gm(alice, $"!actauction {EventTab} close");
        Assert.False(Service<TimedEventService>().IsActive(NoviceActivity));
    }

    [ClientDataFact]
    public async Task WorldEventAndMovedLotsSettleOnceAcrossRestarts()
    {
        long aliceId, bobId, bobFunds, eventLot, worldLot;
        long[] guildLots;
        await using (var alice = await InWorld("Alice"))
        await using (var bob = await InWorld("Bob"))
        {
            long guild = await AuctionGuild(alice);
            await Gm(bob, "!zeny 100000000");
            aliceId = (long)alice.Self!.Uuid; bobId = (long)bob.Self!.Uuid;
            Assert.Equal(0, await Service<AuctionService>().PublishGuildChallengeAsync("restart", guild, 1001, 1, [aliceId]));
            guildLots = Service<AuctionStore>().Items().Where(r => r.GuildId == guild).Select(r => r.Id).ToArray();
            await Gm(alice, $"!actauction {EventTab} open");
            await Gm(alice, "!worldauction open 1200000");
            eventLot = Lots(AuctionType.Act).Single(r => r.Entry == 1200000).Id;
            worldLot = Lots(AuctionType.World).Single().Id;
            Assert.Equal(0, (await Bid(bob, eventLot, 600, BiddingType.Manual)).Result);
            Assert.Equal(0, (await Bid(bob, worldLot, 700, BiddingType.Manual)).Result);
            bobFunds = Currency(bob);
        }
        // Down from 12:00 to 19:00: the event lots (12:30), the guild lots (12:35) and the World lot (18:00) all end.
        await RestartHostAsync(TimeSpan.FromHours(7));
        for (int i = 0; i < 200 && (Service<AuctionStore>().Get(eventLot)!.State != AuctionState.Settled
            || Service<AuctionStore>().Get(worldLot)!.State != AuctionState.Settled
            || guildLots.Any(id => Service<AuctionStore>().Get(id)!.Type == AuctionType.Guild
                && Service<AuctionStore>().Get(id)!.State != AuctionState.Settled)); i++)
            await Task.Delay(50);
        Assert.Equal(bobId, Service<AuctionStore>().Get(eventLot)!.Winner);
        Assert.Equal(bobId, Service<AuctionStore>().Get(worldLot)!.Winner);
        var movedEnds = guildLots.Select(id => Service<AuctionStore>().Get(id)!)
            .Where(r => r.Type == AuctionType.World).ToDictionary(r => r.Id, r => r.End);
        Assert.NotEmpty(movedEnds);
        Assert.All(movedEnds.Values, end => Assert.Equal(SixPm(1), end)); // moved at 19:00, so 18:00 the next day

        await RestartHostAsync(TimeSpan.FromMinutes(1));
        await Task.Delay(1500); // more than one sweep
        Assert.Single(MailboxOf(bobId).Letters, m => m.DeliveryKey == $"auction:{eventLot}:award");
        Assert.Single(MailboxOf(bobId).Letters, m => m.DeliveryKey == $"auction:{worldLot}:award");
        Assert.Single(MailboxOf(aliceId).Letters, m => m.DeliveryKey == $"auction:{eventLot}:dividend:{aliceId}");
        Assert.Equal(movedEnds, guildLots.Select(id => Service<AuctionStore>().Get(id)!)
            .Where(r => r.Type == AuctionType.World).ToDictionary(r => r.Id, r => r.End));
        // The forced activity window lived in memory only: after a restart the tab is closed again.
        Assert.False(Service<TimedEventService>().IsActive(NoviceActivity));
        await using var returning = await InWorld("Bob", create: false);
        Assert.Equal(bobFunds, Currency(returning));
    }
}
