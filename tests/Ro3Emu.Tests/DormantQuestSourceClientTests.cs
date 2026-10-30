using System.Collections;
using System.Reflection;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Genesis 保留但沒有開放的事件來源：OnHook Function19 的等級9999門檻／缺場景2100004，以及塔每日獎0。
/// 只驗證真正43408／32804入口的拒絕與持久化，不以GM、假擊殺或直接呼叫任務事件冒充可完成的玩法。
/// </summary>
public sealed class DormantQuestSourceClientTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-dormant-events-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task<T> Read<T>(Func<T> work) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(work()));
    private GameSession Session => Service<SessionRegistry>().All().Single(s => s.Account == "alice" && s.Role != null);

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{http}";
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
        await bot.LoginAsync("alice", "pw");
        await bot.EnterGateAsync();
        long role = create ? (await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(role);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint message, MessageParser<T> parser) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(message), Wait)).Body);

    [ClientDataFact]
    public async Task ConfiguredDormantSourcesAreDistinctFromOrdinaryMvpAndTowerClears()
    {
        await using var bot = await InWorld(create: true);
        var data = Service<GameData>();
        var function = FunctionConfig.Get(data, 19);
        Assert.NotNull(function);
        Assert.Equal(2000016, function.Value.OpenCondition);
        var condition = data.Row("LC_ConditionList", function.Value.OpenCondition)!.Value;
        var required = condition.GetProperty("m_kSubConditionList")[0].GetProperty("m_kData");
        Assert.Equal(1002, required[0].GetInt32());
        Assert.Equal(9999, required[1].GetInt32());
        Assert.False(await Read(() => Service<FunctionService>().IsOpen(Session, 19)));
        AssertDormantAfkRows(data);
        Assert.All(data.Table("PetFloorConfig").Values, floor => Assert.Equal(0, GameData.Int(floor, "_iDailyReward")));
        Assert.False(Service<TaskService>().TargetsSupported(500021));
    }

    private static void AssertDormantAfkRows(GameData data)
    {
        var rows = data.Table("OnHookDungeonConfig").Values.ToArray();
        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            long scene = GameData.Int(row, "_iDungeon");
            Assert.Equal(2100004, scene);
            Assert.Null(data.Row("DungeonConfig", scene));
            Assert.Null(data.Row("MapConfig", scene));
        });
        foreach (long id in new long[] { 1901180, 1901190 })
        {
            var task = TaskConfig.Get(data, id);
            Assert.NotNull(task);
            Assert.Equal(19, task.Value.TaskType);
            Assert.Equal(new long[] { 106012, 1, 9300201 }, Assert.Single(task.Value.ProgressTarget));
            Assert.Equal(0, task.Value.Award);
            Assert.Empty(task.Value.Exp);
        }
        // 同號NPC及Buff確實共存；OnHookDungeon的tuple不是足以猜測出生／結算時序的證據。
        Assert.NotNull(data.Row("NPCConfig", 9300201));
        Assert.NotNull(data.Row("BuffConfig", 9300201));
        Assert.DoesNotContain(data.Table("MVPConfig").Values, row => GameData.Int(row, "_iNPCID") == 9300201);
    }

    [ClientDataFact]
    public async Task RefusedAfkAndSweepRequestsDoNotMintRoomsRewardsOrQuestProgressAcrossRelog()
    {
        Observation before;
        await using (var bot = await InWorld(create: true))
        {
            before = await Snapshot();
            foreach (int config in new[] { 0, 1, 50, int.MaxValue })
            {
                await bot.SendGameAsync(WildAfkService.DungeonStart, new AfkDungeonStartReq
                { DungeonId = config, IsInDungeon = config == 50 });
                Assert.Equal(ErrorCodes.FeatureUnavailable,
                    (await Next(bot, WildAfkService.DungeonStart, AfkDungeonStartRsp.Parser)).Result);
                await AssertUnchanged(before);
            }
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await bot.SendGameAsync(TowerService.Sweep, new ClimTowerSweepReq());
                var response = await Next(bot, TowerService.Sweep, ClimTowerSweepRes.Parser);
                Assert.Equal(ErrorCodes.FeatureUnavailable, response.Result);
                Assert.Null(response.RewardNotify);
                await AssertUnchanged(before);
            }
        }
        await using var again = await InWorld(create: false);
        await AssertUnchanged(before);
    }

    private sealed record Observation(long Scene, long Instance, int Rooms, long Exp, long JobExp,
        string Items, string Currency, string Tasks, string SavedTasks, string Tower);

    private Task<Observation> Snapshot() => Read(() =>
    {
        var session = Session;
        var tasks = session.Tasks ?? throw new InvalidOperationException("Role task data was not loaded.");
        var role = session.Role ?? throw new InvalidOperationException("Role was detached before observation.");
        var progress = session.Progress ?? throw new InvalidOperationException("Role progress was not loaded.");
        var saved = Service<RoleStore>().LoadTasks(role.Id);
        return new Observation(session.SceneEntry, session.InstanceId, PrivateRooms(), progress.Exp,
            progress.JobExp, JsonSerializer.Serialize(session.Items.OrderBy(i => i.Guid).Select(i => new { i.Guid, i.Entry, i.Stack })),
            JsonSerializer.Serialize(progress.Currency.OrderBy(p => p.Key)), Goals(tasks),
            Goals(saved == null ? new DBTaskInfo() : DBTaskInfo.Parser.ParseFrom(saved)),
            Convert.ToBase64String(Service<RoleStore>().LoadTower(role.Id) ?? []));
    });

    private int PrivateRooms()
    {
        // 唯讀測試觀察值；不為了新增probe去擴充InstanceService正式API。
        var field = typeof(InstanceService).GetField("_rooms", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(Service<InstanceService>()) is not IDictionary rooms)
            throw new InvalidOperationException("The private-room observation changed; review this probe.");
        return rooms.Count;
    }

    private static string Goals(DBTaskInfo tasks) => JsonSerializer.Serialize(
        new long[] { 1901180, 1901190, 500021 }.Select(id => new
        {
            Id = id,
            Status = tasks.Infos.GetValueOrDefault(id)?.Status,
            Counts = tasks.Infos.GetValueOrDefault(id)?.CompleteNum.ToArray(),
            Delivered = tasks.CompleteTaskNum.GetValueOrDefault(id)
        }));

    private async Task AssertUnchanged(Observation before)
    {
        Assert.Equal(before, await Snapshot());
        await Read(() =>
        {
            Assert.Null(Session.TowerAttempt);
            Assert.Null(Session.DungeonAttempt);
            Assert.False(Session.Tower!.IsCanSweep);
            Assert.DoesNotContain(Service<World>().AllUnits(), unit => unit.InstanceId != 0
                && unit is MonsterUnit monster && monster.Template.NpcId == 9300201);
            return 0;
        });
    }
}
