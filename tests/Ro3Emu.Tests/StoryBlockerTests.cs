using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Fast regressions, one per main-story blocker the story walk (StoryWalkTests, opt-in) found.
/// </summary>
public sealed class StoryBlockerTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-storyblock-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{http}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name = "StoryAlice")
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        return bot;
    }

    private async Task Gm(string line) => await Gm("storyalice", line);

    private async Task Gm(string account, string line)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(line))).EnsureSuccessStatusCode();
    }

    private Task<T> Read<T>(Func<GameSession, T> read) => Service<GameLoop>().InvokeAsync(() =>
        Task.FromResult(read(Service<SessionRegistry>().All().Single(s => s.Role != null))));

    private async Task<T> Request<T>(BotClient bot, uint id, IMessage body, MessageParser<T> parser) where T : IMessage<T>
    {
        using var reply = bot.Inbox.Expect(e => e.IsGame(id));
        await bot.SendGameAsync(id, body);
        return parser.ParseFrom((await reply.WaitAsync(Wait)).Body);
    }

    /// <summary>A GM warp, then the client's 30005 scene-loaded report.</summary>
    private static async Task WarpAsync(BotClient bot, Func<string, Task> gm, long scene, float x, float z)
    {
        using var sw = bot.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine));
        await gm(FormattableString.Invariant($"!warp {scene} {x:0.0} {z:0.0}"));
        await LoadAsync(bot, SwitchSceneLineResp.Parser.ParseFrom((await sw.WaitAsync(Wait)).Body).SceneEntry);
    }

    private static async Task LoadAsync(BotClient bot, long scene)
    {
        using var self = bot.Inbox.Expect(e => e.IsGame(MsgId.EntitySelf));
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = scene });
        await self.WaitAsync(Wait);
    }

    [ClientDataFact]
    public async Task ARegionWithTheServerAction613EntersItsStoryDungeonWhileItsQuestRuns()
    {
        // sc_pay_fild03_001 region 1010209001 "613|1021301|1|10102100" for 10102100 [101018,1021301,1].
        await using var bot = await InWorld();
        var region = Service<WorldData>().Map(10213)!.Regions[1010209001];
        await WarpAsync(bot, Gm, 10213, region.X, region.Z);
        using (var early = bot.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine)))
        {
            await bot.SendGameAsync(SceneService.ClientEnterArea, new ClientEnterAreaReq { AreaId = region.Id });
            await Task.Delay(500);
            Assert.False(early.IsCompleted); // the quest is not running: the region does nothing
        }
        await Gm("!quest accept 10102100");
        using var sw = bot.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine) && SwitchSceneLineResp.Parser.ParseFrom(e.Body).SceneEntry == 1021301);
        await bot.SendGameAsync(SceneService.ClientEnterArea, new ClientEnterAreaReq { AreaId = region.Id });
        await sw.WaitAsync(Wait);
    }

    [ClientDataFact]
    public async Task APartyMemberWhoIsNotTheLeaderEntersTheRegionsStoryDungeonAlone()
    {
        // sc_prt_sewb4_001 (10153) region 1010626001 "613|3120402|1|10106270": dungeon 3120402 takes up to 10 players, so a
        // 30400 from a member who is not the leader would be refused with 1204 (issue #163).
        await using var alice = await InWorld();
        await using var bob = await InWorld("StoryBob");
        var created = await Request(alice, TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 },
            TeamCreateResp.Parser);
        Assert.Equal(0, created.Result);
        using (var invite = bob.Inbox.Expect(e => e.IsGame(TeamService.TeamNotifyInvitee)))
        {
            Assert.Equal(0, (await Request(alice, TeamService.TeamInvite, new InviteIntoTeamReq { Guid = (long)bob.Self!.Uuid },
                InviteIntoTeamResp.Parser)).Result);
            await invite.WaitAsync(Wait);
        }
        Assert.Equal(0, (await Request(bob, TeamService.TeamInviteeResp,
            new InviteeResp { TeamId = created.TeamId, IsAgree = true, InviteGuid = (long)alice.Self!.Uuid }, InviteeRespResp.Parser)).Result);

        var region = Service<WorldData>().Map(10153)!.Regions[1010626001];
        await WarpAsync(bob, line => Gm("storybob", line), 10153, region.X, region.Z);
        await Gm("storybob", "!quest accept 10106270");
        using var sw = bob.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine) && SwitchSceneLineResp.Parser.ParseFrom(e.Body).SceneEntry == 3120402);
        await bob.SendGameAsync(SceneService.ClientEnterArea, new ClientEnterAreaReq { AreaId = region.Id });
        await sw.WaitAsync(Wait);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(MsgId.SwitchSceneLine)
            && SwitchSceneLineResp.Parser.ParseFrom(e.Body).SceneEntry == 3120402);
    }

    [ClientDataFact]
    public async Task AFlyWingMovesThePlayerOnItsMapAndCountsForItsQuest()
    {
        // ItemConfig 1002001 [[101,0],[104],[106]]: 101 JumpSameMap is server-hosted; 10102150 [103042,1002001,1].
        await using var bot = await InWorld();
        await Gm("!item 1002001 1");
        await Gm("!quest accept 10102150");
        var point = Service<WorldData>().Map(10220)!.Points.Values.First();
        await WarpAsync(bot, Gm, 10220, point.X, point.Z);
        long guid = await Read(s => s.Items.Single(i => i.Entry == 1002001).Guid);
        var before = await Read(s => (s.Position.X, s.Position.Z));
        var reply = await Request(bot, CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 }, ItemUseResp.Parser);
        Assert.Equal(0, reply.Result);
        for (int i = 0; i < 100 && !await Read(s => TaskService.IsDelivered(s, 10102150)); i++) await Task.Delay(20);
        Assert.True(await Read(s => TaskService.IsDelivered(s, 10102150)));
        Assert.NotEqual(before, await Read(s => (s.Position.X, s.Position.Z)));
        Assert.Equal(10220, await Read(s => s.SceneEntry));
    }

    [ClientDataFact]
    public async Task AButterflyWingGoesToTheMapPickedOnTheMapPanel()
    {
        // ItemConfig 1002000 [[8,...],[102,0],[104]]; the map panel sends ItemUseParamInfo.butterflyWings.mapId.
        await using var bot = await InWorld();
        await Gm("!item 1002000 2");
        // Off the starting maps (ItemUseLimitConfig 1001 is not enforced; this only keeps the test map-independent).
        var point = Service<WorldData>().Map(10220)!.Points.Values.First();
        await WarpAsync(bot, Gm, 10220, point.X, point.Z);
        long guid = await Read(s => s.Items.Single(i => i.Entry == 1002000).Guid);
        var bare = await Request(bot, CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 }, ItemUseResp.Parser);
        Assert.NotEqual(0, bare.Result); // no destination picked
        Assert.Equal(2, await Read(s => s.Items.Where(i => i.Entry == 1002000).Sum(i => i.Stack)));
        using var sw = bot.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine) && SwitchSceneLineResp.Parser.ParseFrom(e.Body).SceneEntry == 10100);
        var use = new ItemUseReq { ItemGuid = guid, ItemCount = 1 };
        use.ItemParam.Add(new ItemUseParamInfo { ButterflyWings = new ItemUseParamInfo.Types.ButterflyWings { MapId = 10100 } });
        Assert.Equal(0, (await Request(bot, CharacterService.ItemUse, use, ItemUseResp.Parser)).Result);
        await sw.WaitAsync(Wait);
        Assert.Equal(1, await Read(s => s.Items.Where(i => i.Entry == 1002000).Sum(i => i.Stack)));
    }

    [ClientDataFact]
    public async Task TheGrowthManualOffersOneTitleAtATimeAndItsChapterIsClaimedAfterItsChildren()
    {
        // NewbieTitlesConfig title 1: chapter 701990 [104004, 701010..701060]; title 2 is 702990.
        await using var bot = await InWorld();
        long[] children = [701010, 701020, 701030, 701040, 701050, 701060];
        foreach (long id in children.Append(701990))
            Assert.Equal(TaskState.Running, await Read(s => s.Tasks!.Infos[id].Status));
        Assert.False(await Read(s => s.Tasks!.Infos.ContainsKey(702990)));
        var claim = new TaskDeliveryReq { Req = { new TaskDeliverySingleReq { Id = 701990 } } };
        Assert.Empty((await Request(bot, TaskService.TaskDelivery, claim, TaskDeliveryResp.Parser)).Data); // children first
        // 701010's craft target is event-counted, so a GM finish holds; the panel's 31205 claims it. The other children
        // read live state (talents, stat points, level): they are delivered by GM here.
        await Gm("!quest finish 701010");
        var child = await Request(bot, TaskService.TaskDelivery, new TaskDeliveryReq { Req = { new TaskDeliverySingleReq { Id = 701010 } } },
            TaskDeliveryResp.Parser);
        Assert.Single(child.Data);
        foreach (long id in children.Skip(1)) await Gm($"!quest done {id}");
        for (int i = 0; i < 100 && await Read(s => s.Tasks!.Infos[701990].Status) != TaskState.Finish; i++) await Task.Delay(20);
        Assert.Single((await Request(bot, TaskService.TaskDelivery, claim, TaskDeliveryResp.Parser)).Data);
        for (int i = 0; i < 100 && !await Read(s => s.Tasks!.Infos.ContainsKey(702990)); i++) await Task.Delay(20);
        foreach (long id in new long[] { 702020, 702030, 702040, 702050, 702060, 702990 })
            Assert.Equal(TaskState.Running, await Read(s => s.Tasks!.Infos[id].Status));
    }

    [ClientDataFact]
    public async Task AGrowthChapterWithAnUnmetConditionIsOfferedLocked()
    {
        // 706990 _iCondition 88000070 (base level 53): offered after 705990, flagged isChapterLocked.
        await using var bot = await InWorld();
        foreach (long chapter in new long[] { 701990, 702990, 703990, 704990, 705990 }) await Gm($"!quest done {chapter}");
        for (int i = 0; i < 100 && !await Read(s => s.Tasks!.Infos.ContainsKey(706990)); i++) await Task.Delay(20);
        Assert.True(await Read(s => s.Tasks!.Infos[706990].IsChapterLocked));
        await Gm("!lvl 53");
        for (int i = 0; i < 100 && await Read(s => s.Tasks!.Infos[706990].IsChapterLocked); i++) await Task.Delay(20);
        Assert.False(await Read(s => s.Tasks!.Infos[706990].IsChapterLocked));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
