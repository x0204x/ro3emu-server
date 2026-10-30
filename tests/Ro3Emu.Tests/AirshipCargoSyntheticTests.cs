using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

public sealed class AirshipCargoSyntheticTests
{
    private const long Cargo = 10103150;
    private const int Group = 906731, Award = 906732, Goods = 1000000;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalCargoAwardIsAtomicBeforeCommitRetryAndAfterCommitRestart(bool afterCommit)
    {
        string fixture = CreateFixture();
        try
        {
            await using var host = await SyntheticHost.StartAsync(options => options.GameDataDir = fixture);
            long role, openingExperience, openingGoods;
            await using (var bot = await host.InWorldAsync("SyntheticCargo"))
            {
                role = (long)bot.Self!.Uuid;
                var session = Session(host, role);
                Assert.Equal(TaskState.Running, session.Tasks!.Infos[Cargo].Status);
                openingExperience = TotalBaseExperience(host, role);
                openingGoods = GoodsCount(host, role);
                Assert.Equal(0, await Complete(bot));
                Assert.Equal(1, Saved(host, role).Infos[Cargo].CompleteNum[0]);
                Assert.Equal(0, Sources(host, role));
                var accounts = host.Service<AccountStore>();
                var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task Fault(string label)
                {
                    if (label != "economy/cd13e/quest-award") return Task.CompletedTask;
                    attempted.TrySetResult();
                    throw new IOException("自造貨物最後一關交易故障");
                }
                if (afterCommit) accounts.BeforeOwnedApplyForTest = Fault;
                else accounts.BeforeOwnedCommitForTest = Fault;
                try
                {
                    await bot.SendGameAsync(TaskService.GoodsStack, new TaskGoodsStackReq());
                    await attempted.Task.WaitAsync(Wait);
                    if (afterCommit)
                    {
                        using var timeout = new CancellationTokenSource(Wait);
                        while (session.Owner?.State != RoleOwnerState.RecoveryRequired)
                            await Task.Delay(10, timeout.Token);
                    }
                    else Assert.Equal(ErrorCodes.Unknown, await Response(bot));
                    Assert.Equal(1, session.Tasks.Infos[Cargo].CompleteNum[0]);
                    Assert.Equal(afterCommit ? 2 : 1, Saved(host, role).Infos[Cargo].CompleteNum[0]);
                    Assert.Equal(afterCommit ? TaskState.Delivery : TaskState.Running, Saved(host, role).Infos[Cargo].Status);
                    Assert.Equal(afterCommit ? 1 : 0, Sources(host, role));
                    Assert.Equal(openingExperience + (afterCommit ? 7 : 0), TotalBaseExperience(host, role));
                    Assert.Equal(openingGoods + (afterCommit ? 5 : 0), GoodsCount(host, role));
                }
                finally { accounts.BeforeOwnedCommitForTest = null; accounts.BeforeOwnedApplyForTest = null; }
                if (!afterCommit)
                {
                    // 最後一關提交失敗時同連線重試，完成值與經驗／物品獎勵一起入帳。
                    Assert.Equal(0, await Complete(bot));
                    AssertDelivered(host, role, openingExperience, openingGoods);
                    Assert.Equal(ErrorCodes.TaskRequirements, await Complete(bot));
                }
            }
            await host.RestartAsync();
            await using var recovered = await host.InWorldAsync("SyntheticCargo", create: false);
            AssertDelivered(host, role, openingExperience, openingGoods);
            Assert.Equal(ErrorCodes.TaskRequirements, await Complete(recovered));
            AssertDelivered(host, role, openingExperience, openingGoods);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(fixture, true);
        }
    }

    private static GameSession Session(SyntheticHost host, long role) =>
        host.Service<SessionRegistry>().All().Single(session => session.Role?.Id == role);

    private static DBTaskInfo Saved(SyntheticHost host, long role) =>
        DBTaskInfo.Parser.ParseFrom(host.Service<RoleStore>().LoadTasks(role)!);

    private static long GoodsCount(SyntheticHost host, long role) =>
        host.Service<ItemStore>().Load(role).Where(item => item.Entry == Goods).Sum(item => (long)item.Stack);

