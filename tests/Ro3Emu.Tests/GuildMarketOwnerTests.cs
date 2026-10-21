using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

public sealed partial class GuildTests
{
    private readonly SwitchableClock _clock = new();

    private sealed class SwitchableClock : TimeProvider
    {
        private long _frozenTicks = long.MinValue;
        public override DateTimeOffset GetUtcNow()
        {
            long ticks = Volatile.Read(ref _frozenTicks);
            return ticks == long.MinValue ? TimeProvider.System.GetUtcNow() : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        public void Freeze() => Interlocked.Exchange(ref _frozenTicks, TimeProvider.System.GetUtcNow().Ticks);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _frozenTicks, by.Ticks);
    }

    [ClientDataFact]
    public async Task MarketListUsesExpiryRefundBeforeCheckingOwnMaterials()
    {
        await using var seller = await InWorld("Refundseller");
        await Level("refundseller");
        using (var client = new HttpClient())
            (await client.PostAsync($"{_http}/ro3emu/gm?account=refundseller", new StringContent("!lvl 99")))
                .EnsureSuccessStatusCode();
        long guildId = await CreateGuild(seller, "Refund");
        var store = _app.Services.GetRequiredService<GuildStore>();
        var funded = store.Get(guildId)!;
        funded.Materials = 10;
        Assert.True(store.Save(funded));

        await seller.SendGameAsync(GuildService.MarketList, new ListOnBlackMarketReq { UnitPrice = 500, Count = 10 });
        Assert.Equal(0, (await Next(seller, GuildService.MarketList, ListOnBlackMarketRsp.Parser)).Result);
        Assert.Equal(0, store.Get(guildId)!.Materials);
        var market = store.LoadMarket();
        string oldId = Assert.Single(market.Stalls).Id;
        market.Stalls[0].ExpiresMs = 0;
        market.LastListedMs[guildId] = 0;
        Assert.True(store.SaveMarket(market, []));

        await seller.SendGameAsync(GuildService.MarketList, new ListOnBlackMarketReq { UnitPrice = 500, Count = 10 });
        Assert.Equal(0, (await Next(seller, GuildService.MarketList, ListOnBlackMarketRsp.Parser)).Result);
        Assert.Equal(0, store.Get(guildId)!.Materials);
        Assert.NotEqual(oldId, Assert.Single(store.LoadMarket().Stalls).Id);
    }

