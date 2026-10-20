using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Auctions of real content on the client's own data: dungeon bosses and their drops, guild challenge stages,
/// the world and event tabs (AuctionTests.World.cs, AuctionTests.Guild.cs).</summary>
public sealed partial class AuctionContentTests : AuctionTestsHost
{
    protected override string GameDataDir => Path.Combine(ClientData.RepoRoot, "data");

    [ClientDataFact]
    public async Task AuthoritativeThreePlayerBossCreatesDungeonRowsVisibleBeforeAnyBid()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        foreach (var bot in new[] { alice, bob, carol }) await Gm(bot, "!lvl 99");
        long team = await Party(alice, bob, carol);
        await alice.SendGameAsync(DungeonService.Enter, new EnterDungeonReq
        {
            SceneEntry = Scene,
            InitData = new DungeonInitData
            {
                MultiDungeonInitData = new MultiDungeonInitData
                { MultiDungeonId = Multi, MarkId = 1 }
            },
        });
        Assert.Equal(0, (await Next(alice, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        foreach (var member in new[] { bob, carol })
        {
            await Next(member, DungeonService.ReadyQueue, TeamEnterReadyQueueNotify.Parser, n => n.ReadyQueue.Count == 3);
            await member.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = true });
            Assert.Equal(0, (await Next(member, DungeonService.Confirm, ConfirmTeamDungeonRsp.Parser)).Result);
        }
        long runId = await Load(alice);
        Assert.Equal(runId, await Load(bob));
        Assert.Equal(runId, await Load(carol));
        var actor = await BossIn(runId);
        await Kill(alice, actor);
        foreach (var bot in new[] { alice, bob, carol })
        {
            Assert.Equal((DungeonRes)1, (await Next(bot, DungeonService.Result, DungeonResNotify.Parser)).Res);
            var award = await Next(bot, DungeonService.AwardNotify, SyncMultiDungeonAwardNotify.Parser);
            Assert.NotEmpty(award.AutionAward);
        }
        var rows = Service<AuctionService>().DungeonRows(runId);
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(AuctionState.Public, r.State));
        await alice.SendGameAsync(AuctionService.Self, new QuerySelfAuctionReq { AuctionType = AuctionType.Dungeon });
        var self = await Next(alice, AuctionService.Self, QuerySelfAuctionRes.Parser);
        Assert.Contains(self.SelfAuctions, r => rows.Any(row => row.Id == r.AuctionId));
        int before = rows.Count;
        await Service<AuctionService>().FinalizeDungeonRunAsync(runId);
        Assert.Equal(before, Service<AuctionService>().DungeonRows(runId).Count); // completion replay cannot mint twice
        await using var outsider = await InWorld("Outsider");
        await outsider.SendGameAsync(AuctionService.Query, new QueryAuctionReq
        { AuctionType = AuctionType.Dungeon, SrcType = 5001, StartIndex = 1, EndIndex = 100 });
        Assert.Empty((await Next(outsider, AuctionService.Query, QueryAuctionRes.Parser)).Auctions);
    }

    [ClientDataFact]
    public async Task AuctionInsertFailureCannotHideCommittedDungeonResultAndJournalRetries()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        foreach (var bot in new[] { alice, bob, carol }) await Gm(bot, "!lvl 99");
        await Party(alice, bob, carol);
        await alice.SendGameAsync(DungeonService.Enter, new EnterDungeonReq
        {
            SceneEntry = Scene,
            InitData = new DungeonInitData
            {
                MultiDungeonInitData = new MultiDungeonInitData
                { MultiDungeonId = Multi, MarkId = 1 }
            },
        });
        Assert.Equal(0, (await Next(alice, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        foreach (var member in new[] { bob, carol })
        {
            await Next(member, DungeonService.ReadyQueue, TeamEnterReadyQueueNotify.Parser, n => n.ReadyQueue.Count == 3);
            await member.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = true });
            Assert.Equal(0, (await Next(member, DungeonService.Confirm, ConfirmTeamDungeonRsp.Parser)).Result);
        }
        long runId = await Load(alice);
        await Load(bob);
        await Load(carol);
        FixtureSql.Execute(Service<AccountStore>(), "CREATE TRIGGER fail_auction_insert BEFORE INSERT ON auction_item "
            + "BEGIN SELECT RAISE(ABORT, 'test auction publication failure'); END");
        await Kill(alice, await BossIn(runId));
        foreach (var bot in new[] { alice, bob, carol })
        {
            Assert.Equal((DungeonRes)1, (await Next(bot, DungeonService.Result, DungeonResNotify.Parser)).Res);
            Assert.NotEmpty((await Next(bot, DungeonService.AwardNotify, SyncMultiDungeonAwardNotify.Parser)).NormalAward);
        }
        Assert.Empty(Service<AuctionService>().DungeonRows(runId));
        Assert.Contains(Service<AuctionStore>().PendingRuns(), r => r.Id == runId && r.Ready && !r.Finalized);
        FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_auction_insert");
        await Service<AuctionService>().FinalizeDungeonRunAsync(runId);
        Assert.NotEmpty(Service<AuctionService>().DungeonRows(runId));
        await alice.SendGameAsync(AuctionService.Self, new QuerySelfAuctionReq { AuctionType = AuctionType.Dungeon });
        Assert.NotEmpty((await Next(alice, AuctionService.Self, QuerySelfAuctionRes.Parser)).SelfAuctions);
    }

    [ClientDataFact]
    public async Task AuctionEconomyGuildPublicationSourcesEachResolvedLotOnce()
    {
        await using var alice = await InWorld("Alice");
        long guild = await AuctionGuild(alice);
        await Gm(alice, "!zeny 100000000");
        var opening = AuctionCut();
        Assert.Equal(0, await Service<AuctionService>().PublishGuildChallengeAsync(
            "economy-source", guild, 1001, 1, [(long)alice.Self!.Uuid]));

        var lots = Service<AuctionStore>().Items().Where(x => x.GuildId == guild).ToArray();
        Assert.NotEmpty(lots);
        var closing = AuctionCut();
        var ledger = AuctionWindow(opening, closing);
        Assert.Single(ledger.Receipts);
        var sources = ledger.Legs.Where(x => x.Kind == EconomyLegKind.Source).ToArray();
        Assert.Equal((long)lots.Length, sources.Sum(x => x.Delta));
        Assert.Equal(lots.Select(x => $"auction/lot/{x.Id}").Order(),
            sources.Select(x => x.Location.Key).Order());
        Assert.Equal(lots.Length, sources.Select(x => x.Location.Key).Distinct().Count());
        Assert.All(sources,
            leg => Assert.Equal(EconomyReason.ActivityReward, leg.Reason));
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.All(check.Assets, value => Assert.Equal("0", value.Residual));
        Assert.All(check.Locations, value => Assert.Equal("0", value.Residual));

        // A later sibling lot must retain its own identity through sale; the source is not recreated.
        var later = lots.OrderBy(x => x.Id).Skip(1).First();
        clock.Advance(TimeSpan.FromMinutes(5));
        await Service<AuctionService>().ProcessPrebuysAsync();
        Assert.Equal(0, (await Bid(alice, later.Id, later.FixedPrice, BiddingType.Fixed)).Result);
        Assert.Equal(later.SourceKey, Service<AuctionStore>().Get(later.Id)!.SourceKey);
        Assert.Single(Mailbox(alice).Letters, m => m.DeliveryKey == $"auction:{later.Id}:award");
        var afterSale = AuctionCut();
        var wholeWindow = AuctionWindow(opening, afterSale);
        Assert.Equal((long)lots.Length, wholeWindow.Legs.Where(x => x.Kind == EconomyLegKind.Source)
            .Sum(x => x.Delta));
        var wholeCheck = EconomyExactnessChecker.Check(opening, afterSale, wholeWindow);
        Assert.All(wholeCheck.Assets, value => Assert.Equal("0", value.Residual));
        Assert.All(wholeCheck.Locations, value => Assert.Equal("0", value.Residual));
    }
}
