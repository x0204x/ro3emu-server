using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

internal static class MarketEconomyEvidence
{
    internal static EconomySnapshot Cut(string directory, string root) =>
        EconomySnapshotReader.Read(Path.Combine(directory, "ro3emu.db"), EconomyCatalog.Load(Path.Combine(root, "data")), "auto");

    internal static EconomyLedgerWindow Exact(string directory, string root, EconomySnapshot before,
        EconomySnapshot after, bool recovery = false)
    {
        var ledger = EconomyLedgerReader.Read(Path.Combine(directory, "ro3emu.db"),
            EconomyCatalog.Load(Path.Combine(root, "data")), before.Cut.RealmId,
            before.Cut.CommitSequence!.Value, after.Cut.CommitSequence!.Value);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues.Select(x => x.Detail)));
        var check = EconomyExactnessChecker.Check(before, after, ledger);
        Assert.DoesNotContain(check.Issues, x => x.Kind is EconomyIssueKind.Corruption or EconomyIssueKind.UnknownSchema);
        if (!recovery) Assert.DoesNotContain(check.Issues, x => x.Kind == EconomyIssueKind.RecoveryDebt);
        Assert.All(check.Assets, x => { Assert.Equal("0", x.Residual); Assert.Equal("0", x.LegResidual); });
        Assert.All(check.Locations, x => Assert.Equal("0", x.Residual));
        Assert.Equal(EconomyVerdict.NotMeasured, check.Verdict); // Other production families remain explicitly uncovered.
        return ledger;
    }
}

