using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Real game/micro wire checks for the extra social features.</summary>
public sealed class SocialExtraTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-social-extra-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
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

    private async Task<(BotClient Bot, long Id)> InWorld(string name, bool create = true, int sex = 1)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name, sex)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return (bot, id);
    }

    private static async Task<T> Next<T>(BotClient bot, uint msg, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(msg) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(packet.Body);
    }

    private static bool Ok(Dictionary<object, object?> value) => ((object?[])value["Ret"]!)[0] is true;

    [Fact]
    public async Task FortyMemberDirectoryApplicationAndSlotFlow()
    {
        var (alice, a) = await InWorld("Alice");
        var (bob, b) = await InWorld("Bob");
        await using var _ = alice;
        await using var __ = bob;

        await alice.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Forty, TargetId = 1001 });
        var created = await Next(alice, TeamService.TeamCreate, TeamCreateResp.Parser);
        Assert.Equal(0, created.Result);
        Assert.Equal(TeamType.Forty, created.TeamType);
        await Next(alice, TeamService.TeamNewMember, BroadCastNewMemberEnter.Parser);
        await Next(alice, TeamService.TeamAll, TeamTotalDetailInfo.Parser);

        await bob.SendGameAsync(TeamService.TeamChangeInfo, new ChangeTeamInfoReq { TeamName = "Raid", TeamDesc = "Test", TargetId = 1001 });
        Assert.NotEqual(0, (await Next(bob, TeamService.TeamChangeInfo, ChangeTeamInfoResp.Parser)).Result);
        await alice.SendGameAsync(TeamService.TeamChangeInfo, new ChangeTeamInfoReq { TeamName = "Raid", TeamDesc = "Test", TargetId = 1001 });
        Assert.Equal(0, (await Next(alice, TeamService.TeamChangeInfo, ChangeTeamInfoResp.Parser)).Result);
        Assert.Equal("Raid", (await Next(alice, TeamService.TeamBaseUpdate, TeamDetailInfo.Parser)).Info.TeamName);

        var directory = await bob.MicroPositionalAsync("PartyService", "getPartysSummary", [0, Array.Empty<object>(), Array.Empty<object>(), Array.Empty<object>(), 0, 100]);
        Assert.True(Ok(directory));
        var summary = (Dictionary<object, object?>)((object?[])directory["SummaryList"]!).Single()!;
        var decoded = TeamTotalSimpleInfo.Parser.ParseFrom((byte[])summary["GameSummaryData"]!);
        Assert.Equal(created.TeamId, decoded.Info.TeamId);
        var party = await bob.MicroPositionalAsync("PartyService", "getParty", [created.TeamId.ToString()]);
        Assert.True(Ok(party));
        Assert.NotNull(((Dictionary<object, object?>)party["Party"]!)["PartyData"]);

        await bob.SendGameAsync(TeamService.TeamEnter, new EnterTeamReq { TeamId = created.TeamId });
        Assert.Equal(0, (await Next(bob, TeamService.TeamEnter, EnterTeamResp.Parser)).Result);
        Assert.Equal(b, (await Next(alice, TeamService.TeamBaseUpdate, TeamDetailInfo.Parser, m => m.ApplyerList.ContainsKey(b))).ApplyerList.Keys.Single());
        await alice.SendGameAsync(TeamService.TeamApplyList, new ApplyListOperateReq { OperType = ApplyListOperateType.Agree, TargetGuid = b });
        Assert.Equal(0, (await Next(alice, TeamService.TeamApplyList, ApplyListOperateResp.Parser)).Result);
        Assert.Equal(b, (await Next(bob, TeamService.TeamNewMember, BroadCastNewMemberEnter.Parser)).DetailInfo.Guid);
        Assert.Equal(2, (await Next(bob, TeamService.TeamAll, TeamTotalDetailInfo.Parser)).MemberList.Count);

        await bob.SendGameAsync(TeamService.TeamChangeMemberState, new ChangeTeamMemberStateReq
        { OperGuid = b, MemberGuid = b, EOperType = ETeamMemberStateOperEnum.EteamMemberStateOperChat, ChatState = TeamMemberChatState.Close });
        Assert.Equal(0, (await Next(bob, TeamService.TeamChangeMemberState, ChangeTeamMemberStateResp.Parser)).Result);
        Assert.Equal(TeamMemberChatState.Close,
            (await Next(alice, TeamService.TeamMemberUpdate, TeamMemberChangeResp.Parser,
                m => m.MemberList.TryGetValue(b, out var member) && member.ChatState == TeamMemberChatState.Close)).MemberList[b].ChatState);

        await bob.SendGameAsync(TeamService.TeamEditor, new EditorTeamReq { IndexFirst = 0, IndexSecond = 1 });
        Assert.NotEqual(0, (await Next(bob, TeamService.TeamEditor, EditorTeamResp.Parser)).Result);
        await alice.SendGameAsync(TeamService.TeamEditor, new EditorTeamReq { IndexFirst = 0, IndexSecond = 1 });
        Assert.Equal(0, (await Next(alice, TeamService.TeamEditor, EditorTeamResp.Parser)).Result);
        var slots = await Next(bob, TeamService.TeamMemberIndexUpdate, TeamMemberIndexChangeRes.Parser);
        Assert.Equal(1, slots.MemberIndex[a]);
        Assert.Equal(0, slots.MemberIndex[b]);
    }

    [Fact]
    public async Task FiveMemberAdmissionsStayBoundedAndExpiredApplicationIsRefused()
    {
        var (alice, leader) = await InWorld("Alice");
        var (bob, _) = await InWorld("Bob");
        var (cora, _) = await InWorld("Cora");
        var (dave, _) = await InWorld("Dave");
        var (emma, emmaId) = await InWorld("Emma");
        var (finn, finnId) = await InWorld("Finn");
        await using var aLease = alice;
        await using var bLease = bob;
        await using var cLease = cora;
        await using var dLease = dave;
        await using var eLease = emma;
        await using var fLease = finn;

        await alice.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        long teamId = (await Next(alice, TeamService.TeamCreate, TeamCreateResp.Parser)).TeamId;
        await alice.SendGameAsync(TeamService.TeamChangeInfo, new ChangeTeamInfoReq { TargetId = 1001, IsAutoAgree = true });
        Assert.Equal(0, (await Next(alice, TeamService.TeamChangeInfo, ChangeTeamInfoResp.Parser)).Result);
        foreach (var bot in new[] { bob, cora, dave })
        {
            await bot.SendGameAsync(TeamService.TeamEnter, new EnterTeamReq { TeamId = teamId });
            Assert.Equal(0, (await Next(bot, TeamService.TeamEnter, EnterTeamResp.Parser)).Result);
        }

        // Micro directory serialization and the two final admissions contend on the same game loop.
        var summary = bob.MicroPositionalAsync("PartyService", "getPartysSummary", [0, Array.Empty<object>(), Array.Empty<object>(), Array.Empty<object>(), 0, 100]);
        var detail = cora.MicroPositionalAsync("PartyService", "getParty", [teamId.ToString()]);
        var counts = dave.MicroPositionalAsync("PartyService", "getSubPartysCountByType", [0, ""]);
        await Task.WhenAll(summary, detail, counts,
            emma.SendGameAsync(TeamService.TeamEnter, new EnterTeamReq { TeamId = teamId }),
            finn.SendGameAsync(TeamService.TeamEnter, new EnterTeamReq { TeamId = teamId }));
        Assert.True(Ok(await summary));
        Assert.True(Ok(await detail));
        Assert.True(Ok(await counts));
        var partyData = (Dictionary<object, object?>)(await detail)["Party"]!;
        Assert.True(((object?[])partyData["MemberList"]!).Length <= 5);
        int emmaCode = (await Next(emma, TeamService.TeamEnter, EnterTeamResp.Parser)).Result;
        int finnCode = (await Next(finn, TeamService.TeamEnter, EnterTeamResp.Parser)).Result;
        Assert.Equal([0, ErrorCodes.PartyFull], [.. new[] { emmaCode, finnCode }.OrderBy(x => x)]);
        var teams = _app.Services.GetRequiredService<TeamService>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        Assert.Equal(5, await loop.InvokeAsync(() => Task.FromResult(teams.MembersOf(leader).Count)));

        await alice.SendGameAsync(TeamService.TeamChangeType, new ChangeTeamTypeReq { TeamType = TeamType.Forty });
        Assert.Equal(0, (await Next(alice, TeamService.TeamChangeType, ChangeTeamTypeResp.Parser)).Result);
        await alice.SendGameAsync(TeamService.TeamChangeInfo, new ChangeTeamInfoReq { TargetId = 1001, IsAutoAgree = false });
        Assert.Equal(0, (await Next(alice, TeamService.TeamChangeInfo, ChangeTeamInfoResp.Parser)).Result);
        var applicant = emmaCode == 0 ? finn : emma;
        long applicantId = emmaCode == 0 ? finnId : emmaId;
        await applicant.SendGameAsync(TeamService.TeamEnter, new EnterTeamReq { TeamId = teamId });
        Assert.Equal(0, (await Next(applicant, TeamService.TeamEnter, EnterTeamResp.Parser)).Result);
        await Next(alice, TeamService.TeamBaseUpdate, TeamDetailInfo.Parser, p => p.ApplyerList.ContainsKey(applicantId));
        // Fast-forward the saved deadline without a 30-second wall-clock delay; the refusal is checked over the wire.
        await loop.InvokeAsync(() =>
        {
            var field = typeof(TeamService).GetField("_teams", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var roster = ((System.Collections.IDictionary)field.GetValue(teams)!)[teamId]!;
            var applicants = (Dictionary<long, long>)roster.GetType().GetField("Applicants")!.GetValue(roster)!;
            applicants[applicantId] = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds();
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(TeamService.TeamApplyList, new ApplyListOperateReq { OperType = ApplyListOperateType.Agree, TargetGuid = applicantId });
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, TeamService.TeamApplyList, ApplyListOperateResp.Parser)).Result);
        Assert.Equal(5, await loop.InvokeAsync(() => Task.FromResult(teams.MembersOf(leader).Count)));
    }

    [ClientDataFact]
    public async Task FriendMetadataGroupGiftAndRelog()
    {
        var (alice, a) = await InWorld("Alice");
        var (bob, b) = await InWorld("Bob");
        var (charlie, _) = await InWorld("Charlie");
        await using var outsider = charlie;
        await using var __ = bob;
        Assert.True(Ok(await alice.MicroAsync("FriendService", "apply", new() { ["Uid"] = a.ToString(), ["Target"] = b.ToString() })));
        Assert.True(Ok(await bob.MicroAsync("FriendService", "accept", new() { ["Uid"] = b.ToString(), ["Applicant"] = a.ToString() })));

        await alice.SendGameAsync(FriendExtraService.FetchMetadata, new FetchFriendSystemMetadataReq());
        var empty = await Next(alice, FriendExtraService.FetchMetadata, FetchFriendSystemMetadataResp.Parser);
        Assert.NotNull(empty.TopChats);
        Assert.NotNull(empty.FriendPartitions);

        var pins = new FriendChatRecordList();
        pins.Items.Add(new FriendChatListItem { Type = FriendChatType.Friend, RoleId = b });
        await alice.SendGameAsync(FriendExtraService.SetMetadata, new SetFriendSystemMetadataReq { TopChats = pins });
        var changed = await Next(alice, FriendExtraService.SetMetadata, SetFriendSystemMetadataResp.Parser);
        Assert.Equal(0, changed.Result);
        Assert.Equal(b, Assert.Single(changed.TopChats.Items).RoleId);

        await alice.SendGameAsync(FriendExtraService.CreateGroup, new CreateFriendChatGroupReq { GroupName = "Crew", InviteGuids = { b } });
        string group = (await Next(alice, FriendExtraService.CreateGroup, CreateFriendChatGroupResp.Parser)).Uid;
        Assert.NotEmpty(group);
        Assert.True((await Next(bob, FriendExtraService.GroupNotify, FriendChatGroupNotify.Parser)).IsSelf);
        var details = await bob.MicroAsync("PartyService", "getPartysByIDs", new() { ["PartyIDs"] = new[] { group } });
        Assert.True(Ok(details));
        var groupData = (Dictionary<object, object?>)((object?[])details["PartyList"]!).Single()!;
        Assert.Equal("Crew", ChatGroupData.Parser.ParseFrom((byte[])groupData["PartyData"]!).GroupName);
        Assert.Equal(2, ((object?[])groupData["MemberList"]!).Length);
        var denied = await charlie.MicroAsync("PartyService", "getPartysByIDs", new() { ["PartyIDs"] = new[] { group } });
        Assert.True(Ok(denied));
        Assert.Empty((object?[])denied["PartyList"]!);

        await alice.SendGameAsync(FriendExtraService.GiveGift, new GiveFriendGiftReq { RoleId = b, ItemId = 1004000, Count = 1 });
        Assert.Equal(ErrorCodes.ItemsShort, (await Next(alice, FriendExtraService.GiveGift, GiveFriendGiftResp.Parser)).Result);
        using (var http = new HttpClient())
            (await http.PostAsync($"{_http}/ro3emu/gm?account=alice", Operator.Command("!item 1004000 1"))).EnsureSuccessStatusCode();
        await alice.SendGameAsync(FriendExtraService.GiveGift, new GiveFriendGiftReq { RoleId = b, ItemId = 1004000, Count = 1, Message = "Hi" });
        var gift = await Next(alice, FriendExtraService.GiveGift, GiveFriendGiftResp.Parser);
        Assert.Equal(0, gift.Result);
        Assert.Equal(b, gift.FriendGiftRecord.Receiver);
        Assert.Equal(a, (await Next(bob, FriendExtraService.GiftNotify, FriendGiftRecord.Parser)).Sender);
        await alice.DisposeAsync();

        var (again, sameId) = await InWorld("Alice", create: false);
        await using var _ = again;
        Assert.Equal(a, sameId);
        await again.SendGameAsync(FriendExtraService.FetchMetadata, new FetchFriendSystemMetadataReq());
        var restored = await Next(again, FriendExtraService.FetchMetadata, FetchFriendSystemMetadataResp.Parser);
        Assert.Contains(group, restored.GroupIds);
        Assert.Equal(b, Assert.Single(restored.TopChats.Items).RoleId);
        Assert.Equal(b, Assert.Single(restored.SendGiftRecords).Receiver);
    }

    [Fact]
    public async Task RecommendationRequiresRealCandidatesInAllFourCategories()
    {
        var (alice, a) = await InWorld("Alice");
        var (bob, b) = await InWorld("Bob", sex: 2);
        await using var _ = alice;
        await using var __ = bob;
        await alice.SendGameAsync(FriendExtraService.Recommend, new FetchRecommendFriendsReq());
        Assert.NotEqual(0, (await Next(alice, FriendExtraService.Recommend, FetchRecommendFriendsResp.Parser)).Result);

        using (var http = new HttpClient())
        {
            (await http.PostAsync($"{_http}/ro3emu/gm?account=alice", Operator.Command("!lvl 15 1"))).EnsureSuccessStatusCode();
            (await http.PostAsync($"{_http}/ro3emu/gm?account=bob", Operator.Command("!lvl 15 1"))).EnsureSuccessStatusCode();
        }
        await alice.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        long team = (await Next(alice, TeamService.TeamCreate, TeamCreateResp.Parser)).TeamId;
        await alice.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = b });
        Assert.Equal(0, (await Next(alice, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
        await Next(bob, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
        await bob.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp { TeamId = team, IsAgree = true, InviteGuid = a });
        Assert.Equal(0, (await Next(bob, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);
        await alice.SendGameAsync(FriendExtraService.Recommend, new FetchRecommendFriendsReq());
        var result = await Next(alice, FriendExtraService.Recommend, FetchRecommendFriendsResp.Parser);
        Assert.Equal(0, result.Result);
        Assert.Contains(b, result.DiffSex);
        Assert.Contains(b, result.LevelRange);
        Assert.Contains(b, result.RecentTeam);
        Assert.Contains(b, result.SameJob);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
