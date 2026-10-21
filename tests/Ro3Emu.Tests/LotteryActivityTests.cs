using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Ticket-only 83410 draw, 5011 state, durable cap and a guaranteed pet over the game gate.</summary>
public sealed partial class LotteryActivityTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-lottery-" + Guid.NewGuid().ToString("N"));
    private readonly FixedClock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";

    private sealed class FixedClock : TimeProvider
    {
        private long _milliseconds = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _milliseconds));
        public void AdvanceDays(int days) => Interlocked.Add(ref _milliseconds, days * 86400000L);
        public void Set(DateTimeOffset value) => Interlocked.Exchange(ref _milliseconds, value.ToUnixTimeMilliseconds());
    }

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], options =>
        {
            options.DataDir = _dataDir;
            options.GameDataDir = Path.Combine(RepoRoot(), "data");
            options.HttpPort = httpPort;
            options.MicroGatePort = microPort;
            options.GameGatePort = gamePort;
            options.PlayBook = 101;
            options.SeasonStart = "2026-10-05";
        }, _clock);
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

    private async Task<BotClient> InWorld(bool create)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("lottery", "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync("Lottery")).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser,
        Func<T, bool>? match = null) where T : Google.Protobuf.IMessage<T>
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), TimeSpan.FromSeconds(5));
        return parser.ParseFrom(packet.Body);
    }

    private async Task Gm(string line)
    {
        using var client = new HttpClient();
        var response = await client.PostAsync($"{_http}/ro3emu/gm?account=lottery", new StringContent(line));
        response.EnsureSuccessStatusCode();
    }

    [ClientDataFact]
    public async Task RoleSwitchKeepsEachLotteryCounterWithItsOwnRole()
    {
        await using var bot = await InWorld(create: true);
        var firstRole = _app.Services.GetRequiredService<SessionRegistry>().All()
            .Single(s => s.Role?.Name == "Lottery").Role!.Id;
        await Next(bot, LotteryService.LotterySync, DBLotteryDataIncrement.Parser, sync => sync.SyncAllMark);
        await Gm("!lvl 99");
        await Gm("!item 1250717 1");
        await bot.SendGameAsync(LotteryService.DrawRequest,
            new LotteryReq { PoolId = 201, Type = ELotteryPoolType.GachaCountMin });
        var drawn = await Next(bot, LotteryService.DrawRequest, LotteryResp.Parser, r => r.PoolData.PoolId == 201);
        Assert.Equal(0, drawn.Result);
        Assert.Equal(1, drawn.PoolData.DailyCount);

        await bot.SwitchRoleAsync();
        var second = await bot.CreateRoleAsync("LotteryTwo");
        Assert.Equal(0, second.Result);
        await bot.EnterWorldAsync(second.RoleBaseInfo.Id);
        var secondFull = await Next(bot, LotteryService.LotterySync, DBLotteryDataIncrement.Parser, sync => sync.SyncAllMark);
        Assert.Equal(0, secondFull.LotteryPool[201].All.DailyCount);
        var current = _app.Services.GetRequiredService<SessionRegistry>().All()
            .Single(s => s.Role?.Id == second.RoleBaseInfo.Id);
        Assert.Empty(current.Pets!.ListPet);
        var secondBlob = _app.Services.GetRequiredService<RoleStore>().LoadLottery(second.RoleBaseInfo.Id);
        Assert.NotNull(secondBlob);
        using (var envelope = System.Text.Json.JsonDocument.Parse(secondBlob))
        {
            var data = Convert.FromBase64String(envelope.RootElement.GetProperty("Data").GetString()!);
            Assert.Equal(0, DBLotteryData.Parser.ParseFrom(data).LotteryPool[201].DailyCount);
        }

        await bot.SwitchRoleAsync();
        await bot.EnterWorldAsync(firstRole);
        var firstAgain = await Next(bot, LotteryService.LotterySync, DBLotteryDataIncrement.Parser, sync => sync.SyncAllMark);
        Assert.Equal(1, firstAgain.LotteryPool[201].All.DailyCount);
    }

    [ClientDataFact]
    public async Task EventBannerDrawUsesOwnedTicketsAndResetsDailyCountOnReconnect()
    {
        await using var bot = await InWorld(create: true);
        var full = await Next(bot, LotteryService.LotterySync, DBLotteryDataIncrement.Parser);
        Assert.True(full.SyncAllMark);
        Assert.True(full.LotteryPool.ContainsKey(201));

        await bot.SendGameAsync(LotteryService.DrawRequest, new LotteryReq { PoolId = 201, Type = ELotteryPoolType.GachaCountMin });
        var locked = await Next(bot, LotteryService.DrawRequest, LotteryResp.Parser);
        Assert.NotEqual(0, locked.Result);
        Assert.NotNull(locked.PoolData);
        Assert.False(locked.PoolData.IsOpen);

        await Gm("!lvl 99");
        await Gm("!item 1250717 2");
        await bot.SendGameAsync(LotteryService.DrawRequest, new LotteryReq { PoolId = 201, Type = ELotteryPoolType.GachaCountMin });
        var first = await Next(bot, LotteryService.DrawRequest, LotteryResp.Parser, r => r.PoolData.DailyCount == 1);
        Assert.Equal(0, first.Result);
        Assert.Single(first.ItemList);
        Assert.False(first.ItemList[0].IsPet);

        await bot.SendGameAsync(LotteryService.DrawRequest, new LotteryReq { PoolId = 201, Type = ELotteryPoolType.GachaCountMin });
        var second = await Next(bot, LotteryService.DrawRequest, LotteryResp.Parser, r => r.PoolData.DailyCount == 2);
        Assert.Equal(0, second.Result);

        await bot.SendGameAsync(LotteryService.DrawRequest, new LotteryReq { PoolId = 201, Type = ELotteryPoolType.GachaCountMin });
        var shortTickets = await Next(bot, LotteryService.DrawRequest, LotteryResp.Parser, r => r.Result != 0);
        Assert.Equal(ErrorCodes.ItemsShort, shortTickets.Result);
        Assert.Equal(2, shortTickets.PoolData.DailyCount);

        await bot.DisposeAsync();
        await using var reconnect = await InWorld(create: false);
        var saved = await Next(reconnect, LotteryService.LotterySync, DBLotteryDataIncrement.Parser);
        Assert.Equal(2, saved.LotteryPool[201].All.DailyCount);
        _clock.AdvanceDays(1);
        await reconnect.DisposeAsync();
        await using var nextDay = await InWorld(create: false);
        var reset = await Next(nextDay, LotteryService.LotterySync, DBLotteryDataIncrement.Parser);
        Assert.Equal(0, reset.LotteryPool[201].All.DailyCount);
    }

    [ClientDataFact]
    public async Task TwentyDrawBannerGuaranteesPetAndLifetimeCapSurvivesReconnect()
    {
        await using var bot = await InWorld(create: true);
        await Gm("!lvl 99");
        await Gm("!quest done 10105010");
        await Gm("!item 1250709 21");
        var registry = _app.Services.GetRequiredService<SessionRegistry>();
        var session = registry.All().Single(s => s.Role?.Name == "Lottery");
        int initialTickets = session.Items.Where(i => i.Entry == 1250709).Sum(i => i.Stack);
        Assert.True(initialTickets >= 20);

        for (int index = 1; index <= 2; index++)
        {
            await bot.SendGameAsync(LotteryService.DrawRequest, new LotteryReq { PoolId = 200, Type = ELotteryPoolType.GachaCountMid });
            var result = await Next(bot, LotteryService.DrawRequest, LotteryResp.Parser, r => r.PoolData.PoolId == 200 && r.PoolData.DailyCount == index * 10);
            Assert.Equal(0, result.Result);
            Assert.Equal(10, result.ItemList.Count);
        }
        Assert.NotEmpty(session.Pets!.ListPet);
        int remaining = session.Items.Where(i => i.Entry == 1250709).Sum(i => i.Stack);
        Assert.Equal(initialTickets - 20, remaining);

        await bot.SendGameAsync(LotteryService.DrawRequest, new LotteryReq { PoolId = 200, Type = ELotteryPoolType.GachaCountMin });
        var capped = await Next(bot, LotteryService.DrawRequest, LotteryResp.Parser, r => r.Result != 0 && r.PoolData.PoolId == 200);
        Assert.Equal(ErrorCodes.AttemptsExhausted, capped.Result);
        Assert.Equal(20, capped.PoolData.DailyCount);
        Assert.Equal(remaining, session.Items.Where(i => i.Entry == 1250709).Sum(i => i.Stack));

        await bot.DisposeAsync();
        await using var reconnect = await InWorld(create: false);
        var saved = await Next(reconnect, LotteryService.LotterySync, DBLotteryDataIncrement.Parser);
        Assert.Equal(20, saved.LotteryPool[200].All.DailyCount);
        var joined = registry.All().Single(s => s.Role?.Name == "Lottery" && !ReferenceEquals(s, session));
        Assert.NotEmpty(joined.Pets!.ListPet);
        await reconnect.SendGameAsync(LotteryService.DrawRequest, new LotteryReq { PoolId = 200, Type = ELotteryPoolType.GachaCountMin });
        Assert.Equal(ErrorCodes.AttemptsExhausted,
            (await Next(reconnect, LotteryService.DrawRequest, LotteryResp.Parser, r => r.Result != 0)).Result);
    }

    private static string RepoRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path != null && !Directory.Exists(Path.Combine(path.FullName, "data", "config"))) path = path.Parent;
        return path?.FullName ?? throw new DirectoryNotFoundException("Repository data/config");
    }
}