public sealed partial class TradeTests
{
    [ClientDataFact]
    public async Task E03TradeEscrowMailClaimAndListingFeeHaveExactInstancesAndSources()
    {
        await using var seller = await InWorld("LedgerSeller");
        await using var buyer = await InWorld("LedgerBuyer");
        await Gm("ledgerseller", "!lvl 30"); await Gm("ledgerbuyer", "!lvl 30");
        await Gm("ledgerseller", "!item 1254013 2"); await Gm("ledgerseller", "!item 2 700");
        long original = Items(seller).Single(i => i.Entry == 1254013).Guid;
        await Stage(seller, p =>
        {
            var item = p.Items.Single(i => i.Guid == original);
            item.Attrs = new() { [999] = 71 }; item.StringAttrs = new() { [510] = "e03-instance" };
        });
        var before = MarketEconomyEvidence.Cut(_dataDir, RepoRoot());
        await seller.SendGameAsync(TradeService.PutOn, new TradePutOnItemReq
        { ItemGuid = original, ItemPrice = 600, CurrencyType = 1, Quantity = 2 });
        var listed = await Next(seller, TradeService.PutOn, TradePutOnItemResp.Parser);
        Assert.Equal(0, listed.Result); Assert.Equal(700, listed.Tax);
        var row = Assert.Single(listed.BoothInfos);
        await buyer.SendGameAsync(TradeService.BuyNormal, new TradeBuyNormalItemsReq
        { ItemId = 1254013, SpuId = row.SpuId, Price = 600, TotalPrice = 600, TotalQuantity = 1 });
        Assert.Equal(0, (await Next(buyer, TradeService.BuyNormal, TradeBuyNormalItemsResp.Parser)).Result);
        var mail = Assert.Single(await Mail(buyer, (long)buyer.Self!.Uuid), x => x.Template == 1064);
        await buyer.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { mail.Id } });
        Assert.Equal(0, (await Next(buyer, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        var claimed = Assert.Single(Items(buyer), i => i.Entry == 1254013);
        Assert.NotEqual(original, claimed.Guid); Assert.Equal(1, claimed.Stack);
        Assert.Equal(71, claimed.Attrs![999]); Assert.Equal("e03-instance", claimed.StringAttrs![510]);
        var after = MarketEconomyEvidence.Cut(_dataDir, RepoRoot());
        var ledger = MarketEconomyEvidence.Exact(_dataDir, RepoRoot(), before, after);
        Assert.DoesNotContain(ledger.Legs, x => x.Kind == EconomyLegKind.Source);
        var fee = Assert.Single(ledger.Legs, x => x.Kind == EconomyLegKind.Sink);
        Assert.Equal((EconomyReason.MarketTax, 2L, -700L), (fee.Reason, fee.Asset.Id, fee.Delta));
        Assert.Contains(ledger.Legs, x => x.Asset.Id == 1254013 && x.Location.Compartment == EconomyCompartment.TradeListing && x.Delta == 2);
        Assert.Contains(ledger.Legs, x => x.Asset.Id == 1254013 && x.Location.Instance == claimed.Guid.ToString() && x.Delta == 1);
        var unchanged = after.Cut.CommitSequence;
        await buyer.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { mail.Id } });
        Assert.Equal(ErrorCodes.MailAlreadyClaimed, (await Next(buyer, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        Assert.Equal(unchanged, MarketEconomyEvidence.Cut(_dataDir, RepoRoot()).Cut.CommitSequence);
    }
}

public sealed partial class StallsTests
{
    [ClientDataFact]
    public async Task E03SystemStockReplacementRecordsGrossRetirementAndRestock()
    {
        await using var buyer = await InWorld("RestockLedger");
        await Warp(buyer, 10111, 251, 2);
        var before = MarketEconomyEvidence.Cut(_dataDir, RepoRoot());
        var oldStock = before.Holdings.Where(x => x.Location.Owner.Kind == EconomyOwnerKind.System
            && x.Location.Compartment == EconomyCompartment.Stall).ToArray();
        Assert.NotEmpty(oldStock);
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            _clock.Advance(TimeSpan.FromDays(4));
            await Service<StallsService>().RefreshWorldMaintenanceAsync();
        });
        var after = MarketEconomyEvidence.Cut(_dataDir, RepoRoot());
        var ledger = MarketEconomyEvidence.Exact(_dataDir, RepoRoot(), before, after);
        foreach (var holding in oldStock)
            Assert.Equal(-holding.Quantity, ledger.Legs.Where(x => x.Asset == holding.Asset
                && x.Location == holding.Location && x.Reason == EconomyReason.SystemRetire).Sum(x => x.Delta));
        foreach (var holding in after.Holdings.Where(x => x.Location.Owner.Kind == EconomyOwnerKind.System
            && x.Location.Compartment == EconomyCompartment.Stall))
            Assert.Equal(holding.Quantity, ledger.Legs.Where(x => x.Asset == holding.Asset
                && x.Location == holding.Location && x.Reason == EconomyReason.SystemRestock).Sum(x => x.Delta));
    }

    [ClientDataFact]
    public async Task E03PlayerStallFeeSaleTaxAndReturnedStockAreExactTransfers()
    {
        await using var seller = await InWorld("LedgerStall"); await using var buyer = await InWorld("LedgerShopper");
        await Ready(seller); await Ready(buyer);
        await Gm(seller, "!item 1000100 3"); await Gm(buyer, "!item 2 20000");
        long guid = Items(seller).Single(i => i.Entry == 1000100).Guid;
        var before = MarketEconomyEvidence.Cut(_dataDir, RepoRoot());
        var opened = await Open(seller, Offer(guid)); Assert.Equal(0, opened.Result);
        long offer = Service<MarketStore>().ReadCurrent<StallsService.StallRow>("stall", "p:" + Id(seller))!.Stock.Single().Id;
        Assert.Equal(0, await Purchase(buyer, Id(seller), offer));
        await buyer.SendGameAsync(StallsService.Buy, new PlayerStallsBuyItemReq
        { StallsID = Id(seller), StallsItemID = offer, Num = 1, Price = 9999 });
        Assert.Equal(ErrorCodes.StallPriceChanged, (await Next(buyer, StallsService.Buy, PlayerStallsBuyItemResp.Parser)).Result);
        await seller.SendGameAsync(StallsService.End, new PlayerStallsEndReq());
        Assert.Equal(0, (await Next(seller, StallsService.End, PlayerStallsEndResp.Parser)).Result);
        await Claim(buyer, 1028); await Claim(seller, 1029, 1030);
        Assert.Equal(1, Items(buyer).Single(i => i.Entry == 1000100).Stack);
        Assert.Equal(2, Items(seller).Where(i => i.Entry == 1000100).Sum(i => i.Stack));
        Assert.Equal(9100, Money(seller, 2)); Assert.Equal(10000, Money(buyer, 2));
        var ledger = MarketEconomyEvidence.Exact(_dataDir, RepoRoot(), before, MarketEconomyEvidence.Cut(_dataDir, RepoRoot()));
        Assert.DoesNotContain(ledger.Legs, x => x.Kind == EconomyLegKind.Source);
        Assert.Equal(-20, ledger.Legs.Where(x => x.Reason == EconomyReason.StallOpenFee).Sum(x => x.Delta));
        Assert.Equal(-900, ledger.Legs.Where(x => x.Reason == EconomyReason.StallTax).Sum(x => x.Delta));
        Assert.Contains(ledger.Legs, x => x.Asset.Id == 1000100 && x.Location.Compartment == EconomyCompartment.Stall && x.Delta == 2);
    }
}

