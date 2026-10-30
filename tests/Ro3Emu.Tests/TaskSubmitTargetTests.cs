using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

public sealed class TaskSubmitTargetTests
{
    private const int MaterialA = 906853, MaterialB = 906854, Goods = 906855, Group = 906851, Award = 906852;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void SubmitTupleUsesItsCountAndAcceptsOnlyKnownTrailingParameters(int parameter, bool valid)
    {
        Assert.Equal(valid, TaskSubmitTarget.TryRead([TaskSubmitTarget.Condition, MaterialA, 2, parameter], out var target));
        if (!valid) return;
        Assert.Equal(MaterialA, target.Item);
        Assert.Equal(2, target.Count);
        Assert.Equal(parameter, target.TrailingParameter);
    }

    [Fact]
    public void MalformedSubmitTuplesCannotBecomeZeroThresholdsOrTruncateCosts()
    {
        foreach (long[] values in new long[][]
        {
            [], [TaskSubmitTarget.Condition], [TaskSubmitTarget.Condition, MaterialA, 2],
            [TaskSubmitTarget.Condition, 0, 2, 0], [TaskSubmitTarget.Condition, MaterialA, 0, 0],
            [TaskSubmitTarget.Condition, MaterialA, -1, 0], [TaskSubmitTarget.Condition, MaterialA, (long)int.MaxValue + 1, 0],
            [TaskSubmitTarget.Condition, (long)int.MaxValue + 1, 2, 0], [TaskSubmitTarget.Condition, MaterialA, 2, 0, 1],
        }) Assert.False(TaskSubmitTarget.TryRead(values, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task TwoMaterialsAreRecheckedAndDuplicateDeliverySpendsAndAwardsOnlyOnce(int parameter)
    {
        string fixture = CreateFixture(parameter);
        try
        {
            await using var host = await SyntheticHost.StartAsync(options => options.GameDataDir = fixture);
            long role;
            await using (var bot = await host.InWorldAsync("SubmitMaterials"))
            {
                role = (long)bot.Self!.Uuid;
                await Grant(host, role, [(MaterialA, 2, 0)]);
                Assert.Empty((await Request(bot)).Data);
                Assert.Equal(new long[] { 2, 0 }, Saved(host, role).Infos[TaskService.FirstTask].CompleteNum);
                AssertState(host, role, 2, 0, 0, delivered: false);
                await Grant(host, role, [(MaterialB, 3, 0)]);
                Assert.Equal(TaskState.Finish, Saved(host, role).Infos[TaskService.FirstTask].Status);
                var response = await Request(bot, duplicate: true);
                Assert.Single(response.Data);
                AssertState(host, role, 0, 0, 4, delivered: true);
                Assert.Empty((await Request(bot)).Data);
                AssertState(host, role, 0, 0, 4, delivered: true);
            }
            await host.RestartAsync();
            await using var relog = await host.InWorldAsync("SubmitMaterials", create: false);
            Assert.Empty((await Request(relog)).Data);
            AssertState(host, role, 0, 0, 4, delivered: true);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(fixture, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubmitCostsAndAwardShareTheCommitAndRecoverWithoutDuplicatePayment(bool afterCommit)
    {
        string fixture = CreateFixture(0);
        try
        {
            await using var host = await SyntheticHost.StartAsync(options => options.GameDataDir = fixture);
            long role;
            await using (var bot = await host.InWorldAsync("SubmitFault"))
            {
                role = (long)bot.Self!.Uuid;
                await Grant(host, role, [(MaterialA, 2, 0), (MaterialB, 3, 0)]);
                var accounts = host.Service<AccountStore>();
                bool reached = false;
                Task Fault(string label)
                {
                    if (label != "economy/cd13e/quest-award") return Task.CompletedTask;
                    reached = true;
                    throw new IOException("自造雙材料任務的交易故障");
                }
                if (afterCommit) accounts.BeforeOwnedApplyForTest = Fault;
                else accounts.BeforeOwnedCommitForTest = Fault;
                try
                {
                    // 直接等待同一公開 handler 的故障結果，避免把斷線／回應逾時誤當故障已觸發。
                    var error = await Record.ExceptionAsync(() => host.Service<GameLoop>().InvokeAsync(() =>
                        host.Service<TaskService>().DeliverAsync(Session(host, role), Delivery())));
                    Assert.NotNull(error);
                    Assert.True(reached);
                    AssertState(host, role, afterCommit ? 0 : 2, afterCommit ? 0 : 3, afterCommit ? 4 : 0, afterCommit);
                    Assert.Equal(TaskState.Finish, Session(host, role).Tasks!.Infos[TaskService.FirstTask].Status);
                    if (afterCommit) Assert.Equal(RoleOwnerState.RecoveryRequired, Session(host, role).Owner!.State);
                }
                finally { accounts.BeforeOwnedApplyForTest = null; accounts.BeforeOwnedCommitForTest = null; }
                if (!afterCommit)
                {
                    Assert.Single((await Request(bot)).Data);
                    AssertState(host, role, 0, 0, 4, delivered: true);
                }
            }
            await host.RestartAsync();
            await using var relog = await host.InWorldAsync("SubmitFault", create: false);
            AssertState(host, role, 0, 0, 4, delivered: true);
            Assert.Empty((await Request(relog)).Data);
            AssertState(host, role, 0, 0, 4, delivered: true);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(fixture, true); }
    }

    private static GameSession Session(SyntheticHost host, long role) =>
        host.Service<SessionRegistry>().All().Single(session => session.Role?.Id == role);

    private static DBTaskInfo Saved(SyntheticHost host, long role) =>
        DBTaskInfo.Parser.ParseFrom(host.Service<RoleStore>().LoadTasks(role) ?? throw new InvalidOperationException("沒有任務存檔"));

    private static async Task Grant(SyntheticHost host, long role, (int Entry, int Count, int Bind)[] goods)
    {
        await host.Service<GameLoop>().InvokeAsync(() => host.Service<CharacterService>().GrantBoundAsync(Session(host, role), goods));
        await host.Service<GameLoop>().InvokeAsync(() => host.Service<TaskService>().RefreshAsync(Session(host, role)));
    }

    private static TaskDeliveryReq Delivery(bool duplicate = false)
    {
        var request = new TaskDeliveryReq { Req = { new TaskDeliverySingleReq { Id = TaskService.FirstTask } } };
        if (duplicate) request.Req.Add(request.Req[0].Clone());
        return request;
    }

    private static async Task<TaskDeliveryResp> Request(BotClient bot, bool duplicate = false)
    {
        await bot.SendGameAsync(TaskService.TaskDelivery, Delivery(duplicate));
        var packet = await bot.Inbox.WaitAsync(envelope => envelope.IsGame(TaskService.TaskDelivery), Wait);
        return TaskDeliveryResp.Parser.ParseFrom(packet.Body);
    }

    private static void AssertState(SyntheticHost host, long role, int a, int b, int goods, bool delivered)
    {
        var items = host.Service<ItemStore>().Load(role);
        Assert.Equal(a, items.Where(item => item.Entry == MaterialA).Sum(item => item.Stack));
        Assert.Equal(b, items.Where(item => item.Entry == MaterialB).Sum(item => item.Stack));
        Assert.Equal(goods, items.Where(item => item.Entry == Goods).Sum(item => item.Stack));
        var tasks = Saved(host, role);
        Assert.Equal(delivered ? 1 : 0, tasks.CompleteTaskNum.GetValueOrDefault(TaskService.FirstTask));
        Assert.Equal(delivered ? 1 : 0, QuestProbe.AwardSourceCount(host.App, role, TaskService.FirstTask));
    }

    private static string CreateFixture(int parameter)
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-submit-fixture-" + Guid.NewGuid().ToString("N"));
        foreach (string source in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(root, Path.GetRelativePath(SyntheticData.Dir, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? throw new InvalidOperationException("沒有測試目錄"));
            File.Copy(source, target);
        }
        // 全部材料數量、獎勵與任務內容為自造值，沒有複製201120或任何客戶端資料列。
        var tasks = new Dictionary<string, object>
        {
            [TaskService.FirstTask.ToString(CultureInfo.InvariantCulture)] = new
            {
                _iTaskID = TaskService.FirstTask, _iTaskType = 1, _kAcceptTask = new[] { 1 }, _kDeliverTask = new[] { 3 },
                _kProgressTarget = new[] { new[] { TaskSubmitTarget.Condition, MaterialA, 2, parameter }, new[] { TaskSubmitTarget.Condition, MaterialB, 3, parameter } },
                _iAward = Group,
            },
        };
        File.WriteAllText(Path.Combine(root, "config", "TaskConfig.json"), JsonSerializer.Serialize(tasks));
        foreach (int entry in new[] { MaterialA, MaterialB, Goods })
            Append(root, "ItemConfig", entry, new { _iID = entry, _iType = 2, _iBagTab = 1, _iStackLimit = 50, _iTrade = 0 });
        Append(root, "MultiverseGroupConfig", Group, new { _iID = Group, _itype = 0, _iValue_default = Award });
        Append(root, "AwardConfig", Award, new[] { new { _iID = Award, _kAward = new[] { new[] { Goods, 4, 0 } } } });
        return root;
    }

    private static void Append(string root, string table, int key, object row)
    {
        string path = Path.Combine(root, "config", table + ".json");
        var content = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException("沒有測試表");
        content.Add(key.ToString(CultureInfo.InvariantCulture), JsonSerializer.SerializeToNode(row));
        File.WriteAllText(path, content.ToJsonString());
    }
}
