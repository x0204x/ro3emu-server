using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Held social gift receipt and participant admission through the real game and GM routes.</summary>
public sealed class SocialD13dTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3emu-social-d13d-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], options =>
        {
            options.DataDir = _directory;
            options.GameDataDir = Path.Combine(RepoRoot(), "data");
            options.HttpPort = httpPort;
            options.MicroGatePort = microPort;
            options.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task<(BotClient Bot, long Role)> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long role = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(role);
        // SelfInfoEndFrame precedes the route's final Realm-owned scene/guild publication.
        await Next(bot, GuildService.SceneGuildId, SwitchSceneSyncGuildId.Parser);
        return (bot, role);
    }

    private static async Task<T> Next<T>(BotClient bot, uint message, Google.Protobuf.MessageParser<T> parser)
        where T : Google.Protobuf.IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(packet => packet.IsGame(message), Wait)).Body);

    private async Task GmItemAsync(string account, int item, int count)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={account}",
            new StringContent($"!item {item} {count}"))).EnsureSuccessStatusCode();
    }

    private long Scalar(string sql)
    {
        var path = Service<AccountStore>().DatabasePath;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public async Task HeldTeamJoinKeepsRosterUnpublishedUntilTeammateCommit()
    {
        var (alice, a) = await InWorld("TeamAlice");
        var (bob, b) = await InWorld("TeamBob");
        var (charlie, _) = await InWorld("TeamCharlie");
        await using var aliceLease = alice;
        await using var bobLease = bob;
        await using var charlieLease = charlie;
        await alice.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        long teamId = (await Next(alice, TeamService.TeamCreate, TeamCreateResp.Parser)).TeamId;
        Assert.True(teamId > 0);
        await alice.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = b });
        Assert.Equal(0, (await Next(alice, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
        await Next(bob, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);

        var accounts = Service<AccountStore>();
        var loop = Service<GameLoop>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gmDeferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        accounts.BeforeOwnedCommitForTest = label =>
        {
            if (label != "friend/RecordTeammates") return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };
        Task? recipientGm = null;
        try
        {
            await bob.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp { TeamId = teamId, IsAgree = true });
            await entered.Task.WaitAsync(Wait);
            var teams = Service<TeamService>();
            var before = await loop.InvokeAsync(() => Task.FromResult((teams.TeamOf(b), Members: teams.MembersOf(a).ToArray())));
            Assert.Equal(0, before.Item1.TeamId);
            Assert.Equal(new[] { a }, before.Members);
            Assert.Equal(0, Scalar("SELECT COUNT(*) FROM recent_team_peer"));

            await charlie.SendGameAsync(FriendExtraService.SetMetadata,
                new SetFriendSystemMetadataReq { TopChats = new FriendChatRecordList() });
            Assert.Equal(0, (await Next(charlie, FriendExtraService.SetMetadata,
                SetFriendSystemMetadataResp.Parser)).Result);
            await loop.InvokeAsync(() =>
            {
                loop.Owners!.DeferredForTest = origin => { if (origin == "GM") gmDeferred.TrySetResult(); };
                return Task.CompletedTask;
            });
            recipientGm = GmItemAsync("teambob", 1004000, 1);
            await gmDeferred.Task.WaitAsync(Wait);
            Assert.False(recipientGm.IsCompleted);
            Assert.Equal(0, Scalar("SELECT COUNT(*) FROM recent_team_peer"));

            release.TrySetResult();
            Assert.Equal(0, (await Next(bob, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);
            Assert.Equal(2, (await Next(bob, TeamService.TeamAll, TeamTotalDetailInfo.Parser)).MemberList.Count);
            await recipientGm.WaitAsync(Wait);
            var after = await loop.InvokeAsync(() => Task.FromResult((teams.TeamOf(b), Members: teams.MembersOf(a).ToArray())));
            Assert.Equal(teamId, after.Item1.TeamId);
            Assert.Equal(new[] { a, b }, after.Members);
            Assert.Contains(b, Service<FriendStore>().RecentTeammates(a));
            Assert.Contains(a, Service<FriendStore>().RecentTeammates(b));
            Assert.Equal(2, Scalar("SELECT COUNT(*) FROM recent_team_peer"));
        }
        finally
        {
            release.TrySetResult();
            accounts.BeforeOwnedCommitForTest = null;
            await loop.InvokeAsync(() =>
            {
                loop.Owners!.DeferredForTest = null;
                return Task.CompletedTask;
            });
            if (recipientGm != null) await recipientGm.WaitAsync(Wait);
        }
    }

    [Fact]
    public async Task GroupCreateLostAcknowledgementReplaysCompleteChatGroupReceipt()
    {
        var (alice, a) = await InWorld("GroupAlice");
        var (bob, b) = await InWorld("GroupBob");
        await using var aliceLease = alice;
        await using var bobLease = bob;
        await alice.MicroAsync("FriendService", "apply", new() { ["Uid"] = a.ToString(), ["Target"] = b.ToString() });
        await bob.MicroAsync("FriendService", "accept", new() { ["Uid"] = b.ToString(), ["Applicant"] = a.ToString() });

        var accounts = Service<AccountStore>();
        int faulted = 0;
        accounts.AfterWriterCommitForTest = label =>
        {
            if (label == "friend/CreateGroup" && Interlocked.Exchange(ref faulted, 1) == 0)
                throw new IOException("Test-only lost group acknowledgement after COMMIT.");
        };
        try
        {
            await alice.SendGameAsync(FriendExtraService.CreateGroup,
                new CreateFriendChatGroupReq { GroupName = "Crew", InviteGuids = { b } });
            var response = await Next(alice, FriendExtraService.CreateGroup, CreateFriendChatGroupResp.Parser);
            Assert.Equal(0, response.Result);
            Assert.NotEmpty(response.Uid);
            Assert.Equal(1, Volatile.Read(ref faulted));
            var group = Service<FriendStore>().Group(response.Uid);
            Assert.NotNull(group);
            Assert.Equal(a, group.Owner);
            Assert.Equal("Crew", group.Name);
            Assert.Equal(new[] { a, b }.Order(), group.Members.Select(member => member.Role).Order());
            Assert.Equal(1, Scalar("SELECT COUNT(*) FROM friend_chat_group"));
            Assert.Equal(1, Scalar("SELECT COUNT(*) FROM owner_operation_receipt WHERE family='friend/CreateGroup'"));
        }
        finally { accounts.AfterWriterCommitForTest = null; }
    }

    [ClientDataFact]
    public async Task HeldGiftKeepsRecipientGmBehindReceiptAndPreservesIndependentSocialRole()
    {
        var (alice, a) = await InWorld("GiftAlice");
        var (bob, b) = await InWorld("GiftBob");
        var (charlie, _) = await InWorld("GiftCharlie");
        await using var aliceLease = alice;
        await using var bobLease = bob;
        await using var charlieLease = charlie;
        await alice.MicroAsync("FriendService", "apply", new() { ["Uid"] = a.ToString(), ["Target"] = b.ToString() });
        await bob.MicroAsync("FriendService", "accept", new() { ["Uid"] = b.ToString(), ["Applicant"] = a.ToString() });
        await GmItemAsync("giftalice", 1004000, 1);

        var accounts = Service<AccountStore>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        accounts.BeforeOwnedApplyForTest = label =>
        {
            if (label != "economy/friend-gift") return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };
        Task? recipientGm = null;
        try
        {
            await alice.SendGameAsync(FriendExtraService.GiveGift,
                new GiveFriendGiftReq { RoleId = b, ItemId = 1004000, Count = 1, Message = "Hi" });
            await entered.Task.WaitAsync(Wait);
            recipientGm = GmItemAsync("giftbob", 1004000, 1);
            await charlie.SendGameAsync(FriendExtraService.SetMetadata,
                new SetFriendSystemMetadataReq { TopChats = new FriendChatRecordList() });
            Assert.Equal(0, (await Next(charlie, FriendExtraService.SetMetadata,
                SetFriendSystemMetadataResp.Parser)).Result);
            Assert.False(recipientGm.IsCompleted);
            Assert.Equal(1, Scalar("SELECT COUNT(*) FROM operation_receipt WHERE command_kind='friend-gift'"));
            release.TrySetResult();
            Assert.Equal(0, (await Next(alice, FriendExtraService.GiveGift, GiveFriendGiftResp.Parser)).Result);
            Assert.Equal(a, (await Next(bob, FriendExtraService.GiftNotify, FriendGiftRecord.Parser)).Sender);
            await recipientGm.WaitAsync(Wait);
        }
        finally
        {
            release.TrySetResult();
            accounts.BeforeOwnedApplyForTest = null;
            if (recipientGm != null) await recipientGm.WaitAsync(Wait);
        }

        var friends = Service<FriendStore>();
        Assert.Equal(1, friends.Intimacy(a, b));
        Assert.Equal(1, friends.Intimacy(b, a));
        Assert.Single(friends.LoadMetadata(a).SendGiftRecords);
        Assert.Single(friends.LoadMetadata(b).ReceiveGiftRecords);
        Assert.Equal(1, Scalar($"SELECT COUNT(*) FROM economy_entry WHERE asset_id=1004000 AND delta=-1 AND reason_code={(int)EconomyReason.FriendGiftCost}"));
    }

    [Fact]
    public async Task ShortGiftLeavesNoRelationRecordOrEconomicReceipt()
    {
        var (alice, a) = await InWorld("ShortAlice");
        var (bob, b) = await InWorld("ShortBob");
        await using var aliceLease = alice;
        await using var bobLease = bob;
        await alice.MicroAsync("FriendService", "apply", new() { ["Uid"] = a.ToString(), ["Target"] = b.ToString() });
        await bob.MicroAsync("FriendService", "accept", new() { ["Uid"] = b.ToString(), ["Applicant"] = a.ToString() });
        await alice.SendGameAsync(FriendExtraService.GiveGift,
            new GiveFriendGiftReq { RoleId = b, ItemId = 1004000, Count = 1 });
        Assert.Equal(ErrorCodes.ItemsShort, (await Next(alice, FriendExtraService.GiveGift, GiveFriendGiftResp.Parser)).Result);
        var friends = Service<FriendStore>();
        Assert.Empty(friends.LoadMetadata(a).SendGiftRecords);
        Assert.Empty(friends.LoadMetadata(b).ReceiveGiftRecords);
        Assert.Equal(0, friends.Intimacy(a, b));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM operation_receipt WHERE command_kind='friend-gift'"));
    }

    private static string RepoRoot()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path != null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "Ro3Emu.slnx"))) return path.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
