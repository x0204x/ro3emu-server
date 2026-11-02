using MessagePack;
using Microsoft.AspNetCore.Builder;
using Ro3Emu.Bot;
using Ro3Emu.Server;

namespace Ro3Emu.Tests;

/// <summary>Friends over the micro gate: calls, pushes and online state.</summary>
public sealed class SocialTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-social-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = SyntheticData.Dir;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<(BotClient Bot, long Id)> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return (bot, id);
    }

    private static async Task<object?[]> Push(BotClient bot, string method) =>
        MessagePackSerializer.Deserialize<object?[]>((await bot.Inbox.WaitAsync(e => e.Name == "push:FriendService." + method, Wait)).Body);

    private static bool Ok(Dictionary<object, object?> resp) => resp["Ret"] is object?[] ret && ret[0] is true;

    [Fact]
    public async Task FriendRequestAcceptListAndOnlineStatus()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await using var _ = alice;

        // Search by name (and by a typed uid), then apply: Bob gets onFriendApply [Alice].
        var found = await alice.MicroAsync("NameService", "getUidByName", new() { ["Type"] = "Area0", ["Name"] = "bob" });
        Assert.Equal(bobId.ToString(), found["Uid"]);
        Assert.Equal(bobId.ToString(), (await alice.MicroAsync("NameService", "getUidByName", new() { ["Name"] = bobId.ToString() }))["Uid"]);
        Assert.True(Ok(await alice.MicroAsync("FriendService", "apply", new() { ["Uid"] = aliceId.ToString(), ["Target"] = bobId.ToString() })));
        Assert.Equal(aliceId.ToString(), (await Push(bob, "onFriendApply"))[0]);
        var again = await alice.MicroAsync("FriendService", "apply", new() { ["Uid"] = aliceId.ToString(), ["Target"] = bobId.ToString() });
        Assert.Equal("TID_MS_FRIEND_APPLY_ALREADY_IN_APPLICATIONS", ((object?[])again["Ret"]!)[1]);

        var applicants = await bob.MicroAsync("FriendService", "fetchApplicants", new() { ["Uid"] = bobId.ToString() });
        Assert.Equal(aliceId.ToString(), ((Dictionary<object, object?>)((object?[])applicants["Applicants"]!).Single()!)["uid"]);

        // Accept echoes the applicant; Alice gets onFriendAccept [Bob]; both lists hold the other with a DataMap.
        var accepted = await bob.MicroAsync("FriendService", "accept", new() { ["Uid"] = bobId.ToString(), ["Applicant"] = aliceId.ToString() });
        Assert.Equal(aliceId.ToString(), accepted["Applicant"]);
        Assert.Equal(bobId.ToString(), (await Push(alice, "onFriendAccept"))[0]);
        var info = await alice.MicroAsync("FriendService", "fetchFriendsInfo", new() { ["Uid"] = aliceId.ToString() });
        Assert.Equal(bobId.ToString(), ((Dictionary<object, object?>)((object?[])info["Friends"]!).Single()!)["uid"]);
        var relation = (Dictionary<object, object?>)((object?[])info["RelationData"]!).Single()!;
        Assert.IsType<Dictionary<object, object?>>(relation["DataMap"]);
        Assert.Empty((object?[])info["Blacklist"]!);

        // Online state for the friend list: OfflineTime -1 while in the game.
        var attrs = await alice.GetRoleAttributesAsync(bobId);
        var data = (Dictionary<object, object?>)((Dictionary<object, object?>)((object?[])attrs["Results"]!).Single()!)["Data"]!;
        Assert.Equal(true, data["Online"]);
        Assert.Equal(-1L, Convert.ToInt64(data["OfflineTime"]));

        // Bob leaves: Alice is told he went offline.
        await bob.DisposeAsync();
        var status = (Dictionary<object, object?>)(await Push(alice, "onFriendSyncStatus"))[0]!;
        Assert.Equal(bobId.ToString(), status["Uid"]);
        Assert.Equal("offline", status["Status"]);
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    [Fact]
    public async Task PartyCreateInviteAcceptAndLeave()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;

        // Create: 42000 (team id) -> 42026 about itself (puts the client in the party) -> 42048 with a flagList.
        await alice.SendGameAsync(Server.Game.TeamService.TeamCreate, new Romsg.TeamCreateReq { TeamType = Romsg.TeamType.Five, TargetId = 1001 });
        var created = await Next(alice, Server.Game.TeamService.TeamCreate, Romsg.TeamCreateResp.Parser);
        Assert.Equal(0, created.Result);
        Assert.NotEqual(0, created.TeamId);
        var self = await Next(alice, Server.Game.TeamService.TeamNewMember, Romsg.BroadCastNewMemberEnter.Parser);
        Assert.Equal(aliceId, self.DetailInfo.Guid);
        Assert.Equal(Romsg.TeamTitleType.Leader, self.DetailInfo.Title);
        Assert.NotNull(self.DetailInfo.BattleInfo.Pos);
        var all = await Next(alice, Server.Game.TeamService.TeamAll, Romsg.TeamTotalDetailInfo.Parser);
        Assert.NotEmpty(all.Info.FlagList);
        Assert.Equal(created.TeamId, all.Info.Info.TeamId);

        // Invite: Bob gets 42005 with the inviter in memberList.
        await alice.SendGameAsync(Server.Game.TeamService.TeamInvite, new Romsg.InviteIntoTeamReq { Guid = bobId });
        Assert.Equal(0, (await Next(alice, Server.Game.TeamService.TeamInvite, Romsg.InviteIntoTeamResp.Parser)).Result);
        var invite = await Next(bob, Server.Game.TeamService.TeamNotifyInvitee, Romsg.NotifyInvitee.Parser);
        Assert.Equal(aliceId, invite.InviteGuid);
        Assert.Contains(aliceId, invite.TeamTotalSimpleInfo.MemberList);

        // Accept: Bob is in (42026 about Bob, 42048 with both), Alice sees Bob join.
        await bob.SendGameAsync(Server.Game.TeamService.TeamInviteeResp, new Romsg.InviteeResp { TeamId = created.TeamId, IsAgree = true, InviteGuid = aliceId });
        Assert.Equal(0, (await Next(bob, Server.Game.TeamService.TeamInviteeResp, Romsg.InviteeRespResp.Parser)).Result);
        Assert.Equal(bobId, (await Next(bob, Server.Game.TeamService.TeamNewMember, Romsg.BroadCastNewMemberEnter.Parser)).DetailInfo.Guid);
        Assert.Equal(2, (await Next(bob, Server.Game.TeamService.TeamAll, Romsg.TeamTotalDetailInfo.Parser)).MemberList.Count);
        var joined = await Next(alice, Server.Game.TeamService.TeamNewMember, Romsg.BroadCastNewMemberEnter.Parser, m => m.DetailInfo.Guid == bobId);
        Assert.Equal(1, joined.DetailInfo.TeamIndex);

        // Leave: Bob's reply resets him; Alice gets 42037 with the remaining slots.
        await bob.SendGameAsync(Server.Game.TeamService.TeamLeave, new Romsg.LeaveTeamReq());
        Assert.Equal(0, (await Next(bob, Server.Game.TeamService.TeamLeave, Romsg.LeaveTeamResp.Parser)).Result);
        var left = await Next(alice, Server.Game.TeamService.TeamBroadcastLeave, Romsg.BroadCastMemberLeave.Parser);
        Assert.Equal(bobId, left.Guid);
        Assert.Equal(0, left.MemberIndex[aliceId]);
    }

}