public sealed partial class GuildTests
{
    [ClientDataFact]
    public async Task E03GuildMarketUpdateRequiresCapConfirmationAndKeepsRemainingEscrow()
    {
        await using var seller = await InWorld("CapEditor"); await Level("capeditor");
        using (var client = new HttpClient())
            (await client.PostAsync($"{_http}/ro3emu/gm?account=capeditor", new StringContent("!lvl 99"))).EnsureSuccessStatusCode();
        long guildId = await CreateGuild(seller, "Edits");
        var store = _app.Services.GetRequiredService<GuildStore>();
        var state = store.Get(guildId)!; state.Materials = 20; Assert.True(store.Save(state));
        await seller.SendGameAsync(GuildService.MarketList, new ListOnBlackMarketReq { UnitPrice = 500, Count = 20 });
        Assert.Equal(0, (await Next(seller, GuildService.MarketList, ListOnBlackMarketRsp.Parser)).Result);
        string stallId = Assert.Single(store.LoadMarket().Stalls).Id;
        state = store.Get(guildId)!; state.Materials = 999995; Assert.True(store.Save(state));
        var before = MarketEconomyEvidence.Cut(_dataDir, RepoRoot());
        var request = new UpdateBlackMarketBoothReq { StallUniqueId = stallId, UnitPrice = 1000, Count = 10 };
        await seller.SendGameAsync(GuildService.MarketUpdate, request);
        Assert.Equal(ErrorCodes.Parameter, (await Next(seller, GuildService.MarketUpdate, UpdateBlackMarketBoothRsp.Parser)).Result);
        Assert.Equal(before.Cut.CommitSequence, MarketEconomyEvidence.Cut(_dataDir, RepoRoot()).Cut.CommitSequence);
        Assert.Equal(999995, store.Get(guildId)!.Materials);
        var unchanged = Assert.Single(store.LoadMarket().Stalls);
        Assert.Equal((20L, 20L, 500L), (unchanged.RemainCount, unchanged.TotalCount, unchanged.UnitPrice));

        request.Confirm = true;
        await seller.SendGameAsync(GuildService.MarketUpdate, request);
        Assert.Equal(0, (await Next(seller, GuildService.MarketUpdate, UpdateBlackMarketBoothRsp.Parser)).Result);
        Assert.Equal(1000000, store.Get(guildId)!.Materials);
        var remaining = Assert.Single(store.LoadMarket().Stalls);
        Assert.Equal(stallId, remaining.Id);
        Assert.Equal((10L, 10L, 1000L), (remaining.RemainCount, remaining.TotalCount, remaining.UnitPrice));
        var ledger = MarketEconomyEvidence.Exact(_dataDir, RepoRoot(), before, MarketEconomyEvidence.Cut(_dataDir, RepoRoot()));
        var sink = Assert.Single(ledger.Legs, x => x.Kind == EconomyLegKind.Sink);
        Assert.Equal((EconomyReason.GuildMarketCapDiscard, -5L), (sink.Reason, sink.Delta));
        Assert.Contains(ledger.Legs, x => x.Location.Compartment == EconomyCompartment.GuildTreasury && x.Delta == 5);
        Assert.DoesNotContain(ledger.Legs, x => x.Kind == EconomyLegKind.Source);
    }

