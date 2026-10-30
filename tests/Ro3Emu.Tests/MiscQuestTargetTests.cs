using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The remaining quest targets and the quests the server offers outside the pre-task graph (TaskService.MiscTargets.cs).
/// Event targets are driven at the hook their owning service raises after its commit; state targets by changing the
/// state the reader looks at.
/// </summary>
public sealed class MiscQuestTargetTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-miscquests-" + Guid.NewGuid().ToString("N"));
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

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait)).Body);

    private Task Gm(string command) => QuestProbe.GmAsync(_http, "alice", command);
    private Task Accept(long task) => QuestProbe.AcceptAsync(_app, _http, "alice", task);
    private Task<TaskInfo> At(long task, long progress) => QuestProbe.AtAsync(_app, "alice", task, progress);
    private Task<TaskInfo> Done(long task) => QuestProbe.DoneAsync(_app, "alice", task);

    /// <summary>Every target in the map has a reader now, except the ones whose source does not exist.</summary>
    [ClientDataFact]
    public async Task TheRemainingTargetsHaveReaders()
    {
        await using var bot = await InWorld();
        foreach (long task in new long[] { 100004, 100005, 100006, 100016, 100018, 100019, 306060, 100023, 302091, 382141,
            201001, 201002, 201003, 994000, 102035, 500220, 1101000 })
            Assert.True(Tasks.TargetsSupported(task), $"task {task}");
        // 103012 MINI, 103017 offline rewards, 103018 guild prayer, 101008 tower sweep: no source in this client.
        // 103048: the client builds quest 1400010 itself.
        foreach (long task in new long[] { 100015, 100020, 100021, 500021, 1400010 })
            Assert.False(Tasks.TargetsSupported(task), $"task {task}");
    }

    /// <summary>103002 / 103003 / 103013 / 103015 / 103020 count their service's committed events.</summary>
    [ClientDataFact]
    public async Task EventTargetsCountTheirService()
    {
        await using var bot = await InWorld();
        long role = await Read(() => Session.Role!.Id);
        foreach (long task in new long[] { 100005, 100006, 100016, 100018, 100023 }) await Accept(task);
        await Run(s => { Tasks.OnExchangeBought(s); Tasks.OnGuildAuctionBid(s); Tasks.OnBonfireJoined(s); });
        await Read(() => { Tasks.OnGuildLeagueFought(1, new Dictionary<long, long> { [role] = 0 }); return 0; });
        await Run(s => Tasks.OnChatSent(s, 3)); // 100023 [103020,2,3] asks for the guild channel (2)
        await Run(s => Tasks.OnChatSent(s, 2));
        foreach (long task in new long[] { 100005, 100006, 100016, 100018, 100023 }) await At(task, 1);
        await Run(s => { Tasks.OnExchangeBought(s); Tasks.OnGuildAuctionBid(s); Tasks.OnBonfireJoined(s); });
        await Read(() => { Tasks.OnGuildLeagueFought(2, new Dictionary<long, long> { [role] = 0 }); return 0; });
        await Run(s => { Tasks.OnChatSent(s, 2); Tasks.OnChatSent(s, 2); });
        foreach (long task in new long[] { 100005, 100006, 100016, 100018 }) Assert.Equal(2, (await Done(task)).CompleteNum[0]);
        Assert.Equal(3, (await Done(100023)).CompleteNum[0]);
    }

    /// <summary>103001 title rank, 101021 ridden mount, 103016 Bonus Time minutes, 318001-318003 Monster Hunt claims.</summary>
    [ClientDataFact]
    public async Task StateTargetsReadTheirOwner()
    {
        await using var bot = await InWorld();
        await Accept(100004); // [103001,2] Wind's Departure I
        await At(100004, 0);
        await Gm("!title set 3");
        await Run(Tasks.OnStateChanged);
        Assert.Equal(2, (await Done(100004)).CompleteNum[0]);

        await Accept(302091); // [101021,2,60030020] "Ride the Lion mount"
        await At(302091, 0);
        await Run(s => { s.MountState.QuestMount = 60030020; Tasks.OnStateChanged(s); });
        await Done(302091);

        await Accept(306060); // [103016,5] "Use 5 minutes of boosted auto-battle time"
        await Run(s => { s.WildAfk!.Info.SumHighMultiplierTime = 299; Tasks.OnStateChanged(s); });
        await At(306060, 4);
        await Run(s => { s.WildAfk!.Info.SumHighMultiplierTime = 300; Tasks.OnStateChanged(s); });
        await Done(306060);

        foreach (long task in new long[] { 201001, 201002, 201003 }) await Accept(task);
        await Run(s =>
        {
            s.AreaConcur ??= new DBAreaConcurInfo();
            s.AreaConcur.MapInfo[10100101] = new AreaConcurMapInfo { IsClaimedAward = true };
            s.AreaConcur.AreaInfo[101001] = new AreaConcurAreaInfo { ClaimedAward = { [1] = true } };
            Tasks.OnStateChanged(s);
        });
        await At(201001, 1); // [318001,2]
        await Done(201002); // [318002,1]
        await At(201003, 0); // [318003,10100102], another map
        await Run(s => { s.AreaConcur!.MapInfo[10100102] = new AreaConcurMapInfo { IsClaimedAward = true }; Tasks.OnStateChanged(s); });
        await Done(201001);
        await Done(201003);
    }

    /// <summary>103052: the accepted quest offers GuideMainQuestBranchChoice in waitSelectBranch; 31215 meets it.</summary>
    [ClientDataFact]
    public async Task BranchQuestOffersTheChoiceAndEndsOnIt()
    {
        await using var bot = await InWorld();
        await Accept(102035);
        Assert.Equal(new Dictionary<long, long> { [1] = 1, [2] = 2 },
            await Read(() => ((DBTaskInfo)Session.Synced[TaskService.TaskInfoSync]).WaitSelectBranch.ToDictionary()));
        await At(102035, 0);
        await bot.SendGameAsync(TaskService.TaskSelectQuestBranch, new SelectTaskQuestBranchReq { BranchId = 2 });
        Assert.Equal(2, (await Next(bot, TaskService.TaskSelectQuestBranch, SelectTaskQuestBranchResp.Parser)).Req.BranchId);
        await Done(102035);
        Assert.True(await Read(() => Session.Tasks!.SelectBranch[2] && Session.Tasks.WaitSelectBranch.Count == 0));
    }

    /// <summary>309001 waits from acceptance; 101022 adds the delivered sub-tasks' points; 106907 a paid Rift clear.</summary>
    [ClientDataFact]
    public async Task WaitFactionPointsAndVaultTargets()
    {
        await using var bot = await InWorld();
        await Accept(994000); // [309001,5]
        Assert.True((await QuestProbe.ReadAsync(_app, "alice", 994000))!.CompleteNum[0] < 5);
        await Run(s => { s.Tasks!.Infos[994000].StartTime -= 10_000; Tasks.OnStateChanged(s); });
        Assert.Equal(5, (await Done(994000)).CompleteNum[0]);

        await Accept(1101000); // [101022,1000] "Faction Quest"
        await Gm("!quest done 1102013"); // "Kill Monster A +10 Points"
        await At(1101000, 10);
        await Gm("!quest done 1102023"); // "+20 Points"
        await At(1101000, 30);

        await Accept(500220); // [106907,1] "Secret Trove Activity Quest"
        await Read(() => { Tasks.StageOnePieceReward(Session, "test/vault")!.Apply(); return 0; });
        Assert.Equal(1, (await QuestProbe.ReadAsync(_app, "alice", 500220))!.CompleteNum[0]);
    }

    /// <summary>Type 21: a finished World Exploration node offers its quest. Type 12: the season's settlement quest.</summary>
    [ClientDataFact]
    public async Task ExplorationNodeAndSeasonSettlementQuestsAreOffered()
    {
        await using var bot = await InWorld();
        var seasons = Service<SeasonService>();
        Assert.Equal((1, 101), (seasons.SeasonId, seasons.PlayBook));
        // MultiverseSettlementTask row 1 (playbook 101): 11200060 "Reach Lv. 60 by Season Settlement".
        await QuestProbe.WaitAsync(_app, "alice", 11200060, i => i.Status == TaskState.Running, "offered");

        Assert.Null(await QuestProbe.ReadAsync(_app, "alice", 301000));
        await Run(s =>
        {
            var info = s.NewActivities!.ActivityInfo.Values.FirstOrDefault(a => a.ActivityParam?.WorldExploration != null);
            if (info == null) s.NewActivities.ActivityInfo[111] = info = new PlayerActivityInfo
            { ActivityParam = new PlayerActivityParam { WorldExploration = new DBWorldExploration() } };
            var exploration = info.ActivityParam.WorldExploration;
            if (!exploration.PhaseInfos.TryGetValue(101, out var phase)) exploration.PhaseInfos[101] = phase = new() { PhaseCfgId = 101 };
            phase.NodeInfos[1011] = new DBWorldExplorationPhaseNode { NodeCfgId = 1011, IsFinished = true };
            Tasks.OnStateChanged(s);
        });
        await QuestProbe.WaitAsync(_app, "alice", 301000, i => i.Status is TaskState.Running or TaskState.Finish, "offered");
        Assert.Null(await QuestProbe.ReadAsync(_app, "alice", 302000)); // node 1012 is not finished
    }
}
