using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Every Base EXP brings Job EXP through PublicParamConfig.BaseToJobConversionPercent (CharacterService.JobExpFromBase).
/// The fixture's percent is invented; the real tables have their own test in <see cref="AwardTests"/>.
/// </summary>
public sealed class JobExpConversionTests : IAsyncLifetime
{
    private const int BaseExpItem = 100, JobExpItem = 101;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-jobexp-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = SyntheticData.Dir;
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
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    [Fact]
    public void JobExpIsTheParameterShareOfBaseExpRoundedDown()
    {
        var character = _app.Services.GetRequiredService<CharacterService>();
        Assert.Equal([0L, 0, 0, 1, 25], new long[] { -8, 0, 3, 4, 100 }.Select(character.JobExpFromBase));
    }

    [Fact]
    public async Task AnAwardPaysJobExpForQuestAndGrantedBaseExpOnTopOfItsOwnJobExp()
    {
        await using var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("jobexp", "pw");
        await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync("JobExp")).RoleBaseInfo.Id);
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "JobExp");
        var character = _app.Services.GetRequiredService<CharacterService>();
        var plan = new AwardPlan(0, 0, [new AwardGrant(BaseExpItem, 80), new AwardGrant(JobExpItem, 5)]);

        Assert.True(await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(
            () => character.ApplyAwardAsync(session, plan, taskBaseExp: 20)));

        var envelope = await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.GetRewardNotify)
            && GetRewardNotify.Parser.ParseFrom(e.Body).Rewards.Any(r => r.ItemEntry == JobExpItem), TimeSpan.FromSeconds(10));
        var rewards = GetRewardNotify.Parser.ParseFrom(envelope.Body).Rewards;
        Assert.Equal(100, rewards.Single(r => r.ItemEntry == BaseExpItem).Stack);
        Assert.Equal(5 + 25, rewards.Single(r => r.ItemEntry == JobExpItem).Stack);
    }
}
