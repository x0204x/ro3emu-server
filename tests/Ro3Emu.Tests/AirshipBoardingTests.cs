using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class AirshipBoardingTests : IAsyncLifetime
{
    private const long Cargo = 10103150;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-boarding-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], options =>
        {
            options.DataDir = _dataDir; options.GameDataDir = Path.Combine(RepoRoot(), "data");
            options.HttpPort = http; options.MicroGatePort = micro; options.GameGatePort = game;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{http}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task Restart()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await InitializeAsync();
    }

    private async Task<BotClient> InWorld(bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("boardingalice", "pw"); await bot.EnterGateAsync();
        long role = create ? (await bot.CreateRoleAsync("BoardingAlice")).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(role);
        return bot;
    }

    private GameSession Session => Service<SessionRegistry>().All().Single(s => s.Role?.Name == "BoardingAlice");
    private Task OnLoop(Func<Task> action) => Service<GameLoop>().InvokeAsync(action);
    private Task<T> Read<T>(Func<GameSession, T> read) =>
        Service<GameLoop>().InvokeAsync(() => Task.FromResult(read(Session)));

    private async Task Gm(string command)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_http}/ro3emu/gm?account=boardingalice", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var packet = await bot.Inbox.WaitAsync(p => p.IsGame(id) && (match == null || match(parser.ParseFrom(p.Body))), Wait);
        return parser.ParseFrom(packet.Body);
    }

    private static async Task<int> CompleteCargo(BotClient bot)
    {
        await bot.SendGameAsync(TaskService.GoodsStack, new TaskGoodsStackReq());
        return (await Next(bot, TaskService.GoodsStack, TaskGoodsStackResp.Parser)).Result;
    }

    private async Task PrepareCargo(BotClient bot, int acceptedRounds = 2)
    {
        await Gm($"!quest accept {Cargo}");
        await MoveCargoMap(bot);
        for (int round = 0; round < acceptedRounds; round++) Assert.Equal(0, await CompleteCargo(bot));
    }

    private async Task MoveCargoMap(BotClient bot)
    {
        if (await Read(session => session.SceneEntry == 10101 && session.InstanceId == 0
            && !session.SceneLoading && !session.SceneRecoveryRequired)) return;
        var cargo = Service<WorldData>().Map(10101)!.Points[1010315001];
        // LAB 只定位主線起點；使用正式公開場景切換與客戶端載入握手，不直接改場景欄位。
        await OnLoop(async () => Assert.True(await Service<SceneService>().EnterSceneAsync(Session, 10101, cargo)));
        await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, change => change.SceneEntry == 10101);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 10101 });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, reply => reply.SceneId == 10101)).Result);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        Assert.True(await Read(session => session.SceneEntry == 10101 && session.InstanceId == 0
            && session.RuntimeMap.Kind == RuntimeMapKind.PublicLine && !session.SceneLoading));
    }

    private DBTaskInfo StoredTasks(long role) => DBTaskInfo.Parser.ParseFrom(Service<RoleStore>().LoadTasks(role)!);
    private long AwardSources(long role, long task)
    {
        using var db = Service<AccountStore>().Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM operation_source s JOIN operation_receipt r ON r.operation_id=s.operation_id
            WHERE s.source_key LIKE $source AND r.command_kind='cd13e/quest-award'
            """;
        command.Parameters.AddWithValue("$source", $"task/{role}/{task}/1/%");
        return (long)command.ExecuteScalar()!;
    }

    private void AssertCargoDeliveredOnce(long role)
    {
        var stored = StoredTasks(role);
        Assert.Equal(3, stored.Infos[Cargo].CompleteNum[0]);
        Assert.Equal(TaskState.Delivery, stored.Infos[Cargo].Status);
        Assert.Equal(1, stored.CompleteTaskNum[Cargo]);
        Assert.Equal(1, AwardSources(role, Cargo));
        Assert.False(Service<RoleStore>().HasTaskFollowup(role));
    }

    [ClientDataTheory]
    [InlineData("task/airship-cargo", false)]
    [InlineData("task/airship-cargo", true)]
    [InlineData("economy/cd13e/quest-award", false)]
    [InlineData("economy/cd13e/quest-award", true)]
    public async Task CargoRoundAndFinalAwardStayAtomicAcrossFaultRetryAndRestart(string boundary, bool afterCommit)
    {
        long role;
        await using (var bot = await InWorld())
        {
            role = (long)bot.Self!.Uuid;
            bool finalRound = boundary == "economy/cd13e/quest-award";
            await PrepareCargo(bot, finalRound ? 2 : 1);
            var session = Session;
            var accounts = Service<AccountStore>();
            var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task Fault(string label)
            {
                if (label != boundary) return Task.CompletedTask;
                attempted.TrySetResult();
                throw new IOException("飛空艇貨物交易故障測試");
            }
            if (afterCommit) accounts.BeforeOwnedApplyForTest = Fault;
            else accounts.BeforeOwnedCommitForTest = Fault;
            try
            {
                await bot.SendGameAsync(TaskService.GoodsStack, new TaskGoodsStackReq());
                await attempted.Task.WaitAsync(Wait);
                if (!afterCommit)
                    Assert.Equal(ErrorCodes.Unknown, (await Next(bot, TaskService.GoodsStack, TaskGoodsStackResp.Parser)).Result);
                else
                {
                    using var timeout = new CancellationTokenSource(Wait);
                    while (session.Owner?.State != RoleOwnerState.RecoveryRequired)
                        await Task.Delay(10, timeout.Token);
                }
                long durable = (finalRound ? 2 : 1) + (afterCommit ? 1 : 0);
                Assert.Equal(durable, StoredTasks(role).Infos[Cargo].CompleteNum[0]);
                Assert.Equal(finalRound ? 2 : 1, session.Tasks!.Infos[Cargo].CompleteNum[0]);
                Assert.Equal(finalRound && afterCommit ? TaskState.Delivery : TaskState.Running,
                    StoredTasks(role).Infos[Cargo].Status);
                Assert.Equal(finalRound && afterCommit ? 1 : 0, AwardSources(role, Cargo));
                Assert.Equal(!finalRound && afterCommit, Service<RoleStore>().HasTaskFollowup(role));
            }
            finally { accounts.BeforeOwnedCommitForTest = null; accounts.BeforeOwnedApplyForTest = null; }
            if (!afterCommit)
            {
                // 同一連線提交前失敗仍維持原關次；不必重登，也不提前暴露第三關完成值。
                Assert.Equal(0, await CompleteCargo(bot));
                if (boundary == "task/airship-cargo")
                {
                    Assert.Equal(2, StoredTasks(role).Infos[Cargo].CompleteNum[0]);
                    Assert.Equal(0, AwardSources(role, Cargo));
                    Assert.Equal(0, await CompleteCargo(bot));
                }
                AssertCargoDeliveredOnce(role);
                Assert.Equal(ErrorCodes.TaskRequirements, await CompleteCargo(bot));
            }
        }
        await Restart();
        await using var recovered = await InWorld(create: false);
        if (boundary == "task/airship-cargo" && afterCommit)
        {
            Assert.Equal(2, StoredTasks(role).Infos[Cargo].CompleteNum[0]);
            Assert.Equal(TaskState.Running, StoredTasks(role).Infos[Cargo].Status);
            Assert.Equal(0, AwardSources(role, Cargo));
            await MoveCargoMap(recovered);
            Assert.Equal(0, await CompleteCargo(recovered));
        }
        AssertCargoDeliveredOnce(role);
        Assert.Equal(ErrorCodes.TaskRequirements, await CompleteCargo(recovered));
        AssertCargoDeliveredOnce(role);
    }

    [ClientDataFact]
    public async Task CargoKeepsEachAcceptedRoundAcrossReconnectAndRejectsTheFourth()
    {
        long role;
        await using (var bot = await InWorld())
        {
            role = (long)bot.Self!.Uuid;
            Assert.Equal(ErrorCodes.TaskRequirements, await CompleteCargo(bot));
            await Gm($"!quest accept {Cargo}");
            Assert.Equal(ErrorCodes.TaskRequirements, await CompleteCargo(bot));
            await MoveCargoMap(bot);
            Assert.Equal(0, await CompleteCargo(bot));
            Assert.Equal(1, StoredTasks(role).Infos[Cargo].CompleteNum[0]);
            Assert.Equal(0, AwardSources(role, Cargo));
        }
        await Restart();
        await using var recovered = await InWorld(create: false);
        Assert.Equal(1, await Read(s => s.Tasks!.Infos[Cargo].CompleteNum[0]));
        await MoveCargoMap(recovered);
        Assert.Equal(0, await CompleteCargo(recovered));
        Assert.Equal(2, StoredTasks(role).Infos[Cargo].CompleteNum[0]);
        Assert.Equal(0, await CompleteCargo(recovered));
        AssertCargoDeliveredOnce(role);
        Assert.Equal(ErrorCodes.TaskRequirements, await CompleteCargo(recovered));
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Ro3Emu.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
