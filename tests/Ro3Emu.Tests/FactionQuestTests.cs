using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The Faction Quest (TaskService.FactionQuest.cs): 1101000 and the camp tier's type 25 sub-tasks are offered with
/// function 7202, each sub-task is one action that adds its points and is offered again, the day's count stops at
/// TerritoryWarzoneTaskPointsLimit and a new game day starts over.
/// </summary>
public sealed class FactionQuestTests : IAsyncLifetime
{
    private const long Quest = 1101000, Claim = 1101001;
    private const long CrystalOne = 1102001, CrystalThree = 1102003, OrcOne = 1102011, RivalOne = 1102021, RivalThree = 1102023;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-faction-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task<T> Read<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));
    private Task<int> Run(Action<GameSession> act) => Read(() => { act(Session); return 0; });
    private GameSession Session => Service<SessionRegistry>().All().Single(s => s.Account == "alice" && s.Role != null);
    private TaskService Tasks => Service<TaskService>();

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
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

    private async Task<BotClient> InWorld()
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("alice", "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id);
        return bot;
    }

    private Task Gm(string command) => QuestProbe.GmAsync(_http, "alice", command);
    private Task<TaskInfo> At(long task, long progress) => QuestProbe.AtAsync(_app, "alice", task, progress);
    private Task<TaskInfo> Offered(long task) =>
        QuestProbe.WaitAsync(_app, "alice", task, i => i.Status == TaskState.Running, "offered");
    private Task<bool> Has(long task) => Read(() => Session.Tasks!.Infos.ContainsKey(task));

    private async Task Gone(long task)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (await Has(task))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"quest {task} is still offered");
            await Task.Delay(25);
        }
    }

    /// <summary>Function 7202 offers the quest and the Peace tier; a crystal, a kill and a rival kill each pay once.</summary>
    [ClientDataFact]
    public async Task SubTasksAreOneActionAndOfferedAgain()
    {
        await using var bot = await InWorld();
        Assert.False(await Has(Quest)); // function 7202 is closed below its level
        await Run(s => { s.GmAllFunctionsOpen = true; Tasks.OnStateChanged(s); });
        await At(Quest, 0);
        foreach (long id in new[] { CrystalOne, OrcOne, RivalOne }) await Offered(id);
        Assert.False(await Has(CrystalThree));

        await Run(Tasks.OnFactionCrystalCollected); // a Crystalline Ore cluster
        await At(Quest, 1);
        await Offered(CrystalOne);
        Assert.Equal(1, await Read(() => Session.Tasks!.CompleteTaskNum[CrystalOne]));

        await Run(s => Tasks.FactionRivals = (_, _) => true);
        await Read(() => Tasks.OnFactionPlayerKilledAsync(Session, Session).ContinueWith(_ => 0));
        await At(Quest, 11); // "Defeat players from other guilds: +10 points"
        await Run(s => Tasks.FactionRivals = (_, _) => false);
        await Read(() => Tasks.OnFactionPlayerKilledAsync(Session, Session).ContinueWith(_ => 0));
        await Gm("!quest done " + OrcOne); // "Kill Monster A +5 Points"
        await At(Quest, 16);
    }

    /// <summary>The Guild camp offers the third tier; the day's count stops at the cap and withdraws the sub-tasks.</summary>
    [ClientDataFact]
    public async Task CampTierAndDailyCap()
    {
        await using var bot = await InWorld();
        await Run(s => { s.GmAllFunctionsOpen = true; Tasks.OnStateChanged(s); });
        await Offered(CrystalOne);
        await Run(s => { s.TerritoryWars!.Camp = (int)EPvpRoleCampNum.Guild; Tasks.OnFactionCampChanged(s); });
        await Offered(CrystalThree);
        await Gone(CrystalOne);

        await Run(s => s.TaskRuntime.FactionPoints = 2990);
        await Gm("!quest done " + RivalThree); // +20, of which 10 fit under the 3000 cap
        await At(Quest, 10);
        Assert.Equal(3000, await Read(() => Session.TaskRuntime.FactionPoints));
        await Gone(CrystalThree);
    }

    /// <summary>A delivered 1101000 pays the member's guild the caravan departure's 1000 Guild Funds (inferred).</summary>
    [ClientDataFact]
    public async Task CompletionPaysTheGuildItsShare()
    {
        await using var bot = await InWorld();
        await Gm("!lvl 99");
        await bot.SendGameAsync(GuildService.Create, new GuildCreateReq
        { Name = "Faction", Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 } });
        var full = await bot.Inbox.WaitAsync(e => e.IsGame(GuildService.Full)
            && GuildFullInfoNtf.Parser.ParseFrom(e.Body).Guid > 0, TimeSpan.FromSeconds(20));
        long guild = GuildFullInfoNtf.Parser.ParseFrom(full.Body).Guid;
        var store = Service<Server.Accounts.GuildStore>();
        long before = await Read(() => store.Get(guild)!.Money);
        Assert.Equal(1000, await Read(() => Service<GuildCaravanService>().FactionGuildFunds(Session)));

        await Run(s => { s.GmAllFunctionsOpen = true; Tasks.OnStateChanged(s); });
        await At(Quest, 0);
        await Gm("!quest finish " + Quest);
        await Offered(Claim);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (await Read(() => store.Get(guild)!.Money) != before + 1000)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("guild funds not paid");
            await Task.Delay(25);
        }
    }

    /// <summary>1000 points deliver the quest and offer the claim; a new game day starts the quest over.</summary>
    [ClientDataFact]
    public async Task CompletionClaimAndNewDay()
    {
        await using var bot = await InWorld();
        await Run(s => { s.GmAllFunctionsOpen = true; Tasks.OnStateChanged(s); });
        await At(Quest, 0);
        await Gm("!quest finish " + Quest);
        await Offered(Claim); // [104001,72,30307,10000106], claimed from the UI
        Assert.True(await Read(() => TaskService.IsDelivered(Session, Quest)));

        await Run(s => { s.TaskRuntime.FactionDay -= 86_400; s.TaskRuntime.FactionPoints = 1200; Tasks.OnStateChanged(s); });
        await At(Quest, 0);
        Assert.False(await Has(Claim));
        Assert.Equal(0, await Read(() => Session.TaskRuntime.FactionPoints));
    }
}
