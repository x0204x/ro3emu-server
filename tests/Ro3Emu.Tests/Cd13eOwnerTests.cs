using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.LoadTest;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>13e: retained owner, detached state, source and actual economic result at a suspended commit.</summary>
public sealed class Cd13eOwnerTests : IAsyncLifetime
{
    private const int Token = 1250709;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-cd13e-" + Guid.NewGuid().ToString("N"));
    private static readonly Lazy<EconomyCatalog> Catalog = new(() => EconomyCatalog.Load(Path.Combine(RepoRoot(), "data")));
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        Service<SeasonService>().SetPlayBook(101);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        Service<AccountStore>().BeforeOwnedCommitForTest = null;
        Service<OwnerRegistry>().DeferredForTest = null;
        await _app.StopAsync(); await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task<BotClient> Enter(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }
    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    private async Task PrepareMilestone(GameSession s)
    {
        await OnLoop(async () =>
        {
            await Service<GmCommands>().RunAsync(s, "!lvl 99");
            await Service<GmCommands>().RunAsync(s, "!quest done 10105020");
            Service<RoleStore>().RecordTowerFirstPass(10, new ClimbTowerFirstPassData { Layer = 10, HasFirstPass = true }.ToByteArray());
        });
    }
    private long Count(long role) => Service<ItemStore>().Load(role).Where(i => i.Entry == Token).Sum(i => (long)i.Stack);
    private long Scalar(string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_directory, "ro3emu.db"), Mode = SqliteOpenMode.ReadOnly }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
    private long Claims() => Scalar("SELECT COUNT(*) FROM operation_receipt WHERE command_kind='cd13e/tower-milestone'");
    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), TimeSpan.FromSeconds(10))).Body);
    private Task Barrier(string label, TaskCompletionSource staged, TaskCompletionSource release) => OnLoop(() =>
    {
        Service<AccountStore>().BeforeOwnedCommitForTest = name =>
        {
            if (name != label) return Task.CompletedTask;
            staged.TrySetResult(); return release.Task;
        };
        return Task.CompletedTask;
    });

    [Fact]
    public async Task DungeonPreferencesBeforeRoleSelectionAreRefusedUnanswered()
    {
        await using var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync("alice", "pw"); await bot.EnterGateAsync();
        await StageGate.AssertUnansweredAsync(bot, DungeonService.SetJobPosition, new MultiDungeonSetJobPositionReq { JobPosition = 101 });
        await StageGate.AssertUnansweredAsync(bot, DungeonService.SetGuide, new MultiDungeonSetJobPositionGuideFlagReq { GuideFlag = true });
    }

    [ClientDataFact]
    public async Task MilestoneWaitsForCommitAndSerializesTimerAndGmWhileAnotherRoleProgresses()
    {
        await using var alice = await Enter("Alice");
        await using var bob = await Enter("Bob");
        var s = Session(alice); var other = Session(bob); long role = s.Role!.Id;
        await PrepareMilestone(s);
        var opening = S3EconomyReader.Read(_directory, Catalog.Value);
        var staged = Signal(); var release = Signal(); var gmDeferred = Signal();
        await Barrier("economy/cd13e/tower-milestone", staged, release);
        await OnLoop(() =>
        {
            Service<OwnerRegistry>().DeferredForTest = origin =>
            {
                if (origin == "GM") gmDeferred.TrySetResult();
            };
            return Task.CompletedTask;
        });
        Task? gm = null, timer = null;
        await alice.SendGameAsync(TowerService.ClaimFirstPass, new ClimbTowerClaimFirstPassRewardReq());
        try
        {
            await staged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, s.Tower!.ServerFirstPassClaimedLayer); Assert.Equal(0, Count(role)); Assert.Equal(0, Claims());
            Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(TowerService.ClaimFirstPass));
            gm = OnLoop(() => Service<GmCommands>().RunAsync(s, "!speed 701"));
            timer = OnLoop(() => Service<DungeonService>().SyncAsync(s));
            await gmDeferred.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await OnLoop(() => Service<DungeonService>().SetGuideAsync(other,
                new MultiDungeonSetJobPositionGuideFlagReq { GuideFlag = true })).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(other.Dungeons!.FirstSetted);
            Assert.False(gm.IsCompleted); Assert.False(timer.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(0, (await Next(alice, TowerService.ClaimFirstPass, ClimbTowerClaimFirstPassRewardRes.Parser)).Result);
        await Task.WhenAll(gm!, timer!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(10, Count(role)); Assert.Equal(10, s.Tower!.ServerFirstPassClaimedLayer); Assert.Equal(1, Claims());
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM operation_source WHERE source_key='tower-milestone/{role}/0/10'"));
        var closing = S3EconomyReader.Read(_directory, Catalog.Value);
        var ledger = S3EconomyReader.ReadLedger(_directory, Catalog.Value, opening, closing)!;
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.DoesNotContain(check.Issues, x => x.Kind == EconomyIssueKind.Corruption);
        Assert.All(check.Assets, x => { Assert.Equal("0", x.Residual); Assert.Equal("0", x.LegResidual); });
        Assert.All(check.Locations, x => Assert.Equal("0", x.Residual));
        await alice.SendGameAsync(TowerService.ClaimFirstPass, new ClimbTowerClaimFirstPassRewardReq());
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, TowerService.ClaimFirstPass, ClimbTowerClaimFirstPassRewardRes.Parser)).Result);
        Assert.Equal(10, Count(role)); Assert.Equal(1, Claims());
    }

    [ClientDataFact]
    public async Task MilestoneLedgerFailureRollsBackGoodsAndSourceAndRetryGrantsOnce()
    {
        await using var bot = await Enter("Alice"); var s = Session(bot); long role = s.Role!.Id;
        await PrepareMilestone(s);
        byte[] before = Service<RoleStore>().LoadTower(role)!;
        FixtureSql.Execute(Service<AccountStore>(), "CREATE TRIGGER fail_milestone BEFORE INSERT ON economy_entry WHEN NEW.source_key LIKE 'tower-milestone/%' BEGIN SELECT RAISE(ABORT,'milestone rollback'); END");
        try
        {
            await bot.SendGameAsync(TowerService.ClaimFirstPass, new ClimbTowerClaimFirstPassRewardReq());
            Assert.Equal(ErrorCodes.Unknown, (await Next(bot, TowerService.ClaimFirstPass, ClimbTowerClaimFirstPassRewardRes.Parser)).Result);
            Assert.Equal(before, Service<RoleStore>().LoadTower(role));
            Assert.Equal(0, s.Tower!.ServerFirstPassClaimedLayer); Assert.Equal(0, Count(role)); Assert.Equal(0, Claims());
            Assert.Equal(0, Scalar("SELECT COUNT(*) FROM operation_source WHERE source_key LIKE 'tower-milestone/%'"));
        }
        finally { FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_milestone"); }
        await bot.SendGameAsync(TowerService.ClaimFirstPass, new ClimbTowerClaimFirstPassRewardReq());
        Assert.Equal(0, (await Next(bot, TowerService.ClaimFirstPass, ClimbTowerClaimFirstPassRewardRes.Parser)).Result);
        Assert.Equal(10, Count(role)); Assert.Equal(1, Claims());
    }

    [ClientDataFact]
    public async Task AdmittedMilestoneFinishesBeforeRoleHandoverReleasesItsOwner()
    {
        await using var bot = await Enter("Alice"); var s = Session(bot); long role = s.Role!.Id;
        await PrepareMilestone(s);
        var staged = Signal(); var release = Signal();
        await Barrier("economy/cd13e/tower-milestone", staged, release);
        var claim = OnLoop(() => Service<TowerService>().ClaimFirstPassAsync(s));
        Task? handover = null;
        try
        {
            await staged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            handover = OnLoop(() => Service<RoleHandover>().Begin(s));
            await OnLoop(() =>
            {
                Assert.True(s.Attachment!.Cancelled);
                Assert.Equal(role, s.Role!.Id);
                Assert.Equal(0, s.Tower!.ServerFirstPassClaimedLayer);
                return Task.CompletedTask;
            });
            Assert.False(handover.IsCompleted); Assert.Equal(0, Claims());
        }
        finally { release.TrySetResult(); }
        await claim.WaitAsync(TimeSpan.FromSeconds(10));
        await handover!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(10, Count(role)); Assert.Equal(1, Claims());
        Assert.Equal(10, DBClimTower.Parser.ParseFrom(Service<RoleStore>().LoadTower(role)!).ServerFirstPassClaimedLayer);
    }

    [Fact]
    public async Task FailedTowerFormationWriteRetainsThePreviousLiveAndDurableFormation()
    {
        await using var bot = await Enter("Alice"); var s = Session(bot); long role = s.Role!.Id;
        await PrepareMilestone(s);
        await OnLoop(() => Service<OwnerRegistry>().RunSessionAsync("tower formation fixture",
            [ReservationKey.Role(role)], s, () =>
        {
            // Legacy saved roster: removing a no-longer-owned pet remains a valid formation request.
            s.Tower!.PetIds[0] = 10001;
            Service<RoleStore>().SaveTower(role, s.Tower.ToByteArray());
            return Task.CompletedTask;
        }));
        byte[] before = Service<RoleStore>().LoadTower(role)!;
        FixtureSql.Execute(Service<AccountStore>(), "CREATE TRIGGER fail_tower_formation BEFORE UPDATE OF tower ON role BEGIN SELECT RAISE(ABORT,'formation rollback'); END");
        try
        {
            var request = new SetClimTowerPetsReq(); request.Pets.Add([0, 0, 0, 0]);
            await Assert.ThrowsAsync<SqliteException>(() => OnLoop(() => Service<TowerService>().SetPetsAsync(s, request)));
            Assert.Equal(before, s.Tower!.ToByteArray());
            Assert.Equal(before, Service<RoleStore>().LoadTower(role));
        }
        finally { FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_tower_formation"); }
    }

    [Fact]
    public async Task DungeonGuideRemainsDetachedUntilCommitAndFailureDoesNotPublishIt()
    {
        await using var bot = await Enter("Alice"); var s = Session(bot);
        var before = s.Dungeons!;
        var staged = Signal(); var release = Signal();
        await Barrier("dungeon/state", staged, release);
        var command = OnLoop(() => Service<DungeonService>().SetGuideAsync(s,
            new MultiDungeonSetJobPositionGuideFlagReq { GuideFlag = true }));
        try
        {
            await staged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(before, s.Dungeons); Assert.False(s.Dungeons!.FirstSetted);
            Assert.False(JsonSerializer.Deserialize<DungeonService.State>(Service<RoleStore>().LoadDungeons(s.Role!.Id)!)!.FirstSetted);
        }
        finally { release.TrySetResult(); }
        await command.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(s.Dungeons!.FirstSetted);
        FixtureSql.Execute(Service<AccountStore>(), "CREATE TRIGGER fail_dungeon_guide BEFORE UPDATE OF dungeons ON role BEGIN SELECT RAISE(ABORT,'guide rollback'); END");
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => OnLoop(() => Service<DungeonService>().SetGuideAsync(s,
                new MultiDungeonSetJobPositionGuideFlagReq { GuideFlag = false })));
            Assert.True(s.Dungeons.FirstSetted);
            Assert.True(JsonSerializer.Deserialize<DungeonService.State>(Service<RoleStore>().LoadDungeons(s.Role!.Id)!)!.FirstSetted);
        }
        finally { FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_dungeon_guide"); }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
