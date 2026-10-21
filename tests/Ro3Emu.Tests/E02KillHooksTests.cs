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
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>E02 mandatory kill hooks share the E04 parent, including all credited role owners.</summary>
public sealed class E02KillHooksTests : IAsyncLifetime
{
    private const int Card = 1420100, Npc = 5000055;
    private const long Quest = 10104080;
    private readonly string _directory = Path.Combine(RepoRoot(), ".loadtest-temp", "e02-hooks-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> action) => Service<GameLoop>().InvokeAsync(action);
    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        Service<SeasonService>().SetPlayBook(101);
        await _app.StartAsync(); _http = $"http://127.0.0.1:{web}";
    }
    public async Task DisposeAsync()
    {
        Service<AccountStore>().BeforeOwnedCommitForTest = null;
        Service<AccountStore>().BeforeOwnedApplyForTest = null;
        await _app.StopAsync(); await _app.DisposeAsync(); SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
    private async Task<BotClient> Enter(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long role = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(role); await WorldSettle.StallsAsync(_app.Services);
        await bot.Inbox.WaitAsync(e => e.IsGame(ShopService.ShopSync), TimeSpan.FromSeconds(10));
        return bot;
    }
    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync(_http + "/ro3emu/gm?account=" + bot.Account, new StringContent(command))).EnsureSuccessStatusCode();
    }
    private async Task Prepare(BotClient bot)
    {
        await Gm(bot, "!lvl 37");
        await Gm(bot, "!quest done 10105090");
        await Gm(bot, "!quest accept " + Quest);
        await Gm(bot, "!warp 10111");
        await bot.Inbox.WaitAsync(e => e.IsGame(MsgId.SwitchSceneLine), TimeSpan.FromSeconds(10));
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 10111 });
        await bot.Inbox.WaitAsync(e => e.IsGame(MsgId.SceneLoaded), TimeSpan.FromSeconds(10));
        await OnLoop(() =>
        {
            Service<GameLoop>().Remove(Service<WildAfkService>()); // Freeze elapsed-time fixtures, not the kill callback.
            Service<RewardService>().QualifiesForBonus = null;
            Service<RewardService>().ResolveKillForTest = (_, _) => ([(1000000, 3), (1, 7), (Card, 1)], 3, 4);
            return Task.CompletedTask;
        });
    }
    private MonsterUnit Monster(GameSession killer) => new()
    {
        Uid = 900001, SceneEntry = killer.SceneEntry, RuntimeMap = killer.RuntimeMap,
        Template = Service<MonsterService>().Template(Npc)!, Hp = 0,
        Group = new SpawnGroup(-1, false, 1, 0, [], []), Home = new SpawnPoint(-1, 0, 0, 0, 0, Npc)
    };
    private Task Credit(GameSession participant, MonsterUnit monster) => OnLoop(() =>
    {
        foreach (var handler in Service<CombatService>().Dealt) handler(participant, monster, 1);
        return Task.CompletedTask;
    });
    private Task Commit(GameSession killer, MonsterUnit monster) => OnLoop(() => Service<RewardService>().PrepareKilledAsync(killer, monster));
    private long Count(GameSession s) => s.Tasks!.Infos[Quest].CompleteNum[0];
    private long SavedCount(long role) => DBTaskInfo.Parser.ParseFrom(Service<RoleStore>().LoadTasks(role)).Infos[Quest].CompleteNum[0];
    private WildAfkService.State SavedAfk(long role) => JsonSerializer.Deserialize<WildAfkService.State>(Service<RoleStore>().LoadWildAfk(role)!)!;
    private long Receipts()
    {
        using var db = Service<AccountStore>().Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM operation_receipt WHERE command_kind='monster-kill'";
        return (long)command.ExecuteScalar()!;
    }

    [ClientDataFact]
    public async Task SuspendedKillOwnsBothTaskParticipantsAndCommitsCardAfkAndSourceTogether()
    {
        await using var aliceBot = await Enter("Alice"); await Prepare(aliceBot);
        await using var bobBot = await Enter("Bob"); await Prepare(bobBot);
        var alice = Session(aliceBot); var bob = Session(bobBot); var monster = Monster(alice);
        long aliceRole = alice.Role!.Id, bobRole = bob.Role!.Id;
        await Credit(bob, monster);
        var catalog = EconomyCatalog.Load(Path.Combine(RepoRoot(), "data"));
        var opening = S3EconomyReader.Read(_directory, catalog);
        long beforeKills = alice.WildAfk!.Info.TodayKillCount;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Service<AccountStore>().BeforeOwnedCommitForTest = label =>
        {
            if (label != "economy/monster-kill") return Task.CompletedTask;
            entered.TrySetResult(); return release.Task;
        };
        var pending = Commit(alice, monster);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, Count(alice)); Assert.Equal(0, Count(bob));
            Assert.Equal(0, SavedCount(aliceRole)); Assert.Equal(0, SavedCount(bobRole));
            Assert.Equal(beforeKills, SavedAfk(aliceRole).Info.TodayKillCount);
            Assert.DoesNotContain(Service<ItemStore>().Load(aliceRole), x => x.Entry == Card);
            Assert.Equal(0, Receipts());
            var participantMutation = OnLoop(async () =>
            {
                using var held = await CharacterService.Lock(bob);
                Assert.Equal(1, Count(bob));
            });
            await Assert.ThrowsAsync<TimeoutException>(() => participantMutation.WaitAsync(TimeSpan.FromMilliseconds(150)));
            release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(10));
            await participantMutation.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.TrySetResult(); Service<AccountStore>().BeforeOwnedCommitForTest = null; }
        Assert.Equal(1, SavedCount(aliceRole)); Assert.Equal(1, SavedCount(bobRole));
        Assert.Equal(1, Count(alice)); Assert.Equal(1, Count(bob));
        Assert.True(DBLibraryInfo.Parser.ParseFrom(Service<RoleStore>().LoadLibrary(aliceRole)).Cards.ContainsKey(Card));
        var saved = SavedAfk(aliceRole);
        Assert.Equal(beforeKills + 1, saved.Info.TodayKillCount);
        Assert.Equal(1, saved.Info.DailyDropRecords[saved.DailyEpoch].MonsterKillCount[Npc]);
        Assert.Equal(3, saved.TotalDrops[1000000]); Assert.Equal(7, saved.TotalDrops[1]);
        Assert.Equal(1, saved.TotalDrops[Card]); Assert.Equal(3, saved.TotalDrops[100]); Assert.Equal(4, saved.TotalDrops[101]);
        Assert.Equal(1, Receipts());
        var closing = S3EconomyReader.Read(_directory, catalog);
        var ledger = S3EconomyReader.ReadLedger(_directory, catalog, opening, closing)!;
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.DoesNotContain(check.Issues, x => x.Kind == EconomyIssueKind.Corruption);
        Assert.All(check.Assets, x => { Assert.Equal("0", x.Residual); Assert.Equal("0", x.LegResidual); });
        Assert.Equal(monster.RewardSource!.Key, Assert.Single(ledger.Receipts).SourceKeys!.Single());

        // Stop before RewardService's post-kill observers. Login must recover the already durable core.
        Task? retired = null;
        await OnLoop(() => { retired = Service<RoleHandover>().Begin(alice); return Task.CompletedTask; });
        await retired!.WaitAsync(TimeSpan.FromSeconds(10));
        await using var again = await Enter("Alice", create: false);
        var reloaded = Session(again);
        Assert.Equal(1, Count(reloaded)); Assert.Equal(beforeKills + 1, reloaded.WildAfk!.Info.TodayKillCount);
        Assert.True(reloaded.Library!.Cards.ContainsKey(Card));
        Assert.False(Service<RoleStore>().HasTaskFollowup(aliceRole));
        var replay = Monster(reloaded);
        replay.RewardSource = monster.RewardSource;
        await Commit(reloaded, replay); // A fresh runtime object must consult the durable source marker.
        Assert.Equal(1, Receipts()); Assert.Equal(beforeKills + 1, SavedAfk(aliceRole).Info.TodayKillCount);
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KillHooksSurviveLostAcknowledgementOrApplyFailureAndFreshHostRecovery(bool failApply)
    {
        await using var bot = await Enter("Alice"); await Prepare(bot);
        var player = Session(bot); long role = player.Role!.Id; var monster = Monster(player);
        if (failApply)
            Service<AccountStore>().BeforeOwnedApplyForTest = label => label == "economy/monster-kill"
                ? throw new IOException("E02 fault after parent commit") : Task.CompletedTask;
        else Service<AccountStore>().FailNextEconomyAcknowledgementForTest();
        if (failApply) await Assert.ThrowsAsync<IOException>(() => Commit(player, monster));
        else await Commit(player, monster);
        Service<AccountStore>().BeforeOwnedApplyForTest = null;
        Assert.Equal(1, Receipts()); Assert.Equal(1, SavedCount(role));
        Assert.Equal(1, SavedAfk(role).Info.TodayKillCount);
        Assert.True(DBLibraryInfo.Parser.ParseFrom(Service<RoleStore>().LoadLibrary(role)).Cards.ContainsKey(Card));
        var source = monster.RewardSource;
        // Rebuild the entire service graph from the same database; no old settlement/session survives.
        await _app.StopAsync(); await _app.DisposeAsync(); SqliteConnection.ClearAllPools();
        await InitializeAsync();
        await using var again = await Enter("Alice", create: false);
        var recovered = Session(again);
        Assert.Equal(1, Count(recovered)); Assert.Equal(1, recovered.WildAfk!.Info.TodayKillCount);
        Assert.Equal(1, recovered.Items.Single(i => i.Entry == Card).Stack);
        Assert.True(recovered.Library!.Cards.ContainsKey(Card));
        Assert.False(Service<RoleStore>().HasTaskFollowup(role));
        var replay = Monster(recovered); replay.RewardSource = source;
        await Commit(recovered, replay);
        Assert.Equal(1, Receipts()); Assert.Equal(1, SavedAfk(role).Info.TodayKillCount);
        Assert.Equal(1, SavedCount(role)); Assert.Equal(1, recovered.Items.Single(i => i.Entry == Card).Stack);
    }

    [ClientDataFact]
    public async Task FailedKillRetryUsesNewTaskAndAfkSnapshotWithoutCreditingFutureTasks()
    {
        await using var aliceBot = await Enter("Alice"); await Prepare(aliceBot);
        await using var bobBot = await Enter("Bob"); await Prepare(bobBot);
        var alice = Session(aliceBot); var bob = Session(bobBot); var monster = Monster(alice);
        await Credit(bob, monster);
        long role = alice.Role!.Id;
        FixtureSql.Execute(Service<AccountStore>(), "CREATE TRIGGER e02_kill_fail BEFORE INSERT ON operation_receipt WHEN NEW.command_kind='monster-kill' BEGIN SELECT RAISE(ABORT,'E02 parent rollback'); END");
        await Assert.ThrowsAsync<SqliteException>(() => Commit(alice, monster));
        Assert.Equal(0, Receipts()); Assert.Equal(0, Count(alice)); Assert.Equal(0, Count(bob));
        Assert.Equal(0, SavedCount(role)); Assert.Equal(0, SavedCount(bob.Role!.Id));
        Assert.DoesNotContain(Service<ItemStore>().Load(role), x => x.Entry == Card);
        Assert.Equal(0, SavedAfk(role).Info.TodayKillCount);
        FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER e02_kill_fail");
        await OnLoop(async () =>
        {
            using var held = await CharacterService.Lock(alice, [ReservationKey.Role(bob.Role.Id)]);
            foreach (var (s, value) in new[] { (alice, 3L), (bob, 6L) })
            {
                s.Tasks = s.Tasks!.Clone(); s.Tasks.Infos[Quest].CompleteNum[0] = value;
                Service<RoleStore>().SaveTasks(s.Role!.Id, s.Tasks.ToByteArray());
            }
            var afk = alice.WildAfk!;
            afk.Info.TodayKillCount = 5; afk.TotalDrops[1] = 19;
            afk.RemainderMs = 427; afk.ConsumedTodaySeconds = 17;
            afk.Persisted = JsonSerializer.SerializeToUtf8Bytes(afk);
            Service<RoleStore>().SaveWildAfk(role, afk.Persisted);
        });
        await Commit(alice, monster);
        Assert.Equal(4, Count(alice)); Assert.Equal(4, SavedCount(role));
        Assert.Equal(7, Count(bob)); Assert.Equal(7, SavedCount(bob.Role.Id));
        Assert.Equal(6, alice.WildAfk!.Info.TodayKillCount); Assert.Equal(6, SavedAfk(role).Info.TodayKillCount);
        Assert.Equal(26, SavedAfk(role).TotalDrops[1]); Assert.Equal(427, SavedAfk(role).RemainderMs);
        Assert.Equal(17, SavedAfk(role).ConsumedTodaySeconds);
        Assert.Equal(1, Receipts());
        await Gm(bobBot, "!quest accept 301010");
        await Commit(alice, monster);
        Assert.Equal(0, bob.Tasks!.Infos[301010].CompleteNum[0]);
        Assert.Equal(4, Count(alice)); Assert.Equal(7, Count(bob)); Assert.Equal(1, Receipts());
    }
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }
}