    [ClientDataFact]
    public async Task E03ConfirmedGuildPurchaseKeepsFullPriceAndRecordsCappedExcess()
    {
        await using var seller = await InWorld("CapVendor"); await using var buyer = await InWorld("CapBuyer");
        foreach (string name in new[] { "capvendor", "capbuyer" })
        {
            await Level(name);
            using var client = new HttpClient();
            (await client.PostAsync($"{_http}/ro3emu/gm?account={name}", new StringContent("!lvl 99"))).EnsureSuccessStatusCode();
        }
        long sellerId = await CreateGuild(seller, "Vendor"), buyerId = await CreateGuild(buyer, "Buyer");
        var store = _app.Services.GetRequiredService<GuildStore>();
        var guild = store.Get(sellerId)!; guild.Materials = 10; Assert.True(store.Save(guild));
        guild = store.Get(buyerId)!; guild.Money = 5000; guild.Materials = 999995; Assert.True(store.Save(guild));
        await seller.SendGameAsync(GuildService.MarketList, new ListOnBlackMarketReq { UnitPrice = 500, Count = 10 });
        Assert.Equal(0, (await Next(seller, GuildService.MarketList, ListOnBlackMarketRsp.Parser)).Result);
        var market = store.LoadMarket(); Assert.Single(market.Stalls).AvailableMs = 0; Assert.True(store.SaveMarket(market, []));
        var before = MarketEconomyEvidence.Cut(_dataDir, RepoRoot());
        var request = new BuyBlackMarketItemReq { UnitPrice = 500, BuyCount = 10 };
        await buyer.SendGameAsync(GuildService.MarketBuy, request);
        Assert.Equal(ErrorCodes.Parameter, (await Next(buyer, GuildService.MarketBuy, BuyBlackMarketItemRsp.Parser)).Result);
        Assert.Equal(before.Cut.CommitSequence, MarketEconomyEvidence.Cut(_dataDir, RepoRoot()).Cut.CommitSequence);
        Assert.Equal(5000, store.Get(buyerId)!.Money); Assert.Equal(10, Assert.Single(store.LoadMarket().Stalls).RemainCount);
        request.Confirm = true;
        await buyer.SendGameAsync(GuildService.MarketBuy, request);
        Assert.Equal(0, (await Next(buyer, GuildService.MarketBuy, BuyBlackMarketItemRsp.Parser)).Result);
        Assert.Equal(0, store.Get(buyerId)!.Money); Assert.Equal(1000000, store.Get(buyerId)!.Materials);
        Assert.Equal(4875, store.Get(sellerId)!.Money); Assert.Empty(store.LoadMarket().Stalls);
        var ledger = MarketEconomyEvidence.Exact(_dataDir, RepoRoot(), before, MarketEconomyEvidence.Cut(_dataDir, RepoRoot()));
        Assert.Equal(-125, ledger.Legs.Where(x => x.Reason == EconomyReason.GuildMarketFee).Sum(x => x.Delta));
        Assert.Equal(-5, ledger.Legs.Where(x => x.Reason == EconomyReason.GuildMarketCapDiscard).Sum(x => x.Delta));
        Assert.DoesNotContain(ledger.Legs, x => x.Kind == EconomyLegKind.Source);
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task E03GuildMarketCapRefusalConfirmationAndExpiryRecordOnlyActualDiscard(bool expire)
    {
        await using var seller = await InWorld("CapSeller"); await Level("capseller");
        using (var client = new HttpClient())
            (await client.PostAsync($"{_http}/ro3emu/gm?account=capseller", new StringContent("!lvl 99"))).EnsureSuccessStatusCode();
        long guildId = await CreateGuild(seller, "Caps");
        var store = _app.Services.GetRequiredService<GuildStore>();
        var state = store.Get(guildId)!; state.Materials = 10; Assert.True(store.Save(state));
        await seller.SendGameAsync(GuildService.MarketList, new ListOnBlackMarketReq { UnitPrice = 500, Count = 10 });
        Assert.Equal(0, (await Next(seller, GuildService.MarketList, ListOnBlackMarketRsp.Parser)).Result);
        var market = store.LoadMarket(); var stall = Assert.Single(market.Stalls);
        state = store.Get(guildId)!; state.Materials = 999995; Assert.True(store.Save(state));
        if (expire) { stall.ExpiresMs = 0; Assert.True(store.SaveMarket(market, [])); }
        var before = MarketEconomyEvidence.Cut(_dataDir, RepoRoot());
        if (expire)
        {
            await seller.SendGameAsync(GuildService.MarketItems, new GetBlackMarketItemsReq());
            await Next(seller, GuildService.MarketItems, GetBlackMarketItemsRsp.Parser);
        }
        else
        {
            await seller.SendGameAsync(GuildService.MarketUnlist, new UnlistFromBlackMarketReq { StallUniqueId = stall.Id });
            Assert.Equal(ErrorCodes.Parameter, (await Next(seller, GuildService.MarketUnlist, UnlistFromBlackMarketRsp.Parser)).Result);
            Assert.Equal(before.Cut.CommitSequence, MarketEconomyEvidence.Cut(_dataDir, RepoRoot()).Cut.CommitSequence);
            Assert.Equal(999995, store.Get(guildId)!.Materials);
            await seller.SendGameAsync(GuildService.MarketUnlist, new UnlistFromBlackMarketReq { StallUniqueId = stall.Id, Confirm = true });
            Assert.Equal(0, (await Next(seller, GuildService.MarketUnlist, UnlistFromBlackMarketRsp.Parser)).Result);
        }
        Assert.Equal(1000000, store.Get(guildId)!.Materials); Assert.Empty(store.LoadMarket().Stalls);
        var ledger = MarketEconomyEvidence.Exact(_dataDir, RepoRoot(), before, MarketEconomyEvidence.Cut(_dataDir, RepoRoot()));
        var sink = Assert.Single(ledger.Legs, x => x.Kind == EconomyLegKind.Sink);
        Assert.Equal((EconomyReason.GuildMarketCapDiscard, -5L), (sink.Reason, sink.Delta));
        Assert.Contains(ledger.Legs, x => x.Location.Compartment == EconomyCompartment.GuildTreasury && x.Delta == 5);
    }
}
