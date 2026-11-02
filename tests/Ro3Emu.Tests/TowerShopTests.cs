using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Spirit Tower's ordinary class-240 shop, using condition 1014.</summary>
public sealed class TowerShopTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-tower-shop-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication _app = null!;
    private string _http = "";

    private const int FirstOffer = 24101, SecondOffer = 24102, ThirdOffer = 24103, TowerToken = 1250709;

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, long? clearedConfig = null, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        if (clearedConfig is { } configId)
        {
            // (inferred fixture) A cleared floor is an earned, persisted tower state; tests do not fabricate a gameplay clear.
            _app.Services.GetRequiredService<RoleStore>().SaveTower(id, new DBClimTower { LayerConfID = configId }.ToByteArray());
        }
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string account, string line)
    {
        using var http = new HttpClient();
        var resp = await http.PostAsync($"{_http}/ro3emu/gm?account={account}", Operator.Command(line));
        resp.EnsureSuccessStatusCode();
    }

    private static async Task<BuyCommodityResp> Buy(BotClient bot, int commodityId)
    {
        await bot.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = commodityId, Count = 1, Price = 25000 });
        return await Next(bot, ShopService.BuyCommodity, BuyCommodityResp.Parser);
    }

    [ClientDataFact]
    public async Task ShopNeedsItsQuestAndAnActuallyClearedFloor()
    {
        await using var alice = await InWorld("Alice");
        Assert.True((await Next(alice, ShopService.ShopSync, DBShopInfoIncrement.Parser)).SyncAllMark);
        await Gm("alice", "!item 1 20000");

        Assert.Equal(ErrorCodes.ShopClosed, (await Buy(alice, FirstOffer)).Result);
        await Gm("alice", "!quest done 10105020");
        Assert.Equal(ErrorCodes.PurchaseRequirements, (await Buy(alice, FirstOffer)).Result);
    }

    [ClientDataTheory]
    [InlineData(9, FirstOffer)]
    [InlineData(10, SecondOffer)]
    [InlineData(999999, FirstOffer)]
    public async Task ADisplayFloorBelowTheBuyThresholdCannotPurchase(long clearedConfig, int commodityId)
    {
        await using var alice = await InWorld("Alice", clearedConfig);
        await Gm("alice", "!quest done 10105020");
        await Gm("alice", "!item 1 20000");
        Assert.Equal(ErrorCodes.PurchaseRequirements, (await Buy(alice, commodityId)).Result);
    }

    [ClientDataFact]
    public async Task FloorTenBuysTheFirstOfferOnceAndKeepsItsCounterAfterRelog()
    {
        await using (var alice = await InWorld("Alice", 10))
        {
            await Gm("alice", "!quest done 10105020");
            await Gm("alice", "!item 1 20000");
            var funded = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.Currency.ContainsKey(1) && m.Currency[1].All >= 20000);
            long before = funded.Currency[1].All;

            // At floor 10 the second offer's visibility lock (10) is met, but its buy condition still needs floor 20.
            Assert.Equal(ErrorCodes.PurchaseRequirements, (await Buy(alice, SecondOffer)).Result);
            var bought = await Buy(alice, FirstOffer);
            Assert.Equal((0, FirstOffer, 1, 7500, 1), (bought.Result, bought.CommodityId, bought.Count, bought.Price, bought.BuyTimes));
            // Current ShopService applies configured _iDiscount=3000 to 25000; the spec's literal 25000 example differs.
            var shop = await Next(alice, ShopService.ShopSync, DBShopInfoIncrement.Parser, m => !m.SyncAllMark && m.CommodityBuyTimes.ContainsKey(FirstOffer));
            Assert.Equal(1, shop.CommodityBuyTimes[FirstOffer].All);
            var items = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.Currency.ContainsKey(1) && m.Currency[1].All == before - bought.Price);
            Assert.Equal(before - bought.Price, items.Currency[1].All);
            Assert.Contains(items.PackInfo.Values,
                p => p.Inc?.Items.Values.Any(i => i.All?.ItemEntry == TowerToken) == true);

            Assert.Equal(ErrorCodes.PurchaseLimit, (await Buy(alice, FirstOffer)).Result);
        }

        await using var relog = await InWorld("Alice", create: false);
        var persisted = await Next(relog, ShopService.ShopSync, DBShopInfoIncrement.Parser);
        Assert.Equal(1, persisted.CommodityBuyTimes[FirstOffer].All);
        Assert.Equal(ErrorCodes.PurchaseLimit, (await Buy(relog, FirstOffer)).Result);
    }

    [ClientDataFact]
    public async Task FloorTwentyCanBuySecondOfferButNotThird()
    {
        await using var alice = await InWorld("Alice", 20);
        await Gm("alice", "!quest done 10105020");
        await Gm("alice", "!item 1 20000");
        Assert.Equal(ErrorCodes.PurchaseRequirements, (await Buy(alice, ThirdOffer)).Result);
        Assert.Equal(0, (await Buy(alice, SecondOffer)).Result);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

}
