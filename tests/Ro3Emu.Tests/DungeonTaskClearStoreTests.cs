using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;
using Romsg;

namespace Ro3Emu.Tests;

public sealed class DungeonTaskClearStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-dungeon-task-clear-" + Guid.NewGuid().ToString("N"));
    private AccountStore Store() => new(Options.Create(new ServerOptions { DataDir = _dir, GameDataDir = SyntheticData.Dir }));

    [Fact]
    public async Task FreezeAndTaskSnapshotRollbackTogetherAndReadHandleCannotAcknowledge()
    {
        using var accounts = Store();
        var roles = new RoleStore(accounts);
        var role = roles.Create("outbox", 1, "Outbox", 1, 1000, 1)!;
        var rewards = new DungeonObligationStore(accounts);
        var row = Reward(role.Id, "synthetic-outbox/1", 700001);
        var clear = new DungeonTaskClear(row.Key, role.Id, row.RoomId, row.SceneEntry, []);
        FixtureSql.Execute(accounts, """
            CREATE TRIGGER refuse_task_snapshot BEFORE INSERT ON dungeon_task_clear_outbox
            BEGIN SELECT RAISE(ABORT, 'synthetic snapshot fault'); END
            """);
        await Assert.ThrowsAsync<SqliteException>(() => rewards.FreezeWithTaskClearsAsync([row], [clear]));
        Assert.Empty(rewards.Pending(role.Id));
        Assert.Empty(rewards.PendingTaskClears(role.Id, earnedOnly: false));
        FixtureSql.Execute(accounts, "DROP TRIGGER refuse_task_snapshot");
        await rewards.FreezeWithTaskClearsAsync([row], [clear]);
        await rewards.FreezeWithTaskClearsAsync([row], [clear]);
        Assert.Single(rewards.PendingTaskClears(role.Id, earnedOnly: false));
        Assert.Empty(rewards.PendingTaskClears(role.Id)); // 尚未交付的獎勵不能先增加任務目標。
        using var read = accounts.Open();
        using var command = read.CreateCommand();
        command.CommandText = "UPDATE dungeon_task_clear_outbox SET applied=1";
        Assert.Equal(23, Assert.Throws<SqliteException>(() => command.ExecuteNonQuery()).SqliteErrorCode);
    }

    [Fact]
    public async Task LegacyRewardCannotAcquireALaterTaskSnapshotAndConflictingSnapshotIsRefused()
    {
        using var accounts = Store();
        var roles = new RoleStore(accounts);
        var role = roles.Create("legacy", 1, "Legacy", 1, 1000, 1)!;
        var rewards = new DungeonObligationStore(accounts);
        var legacy = Reward(role.Id, "synthetic-outbox/legacy", 700002);
        await rewards.FreezeAsync([legacy]);
        var legacyClear = new DungeonTaskClear(legacy.Key, role.Id, legacy.RoomId, legacy.SceneEntry, []);
        await Assert.ThrowsAsync<InvalidDataException>(() => rewards.FreezeWithTaskClearsAsync([legacy], [legacyClear]));
        Assert.Empty(rewards.PendingTaskClears(role.Id, earnedOnly: false));

        var current = Reward(role.Id, "synthetic-outbox/current", 700003);
        var clear = new DungeonTaskClear(current.Key, role.Id, current.RoomId, current.SceneEntry,
            [new(700100, 1, 0, 0, [101018, current.SceneEntry, 2], 2)]);
        await rewards.FreezeWithTaskClearsAsync([current], [clear]);
        await Assert.ThrowsAsync<InvalidDataException>(() => rewards.FreezeWithTaskClearsAsync([current],
            [clear with { Targets = [] }]));
        Assert.Single(rewards.PendingTaskClears(role.Id, earnedOnly: false));
    }

    [Fact]
    public async Task OpeningAnOlderRealmAddsAnEmptyOutboxWithoutChangingFrozenRewardOrReceipt()
    {
        long role;
        byte[] beforeState, beforeIntent;
        using (var accounts = Store())
        {
            role = new RoleStore(accounts).Create("migration", 1, "Migration", 1, 1000, 1)!.Id;
            await new DungeonObligationStore(accounts).FreezeAsync([Reward(role, "synthetic-outbox/migration", 700004)]);
            beforeState = ReadBytes(accounts, "SELECT state FROM dungeon_reward_obligation");
            beforeIntent = ReadBytes(accounts, "SELECT intent_payload FROM operation_receipt WHERE command_kind='dungeon-obligation-earn'");
            // 模擬升級前的資料庫，保留舊獎勵與收據，僅移除新版本才有的空旁表。
            FixtureSql.Execute(accounts, "DROP TABLE dungeon_task_clear_outbox");
        }
        using var reopened = Store();
        Assert.Equal(beforeState, ReadBytes(reopened, "SELECT state FROM dungeon_reward_obligation"));
        Assert.Equal(beforeIntent, ReadBytes(reopened, "SELECT intent_payload FROM operation_receipt WHERE command_kind='dungeon-obligation-earn'"));
        Assert.Single(new DungeonObligationStore(reopened).Pending(role));
        Assert.Empty(new DungeonObligationStore(reopened).PendingTaskClears(role, earnedOnly: false));
    }

    private static byte[] ReadBytes(AccountStore accounts, string sql)
    {
        using var read = accounts.Open();
        using var command = read.CreateCommand();
        command.CommandText = sql;
        return (byte[])command.ExecuteScalar()!;
    }

    [Fact]
    public async Task ARepeatedDeliveryCountsOneOfTwoRequiredClearsAndOnlyANewSourceCountsTheSecond()
    {
        string data = Path.Combine(_dir, "GameData");
        foreach (string source in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(data, Path.GetRelativePath(SyntheticData.Dir, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        // 全部數字為自造測試資料；手動交付保留二次通關目標供直接檢查。
        File.WriteAllText(Path.Combine(data, "config", "TaskConfig.json"), """
            {"10101010":{"_iID":10101010,"_iTaskID":10101010,"_iTaskType":1,"_kAcceptTask":[1],"_kDeliverTask":[2],
            "_kProgressTarget":[[101018,700000,2]]}}
            """);
        File.WriteAllText(Path.Combine(data, "config", "DungeonConfig.json"), """
            {"700000":{"_iID":700000}}
            """);
        await using var host = await SyntheticHost.StartAsync(options => options.GameDataDir = data);
        long role;
        await using (var alice = await host.InWorldAsync("Counter"))
        {
            role = (long)alice.Self!.Uuid;
            await DeliverNewSource("synthetic-outbox/count1", 700005);
            await host.Service<GameLoop>().InvokeAsync(async () =>
            {
                var session = Session();
                await host.Service<DungeonService>().DeliverPendingAsync(session);
                await host.Service<DungeonService>().DeliverPendingAsync(session);
                Assert.Equal(1, session.Tasks!.Infos[TaskService.FirstTask].CompleteNum[0]);
            });
        }
        await host.RestartAsync();
        await using var again = await host.InWorldAsync("Counter", create: false);
        Assert.Equal(1, Session().Tasks!.Infos[TaskService.FirstTask].CompleteNum[0]);
        await DeliverNewSource("synthetic-outbox/count2", 700006);
        Assert.Equal(2, Session().Tasks!.Infos[TaskService.FirstTask].CompleteNum[0]);

        GameSession Session() => host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == role);
        Task DeliverNewSource(string source, long room) => host.Service<GameLoop>().InvokeAsync(async () =>
        {
            var session = Session();
            var reward = Reward(role, source, room);
            using (await TaskService.LockTasksAsync(session))
            {
                var clear = host.Service<TaskService>().CaptureDungeonClear(session, reward);
                Assert.Single(clear.Targets);
                await host.Service<DungeonObligationStore>().FreezeWithTaskClearsAsync([reward], [clear]);
            }
            await host.Service<DungeonService>().DeliverPendingAsync(session);
        });
    }

    private static DungeonRewardObligation Reward(long role, string source, long room) => new()
    {
        Key = source, RoleId = role, RoomId = room, SceneEntry = 700000, Success = true,
        DurationMs = 1, NewRecord = false, BaseState = "{}"u8.ToArray(), NextState = "{}"u8.ToArray(),
        Goods = [], GuildId = 0, AuctionSourceType = 0, PlayBook = 1, AuctionTiers = [],
        RequiresKill = false, KillSourceKey = ""
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
