using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>2005 ITEM_EXCHANGE against the client's ItemExchangeConfig (the shop's Currency Redemption).</summary>
public sealed class CurrencyRedemptionTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3emu-redeem-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        });
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
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", Operator.Command(command))).EnsureSuccessStatusCode();
    }

    private static async Task<long> Redeem(BotClient bot, long paid, long count)
    {
        await bot.SendGameAsync(ShopService.ItemExchange, new ItemExchangeReq { ItemId = paid, Count = count });
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(ShopService.ItemExchange), TimeSpan.FromSeconds(8));
        return ItemExchangeResp.Parser.ParseFrom(e.Body).Result;
    }

    [ClientDataFact]
    public async Task EveryRowPaysItsItemForRateTimesTheCount()
    {
        await using var bot = await Enter("Changer");
        await Gm(bot, "!lvl 30");
        var data = Service<GameData>();
        var character = Service<CharacterService>();
        var session = Session(bot);
        foreach (var row in ItemExchangeConfig.All(data))
        {
            await Gm(bot, $"!item {row.Id} 5");
            long paid = character.CountOf(session, (int)row.Id), target = character.CountOf(session, (int)row.TargetId);
            Assert.Equal(0, await Redeem(bot, row.Id, 3));
            Assert.Equal(paid - 3, character.CountOf(session, (int)row.Id));
            Assert.Equal(target + 3 * row.Rate, character.CountOf(session, (int)row.TargetId));
        }
    }

    [ClientDataFact]
    public async Task RefusesUnknownItemsShortageAndTheSingleLimit()
    {
        await using var bot = await Enter("Miser");
        await Gm(bot, "!lvl 30");
        var row = ItemExchangeConfig.All(Service<GameData>()).First();
        var character = Service<CharacterService>();
        long target = character.CountOf(Session(bot), (int)row.TargetId);
        Assert.Equal(ErrorCodes.Parameter, await Redeem(bot, 999_999_999, 1));
        Assert.Equal(ErrorCodes.Parameter, await Redeem(bot, row.Id, 0));
        Assert.Equal(209, await Redeem(bot, row.Id, row.SingleLimit + 1));
        long owned = character.CountOf(Session(bot), (int)row.Id);
        Assert.NotEqual(0, await Redeem(bot, row.Id, owned + 1));
        Assert.Equal(target, character.CountOf(Session(bot), (int)row.TargetId));
    }

    [ClientDataFact]
    public async Task TheRowFunctionGatesTheExchange()
    {
        await using var bot = await Enter("Novice");
        var row = ItemExchangeConfig.All(Service<GameData>()).OrderByDescending(r => r.Function).First();
        await Gm(bot, $"!item {row.Id} 5");
        Assert.Equal(ErrorCodes.FunctionLocked, await Redeem(bot, row.Id, 1));
    }
}
