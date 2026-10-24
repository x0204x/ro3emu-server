using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Title ranks (TitleService): 5031 in full at login, 43102 claims the next TitleConfig rank by _kIndex when the base
/// level, the best power ever reached and the _kSpend Crests allow it, and the new rank travels as a 5031 increment.
/// </summary>
public sealed class TitleTests : IAsyncLifetime
{
    private const int CrestOfJourney = 1250801;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-title-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.OpenContent.Enabled = true; // function 14 is opened by OpenContent only
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
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

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    private static async Task<int> Claim(BotClient bot)
    {
        await bot.SendGameAsync(TitleService.Level, new TitleLevelReq());
        return (await Next(bot, TitleService.Level, TitleLevelRsp.Parser)).Result;
    }

    /// <summary>The best power ever reached, as PowerService keeps it.</summary>
    private Task SetBestPower(BotClient bot, long power) => Service<GameLoop>().InvokeAsync(() =>
    {
        var s = Session(bot);
        var current = PowerService.Current(s);
        s.Power = new PowerService.Snapshot(current, new PowerUnit { NormalPower = power }, new PlayerPowerPackage());
        return Task.CompletedTask;
    });

    [ClientDataFact]
    public async Task RanksAreClaimedInOrderAgainstLevelPowerAndCrests()
    {
        await using var alice = await InWorld("Alice");
        var login = await Next(alice, TitleService.Sync, DBTitleInfoIncrement.Parser, m => m.SyncAllMark);
        Assert.Equal(0, login.TitleId);

        // Rank 1 "Beginner" asks for nothing; it travels as an increment (the client's upgrade animation).
        Assert.Equal(0, await Claim(alice));
        Assert.Equal(1, (await Next(alice, TitleService.Sync, DBTitleInfoIncrement.Parser, m => !m.SyncAllMark)).TitleId);
        // Rank 2 needs base level 20 and power 5000.
        Assert.Equal(ErrorCodes.LevelTooLow, await Claim(alice));
        await Gm(alice, "!lvl 99");
        await SetBestPower(alice, 4999);
        Assert.Equal(ErrorCodes.Parameter, await Claim(alice));
        await SetBestPower(alice, 1_000_000);
        Assert.Equal(0, await Claim(alice));
        Assert.Equal(0, await Claim(alice)); // rank 3
        // Rank 4 spends two Crests of Journey.
        Assert.Equal(ErrorCodes.ItemsShort, await Claim(alice));
        await Gm(alice, $"!item {CrestOfJourney} 2");
        Assert.Equal(0, await Claim(alice));
        Assert.Equal(4, Session(alice).TitleInfo!.TitleId);
        Assert.DoesNotContain(Session(alice).Items, i => i.Entry == CrestOfJourney);
        // The rank is the role's public title and adds its _iPowerAdd to titleScore.
        Assert.Equal(4, Service<CharacterService>().TitleOf!(Session(alice)));
    }
}