    private static long TotalBaseExperience(SyntheticHost host, long role)
    {
        var progress = host.Service<RoleStore>().LoadProgress(role);
        var data = host.Service<GameData>();
        long consumed = Enumerable.Range(1, Math.Max(0, progress.Level - 1))
            .Sum(level => data.Row("BaseLvConfig", level) is { } row ? GameData.Int(row, "_iBaseExp") : 0);
        // 自造曲線第 1 級需 6 EXP；發 7 後是第 2 級剩 1，入帳總額仍須恰為 7。
        return checked(consumed + progress.Exp);
    }

    private static async Task<int> Complete(BotClient bot)
    {
        await bot.SendGameAsync(TaskService.GoodsStack, new TaskGoodsStackReq());
        return await Response(bot);
    }

    private static async Task<int> Response(BotClient bot) => TaskGoodsStackResp.Parser.ParseFrom(
        (await bot.Inbox.WaitAsync(packet => packet.IsGame(TaskService.GoodsStack), Wait)).Body).Result;

    private static long Sources(SyntheticHost host, long role)
    {
        using var db = host.Service<AccountStore>().Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM operation_source s JOIN operation_receipt r ON r.operation_id=s.operation_id
            WHERE s.source_key LIKE $source AND r.command_kind='cd13e/quest-award'
            """;
        command.Parameters.AddWithValue("$source", $"task/{role}/{Cargo}/1/%");
        return (long)command.ExecuteScalar()!;
    }

    private static void AssertDelivered(SyntheticHost host, long role, long experience, long goods)
    {
        var saved = Saved(host, role);
        Assert.Equal(2, saved.Infos[Cargo].CompleteNum[0]);
        Assert.Equal(TaskState.Delivery, saved.Infos[Cargo].Status);
        Assert.Equal(1, saved.CompleteTaskNum[Cargo]);
        Assert.Equal(1, Sources(host, role));
        Assert.Equal(experience + 7, TotalBaseExperience(host, role));
        Assert.Equal(goods + 5, GoodsCount(host, role));
        Assert.False(host.Service<RoleStore>().HasTaskFollowup(role));
    }

    private static string CreateFixture()
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-cargo-fixture-" + Guid.NewGuid().ToString("N"));
        foreach (string source in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(root, Path.GetRelativePath(SyntheticData.Dir, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        // 根任務及貨物入口沿用服務端識別碼，其餘關次、獎勵、地圖追蹤均為自造資料。
        var tasks = new Dictionary<string, object>
        {
            [TaskService.FirstTask.ToString(CultureInfo.InvariantCulture)] = new
            {
                _iTaskID = TaskService.FirstTask, _iTaskType = 1, _kAcceptTask = new[] { 1 }, _kDeliverTask = new[] { 1 },
                _kProgressTarget = new[] { new[] { 101002, 1 } },
            },
            [Cargo.ToString(CultureInfo.InvariantCulture)] = new
            {
                _iTaskID = Cargo, _iTaskType = 1, _kPreTask = new[] { TaskService.FirstTask },
                _kAcceptTask = new[] { 1 }, _kDeliverTask = new[] { 1 }, _kProgressTarget = new[] { new[] { 103053, 2 } },
                _iAward = Group, _kEXP = new[] { 1, 7 },
                _kTrack = new[] { new[] { "142", SyntheticData.Town.ToString(CultureInfo.InvariantCulture), "0" } },
            },
        };
        File.WriteAllText(Path.Combine(root, "config", "TaskConfig.json"), JsonSerializer.Serialize(tasks));
        Append(root, "MultiverseGroupConfig", Group, new { _iID = Group, _itype = 0, _iValue_default = Award });
        Append(root, "AwardConfig", Award, new[] { new { _iID = Award, _kAward = new[] { new[] { Goods, 5, 0 } } } });
        return root;
    }

    private static void Append(string root, string table, int key, object row)
    {
        string path = Path.Combine(root, "config", table + ".json");
        var content = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        content.Add(key.ToString(CultureInfo.InvariantCulture), JsonSerializer.SerializeToNode(row));
        File.WriteAllText(path, content.ToJsonString());
    }
}
