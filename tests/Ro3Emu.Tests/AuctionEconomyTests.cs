using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;
using Romsg;

namespace Ro3Emu.Tests;

public sealed partial class AuctionTests
{
    [Fact]
    public async Task AuctionEconomyBidOutbidAndCancelConserveReserveAndRefundMail()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm(alice, "!zeny 10000");
        await Gm(bob, "!zeny 10000");
        var lot = Seed(AuctionType.Dungeon, alice, bob);
        var opening = AuctionCut();

        Assert.Equal(0, (await Bid(alice, lot.Id, 800, BiddingType.Auto)).Result);
        var afterAlice = AuctionCut();
        Assert.Equal(0, (await Bid(bob, lot.Id, 1000, BiddingType.Auto)).Result);
        var afterBob = AuctionCut();
        await bob.SendGameAsync(AuctionService.Cancel, new CancelBiddingAuctionReq { AuctionId = lot.Id });
        Assert.Equal(0, (await Next(bob, AuctionService.Cancel, CancelBiddingAuctionRes.Parser)).Result);

        var closing = AuctionCut();
        var ledger = AuctionWindow(opening, closing);
        Assert.Equal(3, ledger.Receipts.Count(r => r.CommitSequence > opening.Cut.CommitSequence
            && r.CommitSequence <= closing.Cut.CommitSequence));
        Assert.All(ledger.Legs, leg => Assert.Equal(EconomyLegKind.Transfer, leg.Kind));
        var outbidEdges = ledger.Legs.Where(leg => leg.CommitSequence > afterAlice.Cut.CommitSequence
            && leg.CommitSequence <= afterBob.Cut.CommitSequence && leg.Asset.Id == 1)
            .GroupBy(leg => leg.EdgeNumber).ToArray();
        Assert.Equal(2, outbidEdges.Length);
        Assert.All(outbidEdges, edge =>
        {
            var pair = edge.ToArray();
            Assert.Equal(2, pair.Length);
            Assert.Equal(pair[0].Location.Owner, pair[1].Location.Owner);
            Assert.Equal(0, pair.Sum(leg => leg.Delta));
        });
        Assert.Contains(outbidEdges, edge => edge.Any(leg => leg.Location.Compartment == EconomyCompartment.AuctionReserve
            && leg.Location.Owner.Id == alice.Self!.Uuid.ToString())
            && edge.Any(leg => leg.Location.Compartment == EconomyCompartment.Mail));
        Assert.Contains(outbidEdges, edge => edge.Any(leg => leg.Location.Compartment == EconomyCompartment.Currency
            && leg.Location.Owner.Id == bob.Self!.Uuid.ToString())
            && edge.Any(leg => leg.Location.Compartment == EconomyCompartment.AuctionReserve));
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.All(check.Assets, value => Assert.Equal("0", value.Residual));
        Assert.All(check.Locations, value => Assert.Equal("0", value.Residual));
        Assert.Equal(0, Service<AuctionStore>().Get(lot.Id)!.Reserve);
        Assert.Single(Mailbox(alice).Letters, m => m.DeliveryKey.StartsWith($"auction:{lot.Id}:outbid:"));
        Assert.Single(Mailbox(bob).Letters, m => m.DeliveryKey.StartsWith($"auction:{lot.Id}:cancel:"));
    }

    [Fact]
    public async Task AuctionEconomyFixedBuyRecordsExactFeeAndOneLotDelivery()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm(bob, "!zeny 10000");
        var lot = Seed(AuctionType.Dungeon, alice, bob);
        var opening = AuctionCut();

        Assert.Equal(0, (await Bid(bob, lot.Id, lot.FixedPrice, BiddingType.Fixed)).Result);
        var closing = AuctionCut();
        var ledger = AuctionWindow(opening, closing);
        long expectedDividend = lot.FixedPrice * Math.Clamp(
            Service<Ro3Emu.Server.Data.GameData>().Table("PublicParamConfig")
                .TryGetValue("GamePlayAuctionDividend", out var dividend)
                    ? (long)(Ro3Emu.Server.Data.GameData.Number(dividend) ?? 8000) : 8000, 0, 10000) / 10000;
        Assert.Equal(lot.FixedPrice - expectedDividend,
            ledger.Legs.Where(l => l.Kind == EconomyLegKind.Sink && l.Reason == EconomyReason.AuctionFee)
                .Sum(l => -l.Delta));
        Assert.Single(Mailbox(bob).Letters, m => m.DeliveryKey == $"auction:{lot.Id}:award");
        Assert.Single(Mailbox(alice).Letters, m => m.DeliveryKey == $"auction:{lot.Id}:dividend:{alice.Self!.Uuid}");
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.All(check.Assets, value => Assert.Equal("0", value.Residual));
        Assert.All(check.Locations, value => Assert.Equal("0", value.Residual));
    }

    [Fact]
    public async Task AuctionEconomyLegacyBonusClaimTransfersHeldBonusToMailOnce()
    {
        await using var alice = await InWorld("Alice");
        var lot = Seed(AuctionType.Dungeon, alice);
        long role = (long)alice.Self!.Uuid;
        string key = "dungeon:" + lot.Id;
        FixtureSql.Execute(Service<AccountStore>(), """
            INSERT INTO auction_bonus(role_id,group_key,amount,guild_id,claimed)
            VALUES($role,$key,125,0,0)
            """, ("$role", role), ("$key", key));
        var opening = AuctionCut();

        await alice.SendGameAsync(AuctionService.GetBonus, new GetAuctionBonusReq { SrcTypeGroupKey = key });
        var response = await Next(alice, AuctionService.GetBonus, GetAuctionBonusRes.Parser);
        Assert.Equal(0, response.Result);
        Assert.Equal(125, response.Bonus);
        Assert.Single(Mailbox(alice).Letters, letter => letter.DeliveryKey == $"auction:bonus:{key}:{role}");
        var closing = AuctionCut();
        var ledger = AuctionWindow(opening, closing);
        Assert.Single(ledger.Receipts);
        Assert.All(ledger.Legs, leg => Assert.Equal(EconomyLegKind.Transfer, leg.Kind));
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.All(check.Assets, value => Assert.Equal("0", value.Residual));
        Assert.All(check.Locations, value => Assert.Equal("0", value.Residual));

        await alice.SendGameAsync(AuctionService.GetBonus, new GetAuctionBonusReq { SrcTypeGroupKey = key });
        Assert.Equal(ErrorCodes.AuctionBonusClaimed,
            (await Next(alice, AuctionService.GetBonus, GetAuctionBonusRes.Parser)).Result);
        Assert.Equal(closing.Cut.CommitSequence, AuctionCut().Cut.CommitSequence);
    }

    [Fact]
    public async Task AuctionEconomyRefusalAndFailedCommitLeaveNoReceiptThenLostAckResolvesOnce()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!zeny 10000");
        var lot = Seed(AuctionType.Dungeon, alice);
        var accounts = Service<AccountStore>();
        var opening = AuctionCut();
        long money = Currency(alice);
        Assert.Equal(ErrorCodes.AuctionMaxPrice,
            (await Bid(alice, lot.Id, lot.MaxPrice + 1, BiddingType.Auto)).Result);
        Assert.Equal(opening.Cut.CommitSequence, AuctionCut().Cut.CommitSequence);

        FixtureSql.Execute(accounts, $"CREATE TRIGGER fail_auction_economy BEFORE UPDATE ON auction_item "
            + $"WHEN OLD.id={lot.Id} BEGIN SELECT RAISE(ABORT, 'test failure'); END");
        try
        {
            Assert.Equal(ErrorCodes.Unknown, (await Bid(alice, lot.Id, 800, BiddingType.Auto)).Result);
            Assert.Equal(opening.Cut.CommitSequence, AuctionCut().Cut.CommitSequence);
            Assert.Equal(money, Currency(alice));
        }
        finally { FixtureSql.Execute(accounts, "DROP TRIGGER fail_auction_economy"); }

        accounts.FailNextEconomyAcknowledgementForTest();
        Assert.Equal(0, (await Bid(alice, lot.Id, 800, BiddingType.Auto)).Result);
        Assert.Equal(money - 800, Currency(alice));
        var closing = AuctionCut();
        Assert.Equal(opening.Cut.CommitSequence + 1, closing.Cut.CommitSequence);
        Assert.Single(AuctionWindow(opening, closing).Receipts);
    }

    [Fact]
    public async Task AuctionEconomyDeletedRecipientRetainsLotAndReserveWithOneRecoveryReceipt()
    {
        var roles = Service<RoleStore>();
        var deleted = roles.Create("gonee03", 1, "GoneE03", 1, 1000, 1)!;
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        var lot = new AuctionStore.Item
        {
            SourceKey = "test:deleted:e03:" + Guid.NewGuid().ToString("N"),
            Type = AuctionType.Dungeon, SrcType = 5001, Entry = Entry,
            State = AuctionState.Bidding, Start = now - 100, End = now - 1,
            Owner = deleted.Id, Price = 500, Reserve = 1000, Currency = 1,
            Eligible = [deleted.Id], Joined = [deleted.Id],
        };
        SaveRows(lot);
        Assert.True(roles.Delete("gonee03", deleted.Id));
        var opening = AuctionCut();

        await Service<AuctionService>().SettleAsync(lot.Id);
        var closing = AuctionCut();
        var retained = Service<AuctionStore>().Get(lot.Id)!;
        Assert.Equal(AuctionState.Bidding, retained.State);
        Assert.Equal(1000, retained.Reserve);
        Assert.Equal($"deleted-recipient:{deleted.Id}", retained.SettlementRecoveryReason);
        Assert.Equal(opening.Cut.CommitSequence + 1, closing.Cut.CommitSequence);
        Assert.Empty(AuctionWindow(opening, closing).Legs);

        await Service<AuctionService>().SettleAsync(lot.Id);
        Assert.Equal(closing.Cut.CommitSequence, AuctionCut().Cut.CommitSequence);
    }
}