    [ClientDataFact]
    public async Task MarketMultiSellerSqlFailureRollsBackAllBalancesAndBookThenRetrySucceeds()
    {
        await using var first = await InWorld("Firstseller");
        await using var second = await InWorld("Secondseller");
        await using var buyer = await InWorld("Marketbuyer");
        foreach (string name in new[] { "firstseller", "secondseller", "marketbuyer" })
        {
            await Level(name);
            using var client = new HttpClient();
            (await client.PostAsync($"{_http}/ro3emu/gm?account={name}", new StringContent("!lvl 99")))
                .EnsureSuccessStatusCode();
        }
        long firstId = await CreateGuild(first, "First");
        long secondId = await CreateGuild(second, "Second");
        long buyerId = await CreateGuild(buyer, "Buyers");
        var store = _app.Services.GetRequiredService<GuildStore>();
        foreach (long guildId in new[] { firstId, secondId })
        {
            var guild = store.Get(guildId)!;
            guild.Materials = 10;
            Assert.True(store.Save(guild));
        }
        var funded = store.Get(buyerId)!;
        funded.Money = 10000;
        Assert.True(store.Save(funded));
        foreach (var seller in new[] { first, second })
        {
            await seller.SendGameAsync(GuildService.MarketList, new ListOnBlackMarketReq { UnitPrice = 500, Count = 10 });
            Assert.Equal(0, (await Next(seller, GuildService.MarketList, ListOnBlackMarketRsp.Parser)).Result);
        }
        var market = store.LoadMarket();
        Assert.Equal(2, market.Stalls.Count);
        foreach (var stall in market.Stalls) stall.AvailableMs = 0;
        Assert.True(store.SaveMarket(market, []));

        var accounts = _app.Services.GetRequiredService<AccountStore>();
        var session = _app.Services.GetRequiredService<SessionRegistry>().All()
            .Single(x => x.Role?.Id == (long)buyer.Self!.Uuid);
        var request = new BuyBlackMarketItemReq { UnitPrice = 500, BuyCount = 20 };
        FixtureSql.Execute(accounts,
            $"CREATE TRIGGER fail_market_seller BEFORE UPDATE OF state ON guild WHEN NEW.id={secondId} "
            + "BEGIN SELECT RAISE(ABORT, 'market seller failure'); END");
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => _app.Services.GetRequiredService<GameLoop>()
                .InvokeAsync(() => _app.Services.GetRequiredService<GuildService>()
                    .MarketAsync(session, GuildService.MarketBuy, request.ToByteArray())));
            Assert.Equal(10000, store.Get(buyerId)!.Money);
            Assert.Equal(0, store.Get(buyerId)!.Materials);
            Assert.Equal(0, store.Get(firstId)!.Money);
            Assert.Equal(0, store.Get(secondId)!.Money);
            Assert.Equal(2, store.LoadMarket().Stalls.Count);
            Assert.Empty(store.LoadMarket().Trades);
        }
        finally { FixtureSql.Execute(accounts, "DROP TRIGGER fail_market_seller"); }

        var openingEconomy = MarketEconomyEvidence.Cut(_dataDir, RepoRoot());
        await buyer.SendGameAsync(GuildService.MarketBuy, request);
        Assert.Equal(0, (await Next(buyer, GuildService.MarketBuy, BuyBlackMarketItemRsp.Parser)).Result);
        Assert.Equal(0, store.Get(buyerId)!.Money);
        Assert.Equal(20, store.Get(buyerId)!.Materials);
        Assert.Equal(4875, store.Get(firstId)!.Money);
        Assert.Equal(4875, store.Get(secondId)!.Money);
        Assert.Equal(firstId, store.ForRole((long)first.Self!.Uuid)!.Id);
        Assert.Equal(secondId, store.ForRole((long)second.Self!.Uuid)!.Id);
        Assert.Empty(store.LoadMarket().Stalls);
        Assert.Equal(2, store.LoadMarket().Trades.Count);
        var ledger = MarketEconomyEvidence.Exact(_dataDir, RepoRoot(), openingEconomy,
            MarketEconomyEvidence.Cut(_dataDir, RepoRoot()));
        Assert.Equal(-250, ledger.Legs.Where(x => x.Reason == Ro3Emu.Server.Economy.EconomyReason.GuildMarketFee).Sum(x => x.Delta));
        Assert.DoesNotContain(ledger.Legs, x => x.Kind == Ro3Emu.Server.Economy.EconomyLegKind.Source);
    }

    [ClientDataFact]
    public async Task HeldBuyDefersSellerExpiryRefundWhileUnrelatedRoleSavesSettings()
    {
        await using var seller = await InWorld("Timerseller");
        await using var buyer = await InWorld("Timerbuyer");
        await using var unrelated = await InWorld("Bystander");
        // EnterWorld returns before SceneLoaded's final Realm publication. Drain that
        // same-connection request before holding the guild reservation below.
        await unrelated.SendGameAsync(CharacterService.ItemUse,
            new ItemUseReq { ItemGuid = long.MaxValue, ItemCount = 1 });
        Assert.Equal(ErrorCodes.ItemMissing,
            (await Next(unrelated, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        foreach (string name in new[] { "timerseller", "timerbuyer" })
        {
            await Level(name);
            using var client = new HttpClient();
            (await client.PostAsync($"{_http}/ro3emu/gm?account={name}", new StringContent("!lvl 99")))
                .EnsureSuccessStatusCode();
        }
        long sellerId = await CreateGuild(seller, "Timers");
        long buyerId = await CreateGuild(buyer, "Payees");
        var store = _app.Services.GetRequiredService<GuildStore>();
        var fundedSeller = store.Get(sellerId)!;
        fundedSeller.Materials = 10;
        Assert.True(store.Save(fundedSeller));
        var fundedBuyer = store.Get(buyerId)!;
        fundedBuyer.Money = 5000;
        Assert.True(store.Save(fundedBuyer));
        await seller.SendGameAsync(GuildService.MarketList, new ListOnBlackMarketReq { UnitPrice = 500, Count = 10 });
        Assert.Equal(0, (await Next(seller, GuildService.MarketList, ListOnBlackMarketRsp.Parser)).Result);
        var market = store.LoadMarket();
        _clock.Freeze();
        long expires = _clock.GetUtcNow().ToUnixTimeMilliseconds() + 3000;
        Assert.Single(market.Stalls).AvailableMs = 0;
        market.Stalls[0].ExpiresMs = expires;
        Assert.True(store.SaveMarket(market, []));

        var accounts = _app.Services.GetRequiredService<AccountStore>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var owners = _app.Services.GetRequiredService<OwnerRegistry>();
        double queueAgeBefore = owners.MaxQueueAgeMs;
        double queueWaitBefore = owners.TotalQueueWaitMs;
        var guilds = _app.Services.GetRequiredService<GuildService>();
        var sellerSession = _app.Services.GetRequiredService<SessionRegistry>().All()
            .Single(x => x.Role?.Id == (long)seller.Self!.Uuid);
        var reachedCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int held = 0;
        accounts.BeforeOwnedCommitForTest = async label =>
        {
            if (label != "guild/market" || Interlocked.Exchange(ref held, 1) != 0) return;
            reachedCommit.TrySetResult();
            await releaseCommit.Task;
        };
        Task? refund = null;
        try
        {
            await buyer.SendGameAsync(GuildService.MarketBuy,
                new BuyBlackMarketItemReq { UnitPrice = 500, BuyCount = 10 });
            await reachedCommit.Task.WaitAsync(Wait);
            Assert.True(_clock.GetUtcNow().ToUnixTimeMilliseconds() < expires);
            _clock.Advance(TimeSpan.FromSeconds(4));
            long before = await loop.InvokeAsync(() => Task.FromResult(owners.Deferred));
            refund = loop.InvokeAsync(() => guilds.MarketAsync(sellerSession, GuildService.MarketBooth,
                ReadOnlyMemory<byte>.Empty));
            long after = await loop.InvokeAsync(() => Task.FromResult(owners.Deferred));
            Assert.True(after > before);
            Assert.False(refund.IsCompleted);

            await unrelated.SendGameBytesAsync(MsgId.SaveSetting, [0x0a, 1, 5, 0x12, 4, 0x2a, 2, 16, 1]);
            Assert.Equal(0, (await Next(unrelated, MsgId.SaveSetting, SaveSettingResp.Parser)).Result);
            Assert.False(refund.IsCompleted);
        }
        finally
        {
            releaseCommit.TrySetResult();
            accounts.BeforeOwnedCommitForTest = null;
        }
        if (refund != null) await refund.WaitAsync(Wait);
        // This refund must record a real wait; it need not exceed an earlier login's maximum.
        Assert.True(owners.TotalQueueWaitMs > queueWaitBefore);
        Assert.True(owners.MaxQueueAgeMs >= queueAgeBefore);
        Assert.Equal(0, (await Next(buyer, GuildService.MarketBuy, BuyBlackMarketItemRsp.Parser)).Result);
        Assert.Equal(0, store.Get(sellerId)!.Materials);
        Assert.Equal(4875, store.Get(sellerId)!.Money);
        Assert.Equal(10, store.Get(buyerId)!.Materials);
        Assert.Equal(0, store.Get(buyerId)!.Money);
        Assert.Empty(store.LoadMarket().Stalls);
        Assert.Single(store.LoadMarket().Trades);
    }

    [ClientDataFact]
    public async Task SlowMarketNoticeCompletionDoesNotReorderLaterGuildResources()
    {
        await using var seller = await InWorld("Slowseller");
        await using var buyer = await InWorld("Slowbuyer");
        foreach (string name in new[] { "slowseller", "slowbuyer" })
        {
            await Level(name);
            using var client = new HttpClient();
            (await client.PostAsync($"{_http}/ro3emu/gm?account={name}", new StringContent("!lvl 99")))
                .EnsureSuccessStatusCode();
        }
        long sellerId = await CreateGuild(seller, "Slows");
        long buyerId = await CreateGuild(buyer, "PaySlow");
        var store = _app.Services.GetRequiredService<GuildStore>();
        var fundedSeller = store.Get(sellerId)!;
        fundedSeller.Materials = 20;
        Assert.True(store.Save(fundedSeller));
        var fundedBuyer = store.Get(buyerId)!;
        fundedBuyer.Money = 10000;
        Assert.True(store.Save(fundedBuyer));
        await seller.SendGameAsync(GuildService.MarketList, new ListOnBlackMarketReq { UnitPrice = 500, Count = 20 });
        Assert.Equal(0, (await Next(seller, GuildService.MarketList, ListOnBlackMarketRsp.Parser)).Result);
        var market = store.LoadMarket();
        Assert.Single(market.Stalls).AvailableMs = 0;
        Assert.True(store.SaveMarket(market, []));

        int buyerStart = buyer.Inbox.Snapshot().Count, sellerStart = seller.Inbox.Snapshot().Count;
        var firstNoticeSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstNotice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int held = 0;
        var service = _app.Services.GetRequiredService<GuildService>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var buyerSession = _app.Services.GetRequiredService<SessionRegistry>().All()
            .Single(x => x.Role?.Id == (long)buyer.Self!.Uuid);
        service.AfterMarketNoticeSendForTest = async () =>
        {
            if (Interlocked.Exchange(ref held, 1) != 0) return;
            firstNoticeSent.TrySetResult();
            await releaseFirstNotice.Task;
        };
        Task? firstBuy = null;
        try
        {
            var buy = new BuyBlackMarketItemReq { UnitPrice = 500, BuyCount = 10 };
            firstBuy = loop.InvokeAsync(() => service.MarketAsync(buyerSession, GuildService.MarketBuy, buy.ToByteArray()));
            await firstNoticeSent.Task.WaitAsync(Wait);
            await buyer.SendGameAsync(GuildService.MarketBuy, buy);
            Assert.Equal(20, (await Next(buyer, GuildService.BaseChange, GuildBaseInfoChangeNtf.Parser,
                x => x.Resource?.Materials == 20)).Resource.Materials);
            Assert.Equal(9750, (await Next(seller, GuildService.BaseChange, GuildBaseInfoChangeNtf.Parser,
                x => x.Resource?.Money == 9750)).Resource.Money);
            Assert.Equal(0, (await Next(buyer, GuildService.MarketBuy, BuyBlackMarketItemRsp.Parser)).Result);
            Assert.Equal(1, buyer.Inbox.Snapshot().Skip(buyerStart).Count(x => x.IsGame(GuildService.MarketBuy)));
            Assert.Equal(new long[] { 10, 20 }, buyer.Inbox.Snapshot().Skip(buyerStart)
                .Where(x => x.IsGame(GuildService.BaseChange))
                .Select(x => GuildBaseInfoChangeNtf.Parser.ParseFrom(x.Body))
                .Where(x => x.Resource != null).Select(x => x.Resource.Materials).ToArray());
            Assert.Equal(new long[] { 4875, 9750 }, seller.Inbox.Snapshot().Skip(sellerStart)
                .Where(x => x.IsGame(GuildService.BaseChange))
                .Select(x => GuildBaseInfoChangeNtf.Parser.ParseFrom(x.Body))
                .Where(x => x.Resource != null).Select(x => x.Resource.Money).ToArray());
            Assert.Equal(0, store.Get(buyerId)!.Money);
            Assert.Equal(20, store.Get(buyerId)!.Materials);
            Assert.Equal(9750, store.Get(sellerId)!.Money);
            Assert.Empty(store.LoadMarket().Stalls);
            Assert.Equal(2, store.LoadMarket().Trades.Count);
        }
        finally
        {
            releaseFirstNotice.TrySetResult();
            service.AfterMarketNoticeSendForTest = null;
        }
        await firstBuy!.WaitAsync(Wait);
        Assert.Equal(0, (await Next(buyer, GuildService.MarketBuy, BuyBlackMarketItemRsp.Parser)).Result);
    }
}
