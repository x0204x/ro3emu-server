using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>The cash shop (2001-2004) with Ro3Emu:Shop:FreeProducts: top-ups, month cards and the pass without payment.</summary>
public sealed class PaidProductsTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-products-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private readonly TestClock _clock = new();
    private WebApplication? _app;
    private string _http = "";
    private T Service<T>() where T : notnull => _app!.Services.GetRequiredService<T>();

    private sealed class TestClock : TimeProvider
    {
        private long _seconds = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public void AdvanceDays(int count) => Interlocked.Add(ref _seconds, count * 86400L);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    private async Task StartAsync(bool free = true)
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.PlayBook = 101; o.SeasonStart = "2026-10-05";
            o.Shop.FreeProducts = free;
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        if (_app != null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        return bot;
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    private long Count(BotClient bot, int entry) => Service<CharacterService>().CountOf(Session(bot), entry);
    private Task Reconcile(BotClient bot) => Service<NewActivityService>().ReconcileAsync(Session(bot));

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", Operator.Command(command))).EnsureSuccessStatusCode();
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait)).Body);

    private static async Task<CreateOrderResp> Buy(BotClient bot, long type, long id)
    {
        await bot.SendGameAsync(NewActivityService.CreateOrder, new CreateOrderReq { ChargeType = type, ChargeId = id, Subject = "test" });
        return await Next(bot, NewActivityService.CreateOrder, CreateOrderResp.Parser);
    }

    private static async Task<long[]> FirstAwards(BotClient bot)
    {
        await bot.SendGameAsync(NewActivityService.FirstRechargeAward, new GetRechargeFirstAwardReq());
        var response = await Next(bot, NewActivityService.FirstRechargeAward, GetRechargeFirstAwardResp.Parser);
        Assert.Equal(0, response.Result);
        return [.. response.IdList];
    }

    [ClientDataFact]
    public async Task TopUpsPayTheirPackageWithTheFirstPurchaseBonusOnceAndAreCappedPerDay()
    {
        await StartAsync();
        await using var bot = await InWorld("Spender");
        var package = RechargeConfig.All(Service<GameData>()).First(r => r.Platform == 3 && r.AwardBonus.Length >= 2);
        Assert.Contains(package.Id, await FirstAwards(bot));
        long crystal = Count(bot, (int)package.Award[0]), bound = Count(bot, (int)package.FirstChargeAward[0]);

        var first = await Buy(bot, 1, package.Id);
        Assert.Equal(0, first.Result);
        Assert.Equal("{}", first.OrderData);
        Assert.NotEmpty(first.OrderId);
        Assert.Equal(package.Id, (await Next(bot, NewActivityService.FirstRechargeUpdate, UpdateRechargeFirstAwardResp.Parser)).RechargeId);
        Assert.Equal(crystal + package.Award[1], Count(bot, (int)package.Award[0]));
        Assert.Equal(bound + package.FirstChargeAward[1], Count(bot, (int)package.FirstChargeAward[0]));
        Assert.DoesNotContain(package.Id, await FirstAwards(bot));

        var again = await Buy(bot, 1, package.Id);
        Assert.Equal(ErrorCodes.TopUpLimit, again.Result);
        Assert.Equal("Purchase limit reached", again.ErrMsg);

        _clock.AdvanceDays(1);
        bound = Count(bot, (int)package.AwardBonus[0]);
        Assert.Equal(0, (await Buy(bot, 1, package.Id)).Result);
        Assert.Equal(crystal + 2 * package.Award[1], Count(bot, (int)package.Award[0]));
        Assert.Equal(bound + package.AwardBonus[1], Count(bot, (int)package.AwardBonus[0]));

        long mobile = RechargeConfig.All(Service<GameData>()).First(r => r.Platform != 3).Id;
        Assert.Equal(ErrorCodes.TopUpOption, (await Buy(bot, 1, mobile)).Result);
        Assert.Equal(ErrorCodes.TopUpType, (await Buy(bot, 9, package.Id)).Result);
    }

    [ClientDataFact]
    public async Task MonthCardProductsStartTheCardWithItsBuyAwardUpToTheDayLimit()
    {
        await StartAsync();
        await using var bot = await InWorld("Subscriber");
        await Gm(bot, "!lvl 20");
        await Reconcile(bot);
        var card = MonthCardConfig.All(Service<GameData>()).First();
        long crystal = Count(bot, 3);
        Assert.Equal(0, (await Buy(bot, 2, card.Product)).Result);
        var month = Session(bot).NewActivities!.ActivityInfo[199].ActivityParam.MonthCard;
        Assert.Equal(card.Day + 1, month.Infos[card.Id].LastDays);
        Assert.Equal(0, Count(bot, (int)card.Item));
        Assert.True(Count(bot, 3) > crystal); // _iBuyAward

        int bought = 1;
        while ((await Buy(bot, 2, card.Product)).Result == 0) bought++;
        Assert.Equal((int)((card.DayLimit - 1) / card.Day), bought);
        Assert.Equal(ErrorCodes.TopUpLimit, (await Buy(bot, 2, card.Product)).Result);
    }

    [ClientDataFact]
    public async Task ThePassProductOpensThePremiumTrackOnceAndTheGiftTokenIsOneAtATime()
    {
        await StartAsync();
        await using var bot = await InWorld("Patron");
        await Gm(bot, "!lvl 99");
        await Reconcile(bot);
        Assert.False(Session(bot).NewActivities!.ActivityInfo[200].ActivityParam.BattlePass.Premium);
        // enum_ProductId.BattlePass: one premium item per open pass that takes it (Pass I and II share item 703).
        var takers = BattlePassConfig.All(Service<GameData>()).Where(p => p.PremiumItemId == 703
            && Session(bot).NewActivities!.ActivityInfo.ContainsKey(p.Id)).ToList();
        long result;
        int bought = 0;
        while ((result = (await Buy(bot, 2, 7)).Result) == 0) bought++;
        Assert.Equal(ErrorCodes.AlreadyPurchased, result);
        Assert.InRange(bought, 1, takers.Count);
        Assert.True(Session(bot).NewActivities!.ActivityInfo[200].ActivityParam.BattlePass.Premium);
        Assert.Equal(0, Count(bot, 703));

        Assert.Equal(0, (await Buy(bot, 2, 8)).Result); // BattlePass_GIFT
        Assert.Equal(1, Count(bot, 1020001));
        Assert.Equal(ErrorCodes.TopUpLimit, (await Buy(bot, 2, 8)).Result);
    }

    [ClientDataFact]
    public async Task WithFreeProductsOffEveryOrderIsUnavailable()
    {
        await StartAsync(free: false);
        await using var bot = await InWorld("Window");
        long crystal = Count(bot, 3);
        var refused = await Buy(bot, 1, RechargeConfig.All(Service<GameData>()).First(r => r.Platform == 3).Id);
        Assert.Equal(ErrorCodes.FeatureUnavailable, refused.Result);
        Assert.NotEmpty(refused.ErrMsg);
        Assert.Equal(crystal, Count(bot, 3));
        await bot.SendGameAsync(NewActivityService.FirstRechargeAward, new GetRechargeFirstAwardReq());
        Assert.Equal(ErrorCodes.FeatureUnavailable,
            (await Next(bot, NewActivityService.FirstRechargeAward, GetRechargeFirstAwardResp.Parser)).Result);
    }
}
