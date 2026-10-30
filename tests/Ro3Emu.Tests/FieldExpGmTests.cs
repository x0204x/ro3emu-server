using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>The "!fieldexp" GM command: Ro3Emu:FieldExpRates changed in memory, here through the loopback /ro3emu/gm the panel uses.</summary>
public sealed class FieldExpGmTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3emu-fieldexp-gm-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
            o.FieldExpRates = [new FieldExpRule { MinLevel = 1, MaxLevel = 10, Percent = 150 }];
        });
        await _app.StartAsync(); _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync(); SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task Gm(string line)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_http}/ro3emu/gm?account=fieldexp", new StringContent(line))).EnsureSuccessStatusCode();
    }

    private IReadOnlyList<FieldExpRule> Rules => _app.Services.GetRequiredService<RewardService>().FieldExpRules;

    [ClientDataFact]
    public async Task TheCommandChangesTheRulesInForceAndResetBringsBackTheConfigured()
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync("fieldexp", "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync("FieldExp")).RoleBaseInfo.Id);
        await bot.Inbox.WaitAsync(e => e.IsGame(ShopService.ShopSync), TimeSpan.FromSeconds(10));

        Assert.Equal((1, 10, 150), (Rules[0].MinLevel, Rules[0].MaxLevel, Rules[0].Percent)); // from the configuration

        await Gm("!fieldexp set 20 40 300");
        var rule = Assert.Single(Rules);
        Assert.Equal((20, 40, 300), (rule.MinLevel, rule.MaxLevel, rule.Percent));

        await Gm("!fieldexp set 40 20 300"); // refused: the usage answers and the rules stay
        Assert.Equal(300, Assert.Single(Rules).Percent);

        await Gm("!fieldexp");
        Assert.Equal(300, Assert.Single(Rules).Percent); // showing changes nothing

        await Gm("!fieldexp clear");
        Assert.Empty(Rules);

        await Gm("!fieldexp reset");
        Assert.Equal(150, Assert.Single(Rules).Percent);
    }

    // A field monster of the killer's own level (the Fabre-class NPC 5000021, level 30), the same drop rolls twice: the kill pays
    // the rule's percent of what it pays without one, the Job EXP that equals it too, and the story dungeon's monster is not scaled.
    [ClientDataFact]
    public async Task AKillPaysTheRateOfTheKillersLevelOnlyForAFieldMonster()
    {
        const int FieldMonster = 5000021, StoryMonster = 5300036;
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync("fieldexp", "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync("FieldKill")).RoleBaseInfo.Id);
        await bot.Inbox.WaitAsync(e => e.IsGame(ShopService.ShopSync), TimeSpan.FromSeconds(10));
        await Gm("!lvl 30");

        var rewards = _app.Services.GetRequiredService<RewardService>();
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "fieldexp" && s.Role != null);
        Assert.Equal(30, session.Progress!.Level);
        MonsterUnit Unit(int npc) => new()
        {
            Uid = 77, SceneEntry = session.SceneEntry, RuntimeMap = session.RuntimeMap, Template = monsters.Template(npc)!,
            Group = new SpawnGroup(-1, false, 1, 0, [], []), Home = new SpawnPoint(-1, 0, 0, 0, 0, npc), Hp = 0
        };
        Task<long> JobExp(int npc, int seed) => _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        {
            rewards.SeedRollsForTest(seed);
            return Task.FromResult(rewards.ResolveKill(session, Unit(npc), bonus: false).JobExp);
        });

        rewards.SetFieldExpRules([]);
        long field = await JobExp(FieldMonster, 1), story = await JobExp(StoryMonster, 1);
        Assert.True(field > 0, "the field monster pays EXP at its own level");

        rewards.SetFieldExpRules([new FieldExpRule { MinLevel = 20, MaxLevel = 40, Percent = 300 }]);
        Assert.Equal(field * 3, await JobExp(FieldMonster, 1));
        Assert.Equal(story, await JobExp(StoryMonster, 1));

        rewards.SetFieldExpRules([new FieldExpRule { MinLevel = 41, MaxLevel = 50, Percent = 300 }]); // the killer is not in the range
        Assert.Equal(field, await JobExp(FieldMonster, 1));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
