using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The hidden Connect quests (TaskConfig type 19, TaskService.Connect.cs): started for every role, counted from a party
/// activity, delivered through action 116 with its intimacy gain; and condition 1102 (CheckHasLearnedSkill) of the
/// rental side quests.
/// </summary>
public sealed class ConnectQuestTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-connect-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task<T> Read<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));
    private GameSession Session(string account) => Service<SessionRegistry>().All().Single(s => s.Account == account && s.Role != null);

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser) where T : IMessage<T>
        => parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait)).Body);

    private static async Task Party(BotClient leader, BotClient member)
    {
        await leader.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        var created = await Next(leader, TeamService.TeamCreate, TeamCreateResp.Parser);
        Assert.Equal(0, created.Result);
        await leader.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = (long)member.Self!.Uuid });
        Assert.Equal(0, (await Next(leader, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
        await Next(member, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
        await member.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp
        { TeamId = created.TeamId, IsAgree = true, InviteGuid = (long)leader.Self!.Uuid });
        Assert.Equal(0, (await Next(member, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);
    }

    private Task BossCleared(string account, long boss, params string[] party) => Service<GameLoop>().InvokeAsync(() =>
        Service<TaskService>().OnConnectBossClearedAsync(Session(account), boss, [.. party.Select(Session)]));

    [ClientDataFact]
    public async Task EveryConnectQuestStartsWithTheRole()
    {
        await using var alice = await InWorld("Alice");
        var connect = Service<Ro3Emu.Server.Data.GameData>().Table("TaskConfig").Values
            .Select(r => new Ro3Emu.Server.Data.Config.TaskConfig(r)).Where(r => r.TaskType == 19).Select(r => r.TaskId).ToArray();
        Assert.Equal(53, connect.Length);
        var running = await Read(() => connect.Where(id => Session("alice").Tasks!.Infos.GetValueOrDefault(id)?.Status == TaskState.Running).ToArray());
        Assert.Equal(connect.Order(), running.Order());
        Assert.True(await Read(() => connect.All(Service<TaskService>().TargetsSupported)));
    }

    [ClientDataFact]
    public async Task RaidBossClearedWithAPartyMateDeliversAndRaisesIntimacy()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        long aliceId = await Read(() => Session("alice").Role!.Id), bobId = await Read(() => Session("bob").Role!.Id);
        await Service<FriendStore>().LinkAsync(aliceId, bobId);
        await Party(alice, bob);
        // 1904000 [107005,1,4300001] "Defeat Golden Thief Bug (Normal) in a party", 116 4000: GainIntimacy 5.
        await QuestProbe.AtAsync(_app, "alice", 1904000, 0);

        await BossCleared("alice", 4300001, "alice"); // alone: not with a party
        Assert.Equal(0, (await QuestProbe.ReadAsync(_app, "alice", 1904000))!.CompleteNum[0]);

        await BossCleared("alice", 4300001, "alice", "bob");
        var done = await QuestProbe.WaitAsync(_app, "alice", 1904000, i => i.Status == TaskState.Delivery, "delivered");
        Assert.Equal(1, done.CompleteNum[0]);
        Assert.Equal(1, await Read(() => Session("alice").Tasks!.CompleteTaskNum[1904000]));
        Assert.Equal(TaskState.Running, (await QuestProbe.ReadAsync(_app, "alice", 1904090))!.Status); // another boss
        Assert.Equal(TaskState.Running, (await QuestProbe.ReadAsync(_app, "bob", 1904000))!.Status); // Bob's own counts apart
        var friends = Service<FriendStore>();
        await IntimacyAsync(friends, aliceId, bobId, 5);
        Assert.Equal(5, await friends.IntimacyAsync(bobId, aliceId));

        // 1904090 [107005,1,4300000] "Hard Golden Thief Bug", 116 4090: GainIntimacy 10.
        await BossCleared("alice", 4300000, "alice", "bob");
        await QuestProbe.WaitAsync(_app, "alice", 1904090, i => i.Status == TaskState.Delivery, "delivered");
        await IntimacyAsync(friends, aliceId, bobId, 15);

        // Delivered once: a later clear changes nothing.
        await BossCleared("alice", 4300001, "alice", "bob");
        await Task.Delay(200);
        Assert.Equal(1, await Read(() => Session("alice").Tasks!.CompleteTaskNum[1904000]));
        Assert.Equal(15, await friends.IntimacyAsync(aliceId, bobId));
    }

    [ClientDataFact]
    public async Task BonusTimeCountsOnlyWhileAPartyMateOnTheMapRunsItToo()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        // 1902000 [106014,1] "Auto-battle in a party for @{1} minutes".
        await QuestProbe.AtAsync(_app, "alice", 1902000, 0);
        Task Consumed(long seconds) => Service<GameLoop>().InvokeAsync(() =>
            Service<TaskService>().OnBonusTimeConsumedAsync(Session("alice"), seconds));
        Task BobRuns(bool running) => Service<GameLoop>().InvokeAsync(() =>
        {
            var info = Session("bob").WildAfk!.Info;
            info.IsHighMagnification = running;
            info.IsSuspended = false;
            return Task.CompletedTask;
        });

        await BobRuns(true);
        await Consumed(120); // not in a party yet
        await Party(alice, bob);
        await BobRuns(false);
        await Consumed(120); // Bob's Bonus Time is off
        Assert.Equal(0, (await QuestProbe.ReadAsync(_app, "alice", 1902000))!.CompleteNum[0]);

        await BobRuns(true);
        await Consumed(45);
        Assert.Equal(0, (await QuestProbe.ReadAsync(_app, "alice", 1902000))!.CompleteNum[0]);
        await Consumed(15); // the first whole minute together
        await QuestProbe.WaitAsync(_app, "alice", 1902000, i => i.Status == TaskState.Delivery, "delivered");
    }

    private static async Task IntimacyAsync(FriendStore friends, long role, long friend, int expected)
    {
        for (var until = DateTime.UtcNow + Wait; await friends.IntimacyAsync(role, friend) != expected; await Task.Delay(25))
            Assert.True(DateTime.UtcNow < until, $"intimacy {await friends.IntimacyAsync(role, friend)}, not {expected}");
    }

    [ClientDataFact]
    public async Task LearnedSkillConditionReadsTheSkillGroup()
    {
        await using var alice = await InWorld("Alice");
        var functions = Service<FunctionService>();
        // 88000101 [1102,16003]: side quest 10200150 "Rent a Cart Merchant" asks for Cart Use.
        Assert.False(await Read(() => functions.ConditionMet(Session("alice"), 88000101)));
        Assert.True(await Read(() =>
        {
            var progress = Session("alice").Progress!;
            progress.Skills[16003] = 1;
            bool met = functions.ConditionMet(Session("alice"), 88000101);
            progress.Skills.Remove(16003);
            return met;
        }));
    }
}
