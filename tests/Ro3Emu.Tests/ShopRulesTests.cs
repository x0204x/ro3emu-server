using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>82332 prices with the Merchant's Discount skill, and purchase limits on the 05:00 game day of the realm zone.</summary>
public sealed class ShopRulesTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3emu-shop-rules-" + Guid.NewGuid().ToString("N"));
    private readonly RealmClock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private const int Daily = 300, Discounted = 14101; // [1100000,5] x10 per day; [2,10000] with _iMerchantDiscount 1

    /// <summary>A realm at UTC-03 (the test host runs at +08): the game day must follow the realm's zone.</summary>
    private sealed class RealmClock : TimeProvider
    {
        public static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Realm -03", TimeSpan.FromHours(-3), "Realm -03", "Realm -03");
        public DateTimeOffset Now = new(2026, 10, 13, 23, 30, 0, TimeSpan.FromHours(-3)); // Tuesday 23:30 realm time
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => Zone;
    }

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task<BotClient> Enter(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        return bot;
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private static async Task<BuyCommodityResp> Buy(BotClient bot, int commodity, int count = 1)
    {
        await bot.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = commodity, Count = count });
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(ShopService.BuyCommodity)
            && BuyCommodityResp.Parser.ParseFrom(e.Body).CommodityId == commodity, TimeSpan.FromSeconds(8));
        return BuyCommodityResp.Parser.ParseFrom(e.Body);
    }

    /// <summary>Test-only learning: a just-answered exchange can still publish its staged progress over a direct write.</summary>
    private async Task Learn(BotClient bot, int skill, int level)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            await Service<GameLoop>().InvokeAsync(() => { Session(bot).Progress!.Skills[skill] = level; return Task.CompletedTask; });
            await Task.Delay(50);
            if (await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Session(bot).Progress!.Skills.GetValueOrDefault(skill))) == level
                && attempt > 0) return;
        }
        throw new TimeoutException("skill level did not stick");
    }

    [ClientDataFact]
    public async Task MerchantDiscountLowersOnlyMerchantDiscountCommodities()
    {
        await using var bot = await Enter("Haggler");
        await Gm(bot, "!lvl 10"); await Gm(bot, "!item 2 100000"); await Gm(bot, "!item 1100000 100");
        var character = Service<CharacterService>();
        var session = Session(bot);

        long zeny = character.CountOf(session, 2);
        var full = await Buy(bot, Discounted);
        Assert.Equal(0, full.Result);
        Assert.Equal(10000, full.Price);
        Assert.Equal(zeny - 10000, character.CountOf(session, 2));

        // SkillConfig 1600105 _kSpSkillParam [2,500]: 5% off, LC_DAL_Shop.OnGetMerchantDiscount.
        await Learn(bot, 16001, 5);
        zeny = character.CountOf(session, 2);
        var cheaper = await Buy(bot, Discounted, 2);
        Assert.Equal(0, cheaper.Result);
        Assert.Equal(9500, cheaper.Price);
        Assert.Equal(zeny - 19000, character.CountOf(session, 2));

        await Learn(bot, 16001, 10); // [2,1000]: 10%
        Assert.Equal(9000, (await Buy(bot, Discounted)).Price);
        // A commodity without _iMerchantDiscount keeps its price.
        Assert.Equal(5, (await Buy(bot, Daily)).Price);
    }

    [ClientDataFact]
    public async Task DailyLimitResetsAtFiveInTheRealmZoneAndResyncsOnlineRoles()
    {
        await using var bot = await Enter("Shopper");
        await Gm(bot, "!item 1100000 200");
        Assert.Equal(0, (await Buy(bot, Daily, 10)).Result);
        Assert.Equal(ErrorCodes.PurchaseLimit, (await Buy(bot, Daily)).Result);

        _clock.Now = _clock.Now.AddHours(1); // Wednesday 00:30: still Tuesday's game day
        Assert.Equal(ErrorCodes.PurchaseLimit, (await Buy(bot, Daily)).Result);
        _clock.Now = _clock.Now.AddHours(4).AddMinutes(29); // 04:59
        Assert.Equal(ErrorCodes.PurchaseLimit, (await Buy(bot, Daily)).Result);

        _clock.Now = _clock.Now.AddMinutes(2); // 05:01: a new game day
        var resync = await bot.Inbox.WaitAsync(e => e.IsGame(ShopService.ShopSync)
            && DBShopInfoIncrement.Parser.ParseFrom(e.Body).CommodityBuyTimes.TryGetValue(Daily, out var c) && c.Del,
            TimeSpan.FromSeconds(8));
        Assert.NotNull(resync);
        var bought = await Buy(bot, Daily, 10);
        Assert.Equal(0, bought.Result);
        Assert.Equal(10, bought.BuyTimes);
        Assert.Equal("20261014", Session(bot).Shop!.Bought[Daily].Period);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
