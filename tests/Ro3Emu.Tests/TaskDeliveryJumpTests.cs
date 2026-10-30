using System.Text.Json;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class TaskDeliveryJumpTests
{
    private const long Destination = 906012, Point = 906013, OtherPoint = 906014;

    [Fact]
    public async Task DeliveryJumpRequiresItsCommittedTaskAndTheExactConfiguredParameters()
    {
        string fixture = CreateFixture();
        try
        {
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = fixture);
            await using var alice = await host.InWorldAsync("TaskJump");
            var request = Jump();
            Assert.Equal(ErrorCodes.TaskRequirements, await RequestJump(alice, request));
            await alice.SendGameAsync(TaskService.TaskDelivery, new TaskDeliveryReq
            { Req = { new TaskDeliverySingleReq { Id = TaskService.FirstTask } } });
            var delivered = await alice.Inbox.WaitAsync(e => e.IsGame(TaskService.TaskDelivery), TimeSpan.FromSeconds(10));
            Assert.Single(TaskDeliveryResp.Parser.ParseFrom(delivered.Body).Data);

            foreach (var invalid in new[]
                { Jump(task: TaskService.FirstTask + 1), Jump(point: OtherPoint), Jump(extra: 7) })
                Assert.Equal(ErrorCodes.TaskRequirements, await RequestJump(alice, invalid));
            Assert.Equal(0, await RequestJump(alice, request));
            await alice.Inbox.WaitAsync(e => e.IsGame(MsgId.SwitchSceneLine)
                && SwitchSceneLineResp.Parser.ParseFrom(e.Body).SceneEntry == Destination, TimeSpan.FromSeconds(10));
            Assert.Equal(ErrorCodes.SwitchingScenes, await RequestJump(alice, request));
            await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = Destination });
            var loaded = await alice.Inbox.WaitAsync(e => e.IsGame(MsgId.SceneLoaded), TimeSpan.FromSeconds(10));
            Assert.Equal(0, SceneLoadedResp.Parser.ParseFrom(loaded.Body).Result);
            await alice.Inbox.WaitAsync(e => e.IsGame(MsgId.EntitySelf), TimeSpan.FromSeconds(10));
            await host.Service<GameLoop>().InvokeAsync(() =>
            {
                var session = host.Service<SessionRegistry>().All().Single(s => s.Role != null);
                Assert.Equal(Destination, session.SceneEntry);
                Assert.Equal(1, session.Tasks!.CompleteTaskNum[TaskService.FirstTask]);
                return Task.CompletedTask;
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(fixture, true);
        }
    }

    private static ClientCustomJumpMapReq Jump(long task = TaskService.FirstTask, long point = Point, long extra = 0) => new()
    {
        SceneId = Destination, PointId = point, CustomType = EClientCustomJumpMapType.TaskDelivery,
        TypeParam1 = task, TypeParam2 = extra,
    };

    private static async Task<long> RequestJump(BotClient bot, ClientCustomJumpMapReq request)
    {
        await bot.SendGameAsync(SceneService.CustomJumpMap, request);
        var response = await bot.Inbox.WaitAsync(e => e.IsGame(SceneService.CustomJumpMap), TimeSpan.FromSeconds(10));
        return ClientCustomJumpMapResp.Parser.ParseFrom(response.Body).Result;
    }

    private static string CreateFixture()
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-task-jump-" + Guid.NewGuid().ToString("N"));
        foreach (string source in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(root, Path.GetRelativePath(SyntheticData.Dir, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        // 全部目的地、點位與內容為自造資料；只有協定動作及既有根任務常數沿用正式定義。
        WriteDestination(root);
        File.WriteAllText(Path.Combine(root, "config", "ActionConfig.json"), """
            {"141":{"_iInterID":141,"_iFunctionName":"JumpMapArea","_iEXECHost":0}}
            """);
        var task = new Dictionary<string, object>
        {
            [TaskService.FirstTask.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new
            {
                _iTaskID = TaskService.FirstTask, _iTaskType = 1, _kAcceptTask = new[] { 1 }, _kDeliverTask = new[] { 3 },
                _kProgressTarget = new[] { new[] { 101002, 1 } },
                _kEXECAction = new[] { new[] { "141", Destination.ToString(), Point.ToString(), "2", TaskService.FirstTask.ToString(), "0" } },
            },
        };
        File.WriteAllText(Path.Combine(root, "config", "TaskConfig.json"), JsonSerializer.Serialize(task));
        return root;
    }

    private static void WriteDestination(string root)
    {
        var maps = new Dictionary<string, object>
        {
            [SyntheticData.Town.ToString()] = new { _iID = SyntheticData.Town, _kResSceneEditorName = "synthetic_town" },
            [Destination.ToString()] = new { _iID = Destination, _kResSceneEditorName = "synthetic_task_destination" },
        };
        var editor = new
        {
            m_kBornPoints = new[] { new { isDefault = true, position = new { x = 10, y = 0, z = 10 }, direction = 0 } },
            m_kAllPoints = new Dictionary<string, object>
            {
                [Point.ToString()] = new { position = new { x = 13, y = 0, z = 14 }, direction = 0 },
                [OtherPoint.ToString()] = new { position = new { x = 17, y = 0, z = 19 }, direction = 0 },
            },
        };
        File.WriteAllText(Path.Combine(root, "config", "MapConfig.json"), JsonSerializer.Serialize(maps));
        File.WriteAllText(Path.Combine(root, "sceneeditor", "synthetic_task_destination.json"), JsonSerializer.Serialize(editor));
    }
}
