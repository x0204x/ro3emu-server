using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Levelling from Lv 20 to 40 (docs/systems/leveling.md): the Talent Remnants the story pays before Growth Manual
/// chapter 1 and the To-Do boxes' level EXP after main task 10105090.
/// </summary>
public sealed class LevelingTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private const int Remnant = 1250900, MintedCoin = 2, Liveness = 5;
    private const long ChapterOneGate = 10104100, TalentChild = 701020, ToDoOpens = 10105090;

    private sealed class Host : IAsyncDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-leveling-" + Guid.NewGuid().ToString("N"));
        private WebApplication _app = null!;
        public string Http { get; private set; } = "";
        public BotClient Bot { get; private set; } = null!;

        public T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
        public GameSession Session => Service<SessionRegistry>().All().Single(s => s.Role != null);
        public Task<T> Read<T>(Func<GameSession, T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read(Session)));

        public static async Task<Host> StartAsync(Action<ServerOptions>? configure = null)
        {
            var host = new Host();
            var (http, micro, game) = TestPorts.FreePorts();
            host._app = ServerHost.Create([], o =>
            {
                o.DataDir = host._dir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
                o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game;
                configure?.Invoke(o);
            });
            await host._app.StartAsync();
            host.Http = $"http://127.0.0.1:{http}";
            host.Bot = new BotClient(host.Http, File.ReadAllText(Path.Combine(host._dir, "gate_rsa_public.pem")));
            await host.Bot.LoginAsync("leveler", "pw");
            await host.Bot.EnterGateAsync();
            await host.Bot.EnterWorldAsync((await host.Bot.CreateRoleAsync("Leveler")).RoleBaseInfo.Id);
            return host;
        }

        public Task Gm(string line) => QuestProbe.GmAsync(Http, "leveler", line);

        public async Task<T> Request<T>(uint id, IMessage body, MessageParser<T> parser) where T : IMessage<T>
        {
            using var reply = Bot.Inbox.Expect(e => e.IsGame(id));
            await Bot.SendGameAsync(id, body);
            return parser.ParseFrom((await reply.WaitAsync(Wait)).Body);
        }

        /// <summary>Base EXP earned since level 1 (BaseLvConfig._iBaseExp of every level passed, plus the current EXP).</summary>
        public Task<long> TotalExp() => Read(s => s.Progress!.Exp + Enumerable.Range(1, s.Progress.Level - 1)
            .Sum(level => GameData.Int(Service<GameData>().Row("BaseLvConfig", level)!.Value, "_iBaseExp")));

        public async ValueTask DisposeAsync()
        {
            await Bot.DisposeAsync();
            await _app.StopAsync();
            await _app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }

    private static async Task Until(Func<Task<bool>> done, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!await done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// Growth Manual chapter 1 (701990) asks for 40 talent levels (701020, [326002, 40]). Tree 10101 holds exactly 40 (8 nodes
    /// to 3 and 4 to 4, 10 Talent Remnants each, SeasonTalentEffectConfig groups 110101-110104). The bot plays the main story
    /// to 10104100 and claims the other chapter children that pay Remnants (701030, 701040, 701050) through their real
    /// awards; then it learns talents with those Remnants only (Minted Coins, the farmable half of the cost, are supplied).
    /// </summary>
    [ClientDataFact]
    public async Task GrowthChapterOneTalentsArePaidByTheStoryBeforeIt()
    {
        await using var host = await Host.StartAsync();
        var data = host.Service<GameData>();
        foreach (long task in MainChain(data).TakeWhile(id => id != ChapterOneGate))
        {
            await host.Gm($"!quest done {task}");
            await Until(() => host.Read(s => TaskService.IsDelivered(s, task)), $"main task {task} delivered with its award");
        }
        await Until(() => host.Read(s => s.Tasks!.Infos.GetValueOrDefault(ChapterOneGate)?.Status == TaskState.Running), "10104100 running");
        foreach (long child in new long[] { 701030, 701040, 701050 })
        {
            await host.Gm($"!quest done {child}");
            await Until(() => host.Read(s => TaskService.IsDelivered(s, child)), $"chapter child {child} claimed");
        }
        long remnants = await host.Read(s => host.Service<CharacterService>().CountOf(s, Remnant));
        Assert.Equal(400, remnants); // 250 from 10103160/10104010/10104050/10104080, 150 from 701030/701040/701050

        // What the skipped story targets leave out: its first job change (tree 10101's condition 80000000 asks for job
        // rank 2 and Lv 20, which 701060 asks for too) and the Minted Coins, the farmable half of each level's cost.
        await host.Gm("!job 1100");
        await host.Gm("!lvl 20");
        await host.Gm($"!item {MintedCoin} 100000");
        await Until(() => host.Read(s => s.SeasonTalent!.UnlockedNode.GetValueOrDefault(10101001)), "tree 10101 open");
        int learned = 0;
        while (await host.Read(s => s.Tasks!.Infos[TalentChild].Status) == TaskState.Running && learned < 60)
        {
            var node = await host.Read(s => s.SeasonTalent!.UnlockedNode.Where(kv => kv.Value).Select(kv => kv.Key)
                .Where(id => GameData.Int(data.Row("SeasonTalentConfig", id)!.Value, "_iTalentTreeID") == 10101
                    && (s.SeasonTalent.Plan[s.SeasonTalent.CurPlanId].TalentNodes.GetValueOrDefault(id)?.CurrentLevel ?? 0)
                        < GameData.Int(data.Row("SeasonTalentConfig", id)!.Value, "_iMaxLevel"))
                .Order().FirstOrDefault());
            Assert.True(node != 0, $"no tree 10101 node left after {learned} levels");
            var reply = await host.Request(SeasonTalentService.Upgrade, new SeasonTalentUpgradeReq { TalentId = (int)node },
                SeasonTalentUpgradeResp.Parser);
            Assert.True(reply.Result == 0, $"talent {node} refused {reply.Result} after {learned} levels, "
                + $"{await host.Read(s => host.Service<CharacterService>().CountOf(s, Remnant))} Remnants left");
            learned++;
            await Task.Delay(30); // the quest refresh after an upgrade is queued
        }
        await Until(() => host.Read(s => s.Tasks!.Infos[TalentChild].Status == TaskState.Finish), "701020 finished");
        Assert.Equal(40, learned);
        Assert.Equal(0, await host.Read(s => host.Service<CharacterService>().CountOf(s, Remnant)));
    }

    /// <summary>The story's main chain from 10101010: each task's 101xxxxx child (StoryWalkTests.MainChain).</summary>
    private static List<long> MainChain(GameData data)
    {
        var children = new Dictionary<long, List<long>>();
        foreach (var row in data.Table("TaskConfig").Values)
            if (row.TryGetProperty("_kPreTask", out var pre) && pre.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var p in pre.EnumerateArray())
                    (children.TryGetValue(p.GetInt64(), out var list) ? list : children[p.GetInt64()] = []).Add(GameData.Int(row, "_iTaskID"));
        var chain = new List<long>();
        for (long id = TaskService.FirstTask; id != 0;)
        {
            chain.Add(id);
            id = children.GetValueOrDefault(id)?.Where(c => c / 100000 == 101).Order().FirstOrDefault() ?? 0;
        }
        return chain;
    }

    /// <summary>
    /// After main task 10105090 opens To-Do (function 25) and Dodo's ring (5902), a Lv 34 player without a guild has two
    /// liveness rows (107 vigor, 20; the ring's terminal, 20) and a guild caravan one (101, 20): its tasks are handed out
    /// (_iTaskReceiveAction 31) and pay their item 5. Each To-Do box 7001-7003 then pays the Base EXP of its level row
    /// (GenericStageRewardsConfig._iAward → AwardConfig._iDropPool 80001-80003 → DropGroupConfig 3501-3503, item 100).
    /// </summary>
    [ClientDataFact]
    public async Task ToDoBoxesPayTheLevelExpOfTheirDropGroups()
    {
        await using var host = await Host.StartAsync();
        var data = host.Service<GameData>();
        await host.Gm("!lvl 34");
        await host.Gm($"!quest done {ToDoOpens}");
        foreach (long task in new long[] { 500070, 500080 }) // DailyActivityConfig 107 and 101
        {
            await Until(() => host.Read(s => s.Tasks!.Infos.GetValueOrDefault(task)?.Status == TaskState.Running), $"{task} handed out");
            long before = await host.Read(s => s.Progress!.Currency.GetValueOrDefault(Liveness));
            await host.Gm($"!quest done {task}");
            await Until(async () => await host.Read(s => s.Progress!.Currency.GetValueOrDefault(Liveness)) == before + 20, $"{task} pays 20 liveness");
        }
        var ring = await host.Request(DailyActivityService.RingAccept,
            new CommonDailyActivityReq { ActivityId = 3302, RunRingTaskAccept = new RunRingTaskAcceptReq() }, CommonDailyActivityRes.Parser);
        Assert.Equal(0, ring.Result);
        int root = await host.Read(s => s.Daily!.ActiveRingStart);
        for (int task = root; task < root + 10; task++)
        {
            await host.Gm($"!quest done {task}");
            int done = task;
            await Until(() => host.Read(s => TaskService.IsDelivered(s, done)), $"ring task {done}");
        }
        Assert.Equal(60, await host.Read(s => s.Progress!.Currency.GetValueOrDefault(Liveness)));

        for (int box = 7001; box <= 7003; box++)
        {
            int level = await host.Read(s => s.Progress!.Level);
            long expected = GameData.Int(data.Row("DropGroupConfig", 3500 + box - 7000)!.Value.EnumerateArray()
                .Single(r => GameData.Int(r, "_kDropItem") == 100 && GameData.Int(r, "_iMinLevelLimit") == level), "_iMinNumber");
            long before = await host.TotalExp();
            var gain = await host.Request(DailyActivityService.StageGain, new StageRewardGainReq { StageId = box }, StageRewardGainResp.Parser);
            Assert.Equal(0, gain.Result);
            await Until(async () => await host.TotalExp() > before, $"box {box} EXP");
            Assert.Equal(expected, await host.TotalExp() - before);
        }
        var fourth = await host.Request(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 7004 }, StageRewardGainResp.Parser);
        Assert.Equal(ErrorCodes.CurrencyShort, fourth.Result); // 80 liveness: the Lv 37 rows 105 and 106
    }
}
