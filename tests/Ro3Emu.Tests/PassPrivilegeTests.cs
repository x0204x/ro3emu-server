using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>BattlePassPrivilegeConfig: the advanced pass privileges apply to stalls, Trade Exchange favourites and drops.</summary>
public sealed class PassPrivilegeTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-privilege-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    /// <summary>Inside Pass I's window for the season below.</summary>
    private sealed class TestClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    }

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.PlayBook = 101; o.SeasonStart = "2026-10-05";
            o.Pass.FreePremium = true;
        }, new TestClock());
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

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", Operator.Command(command))).EnsureSuccessStatusCode();
    }

    private static async Task<int> Follow(BotClient bot, int item)
    {
        await bot.SendGameAsync(TradeService.Follow, new TradeAttionReq { OpType = 1, AttionType = 2, Id = $"item-{item}" });
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(TradeService.Follow) && TradeAttionResp.Parser.ParseFrom(e.Body).Id == $"item-{item}",
            TimeSpan.FromSeconds(8));
        return TradeAttionResp.Parser.ParseFrom(e.Body).Result;
    }

    private long Param(long type) => BattlePassPrivilegeConfig.All(Service<GameData>()).Single(r => r.PrivilegeType == type).Params[0];

    [ClientDataFact]
    public async Task ThePremiumPassPrivilegesApplyOnlyOnceThePassIsPremium()
    {
        var data = Service<GameData>();
        await using var bot = await InWorld("Privileged");
        Assert.Equal(0, PassPrivileges.Added(data, Session(bot), PassPrivileges.StallSlotAdd));

        await Gm(bot, "!lvl 99");
        await Service<NewActivityService>().ReconcileAsync(Session(bot));
        var session = Session(bot);
        Assert.True(session.NewActivities!.ActivityInfo[200].ActivityParam.BattlePass.Premium);
        Assert.Equal(Param(PassPrivileges.StallSlotAdd), PassPrivileges.Added(data, session, PassPrivileges.StallSlotAdd));
        Assert.Equal(Param(PassPrivileges.TradeFavAdd), PassPrivileges.Added(data, session, PassPrivileges.TradeFavAdd));

        // [currency, percent]: [1, 10] raises Zeny drops by 10%, other drops stay.
        var copper = BattlePassPrivilegeConfig.All(data).Single(r => r.PrivilegeType == PassPrivileges.CopperEffAdd).Params;
        var other = (int)copper[0] + 1;
        var boosted = PassPrivileges.BoostDrops(data, session, [((int)copper[0], 1000), (other, 1000)]);
        Assert.Equal([((int)copper[0], (int)(1000 + 1000 * copper[1] / 100)), (other, 1000)], boosted);
    }

    [ClientDataFact]
    public async Task TradeFavouritesTakeTheTradeExchangePrivilege()
    {
        await using var bot = await InWorld("Collector");
        await Gm(bot, "!lvl 99");
        await Service<NewActivityService>().ReconcileAsync(Session(bot));
        var data = Service<GameData>();
        long limit = (long)(GameData.Number(data.Table("PublicParamConfig")["TradeConcernNum"]) ?? 15)
            + Param(PassPrivileges.TradeFavAdd);
        for (int item = 1; item <= limit; item++) Assert.Equal(0, await Follow(bot, item));
        Assert.Equal(ErrorCodes.TradeQuantity, await Follow(bot, (int)limit + 1));
    }
}
