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

/// <summary>E04 fixed rewards, real role ownership/writer and wire presentation. No load runs.</summary>
public sealed class KillEconomyTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(RepoRoot(), ".loadtest-temp", "e04-kill-" + Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private string http = "";
    private T Service<T>() where T : notnull => app.Services.GetRequiredService<T>();
    private static readonly Lazy<EconomyCatalog> Catalog = new(() => EconomyCatalog.Load(Path.Combine(RepoRoot(), "data")));
    private GameSession Session => Service<SessionRegistry>().All().Single(s => s.Account == "killtest" && s.Role != null);
    private Task OnLoop(Func<Task> action) => Service<GameLoop>().InvokeAsync(action);
    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = directory; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        SureHitRolls.Install(app);
        await app.StartAsync(); http = $"http://127.0.0.1:{web}";
    }
    public async Task DisposeAsync()
    {
        Service<AccountStore>().BeforeOwnedCommitForTest = null;
        Service<AccountStore>().BeforeOwnedApplyForTest = null;
        await app.StopAsync(); await app.DisposeAsync(); SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }
    private async Task<BotClient> Enter(bool create = true)
    {
        var bot = new BotClient(http, File.ReadAllText(Path.Combine(directory, "gate_rsa_public.pem")));
        await bot.LoginAsync("killtest", "pw"); await bot.EnterGateAsync();
        long role = create ? (await bot.CreateRoleAsync("KillTest")).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(role);
        await bot.Inbox.WaitAsync(e => e.IsGame(ShopService.ShopSync), TimeSpan.FromSeconds(10));
        await OnLoop(() =>
        {
            Service<RewardService>().QualifiesForBonus = null;
            Service<RewardService>().KillDropsGranted.Clear();
            Service<RewardService>().ResolveKillForTest = (_, _) => ([(1000000, 3), (1, 7), (2010121, 1)], 3, 4);
            Service<CharacterService>().AutoDecomposeReturns = (_, item) => item.Entry == 2010121 ? [(1, 11L)] : [];
            return Task.CompletedTask;
        });
        return bot;
    }
    private MonsterUnit Monster(long uid = 77) => new()
    {
        Uid = uid, SceneEntry = Session.SceneEntry, RuntimeMap = Session.RuntimeMap,
        Template = new MonsterTemplate(10001, 1, 10, 0, 1, 1, 0, 0, 1, 0, 0, 100, 1, 1, 1, 1,
            false, 0, 1000, 5, 10, 0, 1000, .1f, 1000, 1, 500),
        Group = new SpawnGroup(-1, false, 1, 0, [], []), Home = new SpawnPoint(-1, 0, 0, 0, 0, 10001), Hp = 0
    };
    private Task Reward(GameSession player, MonsterUnit monster) => Service<CombatService>().Killed
        .Single(h => h.Target == Service<RewardService>())(player, monster);
    private EconomySnapshot Cut() => S3EconomyReader.Read(directory, Catalog.Value);
    private long ReceiptCount()
    {
        using var db = Service<AccountStore>().Open(); using var c = db.CreateCommand();
        c.CommandText = "SELECT count(*) FROM operation_receipt WHERE command_kind='monster-kill'"; return (long)c.ExecuteScalar()!;
    }

    [ClientDataFact]
    public async Task LethalHitAndCorpseWaitForDurableRewardBarrier()
    {
        await using var bot = await Enter();
        using (var client = new HttpClient())
            (await client.PostAsync(http + "/ro3emu/gm?account=killtest", new StringContent("!job 1100"))).EnsureSuccessStatusCode();
        var player = Session;
        var monster = await Service<GameLoop>().InvokeAsync(async () =>
        {
            player.Progress!.Skills[11001] = 1;
            var monsters = Service<MonsterService>();
            var target = await monsters.SpawnSingleAsync(player.SceneEntry, 5000001,
                new MapPoint(player.Position.X, player.Position.Y, player.Position.Z, 0), player.InstanceId,
                customTemplate: monsters.Template(5000001)! with { RoamDistance = 0 });
            target!.Hp = 1; return target;
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Service<AccountStore>().BeforeOwnedCommitForTest = label =>
        {
            if (label != "economy/monster-kill") return Task.CompletedTask;
            entered.TrySetResult(); return release.Task;
        };
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100101, TargetId = monster.Uid });
        var cast = CastSkillRsp.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.UseSkill), TimeSpan.FromSeconds(10))).Body);
        Assert.Equal(0, cast.Result);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(monster.Dead); Assert.Equal(0, ReceiptCount());
            await Assert.ThrowsAsync<TimeoutException>(async () => await bot.Inbox.WaitAsync(e =>
                e.IsGame(CombatService.SkillHit) && SkillHitInfo.Parser.ParseFrom(e.Body).SessionId == cast.SessionId,
                TimeSpan.FromMilliseconds(150)));
            release.TrySetResult();
            var hit = SkillHitInfo.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.SkillHit)
                && SkillHitInfo.Parser.ParseFrom(e.Body).SessionId == cast.SessionId, TimeSpan.FromSeconds(10))).Body);
            Assert.True(Assert.Single(hit.Dsts).IsDead); Assert.Equal(1, ReceiptCount());
            await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.GetRewardNotify)
                && GetRewardNotify.Parser.ParseFrom(e.Body).RewardType.Contains(8), TimeSpan.FromSeconds(10));
            Assert.True(monster.Dead);
        }
        finally { release.TrySetResult(); }
    }

    [ClientDataFact]
    public async Task PostcommitEffectFaultStillFinalizesAndObservesTheKillOnce()
    {
        await using var bot = await Enter();
        using (var client = new HttpClient())
            (await client.PostAsync(http + "/ro3emu/gm?account=killtest", new StringContent("!job 1100"))).EnsureSuccessStatusCode();
        var player = Session;
        var monster = await Service<GameLoop>().InvokeAsync(async () =>
        {
            player.Progress!.Skills[11001] = 1;
            var monsters = Service<MonsterService>();
            var target = await monsters.SpawnSingleAsync(player.SceneEntry, 5000001,
                new MapPoint(player.Position.X, player.Position.Y, player.Position.Z, 0), player.InstanceId,
                customTemplate: monsters.Template(5000001)! with { RoamDistance = 0 });
            target!.Hp = 1; return target;
        });
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int observerCalls = 0;
        Service<CombatService>().DealtAsync.Add((_, target, _, _) => target == monster
            ? throw new IOException("postcommit effect failure") : Task.CompletedTask);
        Service<CombatService>().Killed.Add((_, target) =>
        {
            if (target == monster) { observerCalls++; observed.TrySetResult(); }
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100101, TargetId = monster.Uid });
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(monster.Dead);
        Assert.True(monster.KillObserversStarted);
        Assert.Equal(1, observerCalls);
        Assert.Equal(1, ReceiptCount());
        await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.GetRewardNotify)
            && GetRewardNotify.Parser.ParseFrom(e.Body).RewardType.Contains(8), TimeSpan.FromSeconds(10));
        await OnLoop(() => Reward(player, monster));
        Assert.Equal(1, observerCalls);
        Assert.Equal(1, ReceiptCount());
    }

    [ClientDataFact]
    public async Task RepeatedLethalCallbackCommitsWholeConvertedOutcomeOnceAndNewSpawnIsDistinct()
    {
        await using var bot = await Enter(); var player = Session; var monster = Monster();
        var opening = Cut(); long currency = player.Progress!.Currency[1], exp = player.Progress.Exp;
        await OnLoop(async () => { await Reward(player, monster); await Reward(player, monster); });
        var packet = GetRewardNotify.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.GetRewardNotify)
            && GetRewardNotify.Parser.ParseFrom(e.Body).RewardType.Contains(8), TimeSpan.FromSeconds(10))).Body);
        Assert.Equal(monster.Template.NpcId, packet.TargetEntry);
        Assert.Equal(currency + 18, player.Progress.Currency[1]); Assert.Equal(exp + 3, player.Progress.Exp);
        Assert.Equal(3, player.Progress.JobLevel); Assert.Equal(2, player.Progress.JobExp); // novice levels 1 and 2 each consume 1 EXP
        Assert.Equal(23, player.Items.Single(i => i.Entry == 1000000).Stack);
        Assert.Single(player.Items, i => i.Entry == 2010121); // starter weapon, generated loot decomposed
        Assert.Equal(1, ReceiptCount());
        var closing = Cut(); var ledger = S3EconomyReader.ReadLedger(directory, Catalog.Value, opening, closing)!;
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.DoesNotContain(check.Issues, x => x.Kind == EconomyIssueKind.Corruption);
        Assert.All(check.Assets, x => { Assert.Equal("0", x.Residual); Assert.Equal("0", x.LegResidual); });
        Assert.Equal(monster.RewardSource!.Key, Assert.Single(ledger.Receipts).SourceKeys!.Single());
        var newSpawn = Monster(); await OnLoop(() => Reward(player, newSpawn));
        Assert.NotEqual(monster.RewardSource, newSpawn.RewardSource); Assert.Equal(2, ReceiptCount());
    }

    [ClientDataFact]
    public async Task HeldCommitThenDetachPaysOriginalRoleOnceWithoutEarlyPresentation()
    {
        var bot = await Enter(); var player = Session; long role = player.Role!.Id; var monster = Monster();
        long currency = player.Progress!.Currency[1];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Service<AccountStore>().BeforeOwnedCommitForTest = label =>
        {
            if (label != "economy/monster-kill") return Task.CompletedTask;
            entered.TrySetResult(); return release.Task;
        };
        var pending = OnLoop(() => Reward(player, monster));
        Task? detached = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, ReceiptCount()); Assert.Equal(currency, Service<RoleStore>().LoadProgress(role).Currency[1]);
            await Assert.ThrowsAsync<TimeoutException>(async () => await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.GetRewardNotify), TimeSpan.FromMilliseconds(150)));
            await OnLoop(() => { detached = Service<RoleHandover>().Begin(player); Assert.True(player.Attachment!.Cancelled); return Task.CompletedTask; });
            release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(10));
            await detached!.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.TrySetResult(); await bot.DisposeAsync(); }
        Assert.Equal(1, ReceiptCount()); Assert.Equal(currency + 18, Service<RoleStore>().LoadProgress(role).Currency[1]);
        await using var again = await Enter(create: false);
        await OnLoop(() => Reward(Session, monster));
        Assert.Equal(1, ReceiptCount()); Assert.Equal(currency + 18, Session.Progress!.Currency[1]);
    }

    [ClientDataFact]
    public async Task LostAcknowledgementAndPostcommitNoticeFaultNeverPayAgain()
    {
        await using var bot = await Enter(); var player = Session; var monster = Monster();
        Service<AccountStore>().FailNextEconomyAcknowledgementForTest();
        Service<RewardService>().KillDropsGranted.Add((_, _, _) => throw new IOException("postcommit observer"));
        await Assert.ThrowsAsync<IOException>(() => OnLoop(() => Reward(player, monster)));
        Assert.True(monster.RewardSettlement!.Committed); Assert.Equal(1, ReceiptCount());
        var committed = Service<RoleStore>().LoadProgress(player.Role!.Id);
        await OnLoop(() => Reward(player, monster));
        Assert.Equal(1, ReceiptCount()); Assert.Equal(committed.Currency[1], player.Progress!.Currency[1]);
    }

    [ClientDataFact]
    public async Task LastLedgerFailureRollsBackItemsCurrencyExpAndSource()
    {
        await using var bot = await Enter(); var player = Session; var monster = Monster();
        var before = Service<RoleStore>().LoadProgress(player.Role!.Id);
        FixtureSql.Execute(Service<AccountStore>(), "CREATE TRIGGER kill_fail BEFORE INSERT ON economy_entry WHEN NEW.leg_no=1 BEGIN SELECT RAISE(ABORT,'kill rollback'); END");
        await Assert.ThrowsAsync<SqliteException>(() => OnLoop(() => Reward(player, monster)));
        var after = Service<RoleStore>().LoadProgress(player.Role.Id);
        Assert.Equal(before.Currency[1], after.Currency[1]); Assert.Equal(before.Exp, after.Exp); Assert.Equal(before.JobExp, after.JobExp);
        Assert.Equal(20, player.Items.Single(i => i.Entry == 1000000).Stack); Assert.Equal(0, ReceiptCount());
        FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER kill_fail");
        monster.RewardCommit = null;
        await OnLoop(() => Reward(player, monster)); Assert.Equal(1, ReceiptCount());
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }
}
