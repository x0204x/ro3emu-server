using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class GuildQuestActionTests
{
    [Theory]
    [InlineData(906001, true)]
    [InlineData(907002, false)]
    public async Task GuildJoinActionUsesTheDailyActivityIdAndDeliveryIsDurableAndOnceOnly(int activity, bool supported)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "ro3emu-guild-quest-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (string source in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(fixture, Path.GetRelativePath(SyntheticData.Dir, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
            // 全部使用自造識別碼，刻意分開公會活動列鍵與動作的每日活動參數。
            File.WriteAllText(Path.Combine(fixture, "config", "GuildActivityConfig.json"), """
                {"907002":{"_iID":907002,"_iGroupID":907002,"_iDailyActivity":906001}}
                """);
            File.WriteAllText(Path.Combine(fixture, "config", "DailyActivityConfig.json"), """
                {"906001":{"_iID":906001,"_iCount":0}}
                """);
            File.WriteAllText(Path.Combine(fixture, "config", "ActionConfig.json"), """
                {"115":{"_iInterID":115,"_iFunctionName":"MarkGuildActivityJoin","_iEXECHost":1}}
                """);
            var task = new Dictionary<string, object>
            {
                [TaskService.FirstTask.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new
                {
                    _iTaskID = TaskService.FirstTask, _iTaskType = 1, _kAcceptTask = new[] { 1 },
                    _kDeliverTask = new[] { 3 }, _kProgressTarget = new[] { new[] { 101002, 1 } },
                    _kEXECAction = new[] { new[] { "115", activity.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
                    _kEXP = new[] { 1, 1 },
                },
            };
            File.WriteAllText(Path.Combine(fixture, "config", "TaskConfig.json"), JsonSerializer.Serialize(task));
            await using var host = await SyntheticHost.StartAsync(options => options.GameDataDir = fixture);
            var joined = new List<long>();
            host.Service<TaskService>().GuildActivityJoined.Add((_, id) => joined.Add(id));
            await using var alice = await host.InWorldAsync("GuildQuest");
            long role = (long)alice.Self!.Uuid;
            var request = new TaskDeliveryReq { Req = { new TaskDeliverySingleReq { Id = TaskService.FirstTask } } };
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await alice.SendGameAsync(TaskService.TaskDelivery, request);
                var envelope = await alice.Inbox.WaitAsync(e => e.IsGame(TaskService.TaskDelivery), TimeSpan.FromSeconds(10));
                var response = TaskDeliveryResp.Parser.ParseFrom(envelope.Body);
                Assert.Equal(supported && attempt == 0 ? 1 : 0, response.Data.Count);
            }
            await host.Service<GameLoop>().InvokeAsync(() =>
            {
                var session = host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == role);
                Assert.Equal(supported, TaskService.IsDelivered(session, TaskService.FirstTask));
                Assert.Equal(supported ? 1L : 0L, session.Progress!.Exp);
                Assert.Equal(supported ? new long[] { activity } : [], joined);
                return Task.CompletedTask;
            });
            var saved = DBTaskInfo.Parser.ParseFrom(host.Service<RoleStore>().LoadTasks(role)!);
            Assert.Equal(supported ? 1 : 0, saved.CompleteTaskNum.GetValueOrDefault(TaskService.FirstTask));
            Assert.Equal(supported ? TaskState.Delivery : TaskState.Finish, saved.Infos[TaskService.FirstTask].Status);
            using var db = host.Service<AccountStore>().Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM operation_source WHERE source_key LIKE $prefix";
            command.Parameters.AddWithValue("$prefix", $"task/{role}/{TaskService.FirstTask}/1/%");
            Assert.Equal(supported ? 1L : 0L, (long)command.ExecuteScalar()!);
        }
        finally
        {
            if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
        }
    }
}
