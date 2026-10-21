using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Season-limited purchases follow the explicit cycle and commit with payment.</summary>
public sealed class ShopCycleTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3emu-shop-cycle-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private const int Offer = 24101;

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = new(new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Local));
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
            o.PlayBook = 101; o.SeasonStart = "2026-10-05";
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

    private async Task<BotClient> Enter(bool create = true, bool legacyCounter = false)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync("cyclealice", "pw"); await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync("CycleAlice")).RoleBaseInfo.Id : bot.Roles.Single().Id;
        if (create) Service<RoleStore>().SaveTower(id, new DBClimTower { LayerConfID = 10 }.ToByteArray());
        if (legacyCounter)
        {
            var shop = new ShopService.ShopState();
            shop.Bought[Offer] = new ShopService.Counter { Count = 1, Period = "" };
            Service<RoleStore>().SaveShop(id, JsonSerializer.SerializeToUtf8Bytes(shop));
        }
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser) where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id), TimeSpan.FromSeconds(8));
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account=cyclealice", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private static async Task<int> Buy(BotClient bot)
    {
        await bot.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = Offer, Count = 1 });
        return (await Next(bot, ShopService.BuyCommodity, BuyCommodityResp.Parser)).Result;
    }

    [ClientDataFact]
    public async Task LegacySeasonLimitSurvivesCycleOneAndResetsOnlyAfterNewLogin()
    {
        var bot = await Enter(legacyCounter: true);
        await Gm("!quest done 10105020"); await Gm("!item 1 30000");
        Assert.Equal(ErrorCodes.PurchaseLimit, await Buy(bot));
        Assert.Equal("S1", Session(bot).Shop!.Bought[Offer].Period);
        var season = Service<SeasonService>();
        var breakLocal = DateTimeOffset.FromUnixTimeSeconds(season.Times().SeasonBreakStartTime).LocalDateTime;
        var newStart = breakLocal.Date.AddDays(breakLocal.TimeOfDay == TimeSpan.Zero ? 0 : 1);
        _clock.Now = new DateTimeOffset(newStart);
        Assert.Contains("Season 2", season.AdvanceCycle(101, newStart));
        Assert.Equal(ErrorCodes.ShopClosed, await Buy(bot));
        await bot.DisposeAsync();

        await using var again = await Enter(create: false);
        var sync = await Next(again, ShopService.ShopSync, DBShopInfoIncrement.Parser);
        Assert.False(sync.CommodityBuyTimes.ContainsKey(Offer));
        Assert.Equal(0, await Buy(again));
        Assert.Equal("S2", Session(again).Shop!.Bought[Offer].Period);
        Assert.Equal(ErrorCodes.PurchaseLimit, await Buy(again));
    }

    [ClientDataFact]
    public async Task CounterSaveFailureRollsBackPaymentAndGrantedItem()
    {
        await using var bot = await Enter();
        await Gm("!quest done 10105020"); await Gm("!item 1 30000");
        var session = Session(bot);
        var character = Service<CharacterService>();
        long money = character.CountOf(session, 1), tokens = character.CountOf(session, 1250709);
        FixtureSql.Execute(Service<AccountStore>(),
            "CREATE TRIGGER fail_shop BEFORE UPDATE OF shop ON role BEGIN SELECT RAISE(ABORT, 'shop test'); END;");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => Service<GameLoop>().InvokeAsync(
            () => Service<ShopService>().BuyAsync(session,
                new BuyCommodityReq { CommodityId = Offer, Count = 1 })));
        Assert.Equal(money, character.CountOf(session, 1));
        Assert.Equal(tokens, character.CountOf(session, 1250709));
        Assert.False(session.Shop!.Bought.ContainsKey(Offer));
        FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_shop");
        Assert.Equal(0, await Buy(bot));
    }

    [ClientDataFact]
    public async Task FailureAfterCommitCannotReopenThePurchaseLimit()
    {
        await using var bot = await Enter();
        await Gm("!quest done 10105020"); await Gm("!item 1 30000");
        var session = Session(bot);
        var character = Service<CharacterService>();
        long money = character.CountOf(session, 1), tokens = character.CountOf(session, 1250709);
        Action<GameSession> failAfterCommit = _ => throw new IOException("injected post-commit notification failure");
        character.StateChanged.Add(failAfterCommit);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => Service<GameLoop>().InvokeAsync(() => Service<ShopService>().BuyAsync(session,
                new BuyCommodityReq { CommodityId = Offer, Count = 1 })));
        }
        finally { character.StateChanged.Remove(failAfterCommit); }
        Assert.Equal(money - 7500, character.CountOf(session, 1));
        Assert.Equal(tokens + 1, character.CountOf(session, 1250709));
        Assert.Equal(1, session.Shop!.Bought[Offer].Count);
        var persisted = JsonSerializer.Deserialize<ShopService.ShopState>(Service<RoleStore>().LoadShop(session.Role!.Id)!)!;
        Assert.Equal(1, persisted.Bought[Offer].Count);
        Assert.Equal(ErrorCodes.PurchaseLimit, await Buy(bot));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
