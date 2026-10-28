using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Guild voice invitations on the synthetic fixture: routing, eligibility, time windows and admission.</summary>
public sealed class GuildVoiceInviteTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan elapsed) => Now += elapsed;
    }

    private static async Task<T> Ask<T>(BotClient bot, uint id, IMessage request, MessageParser<T> parser)
        where T : IMessage<T>
    {
        using var reply = bot.Inbox.Expect(e => e.IsGame(id));
        await bot.SendGameAsync(id, request);
        return parser.ParseFrom((await reply.WaitAsync(Wait)).Body);
    }

    private static async Task<long> Seed(SyntheticHost host, params BotClient[] members)
    {
        long guildId = 0;
        await host.Service<GameLoop>().InvokeAsync(() =>
        {
            var sessions = host.Service<SessionRegistry>();
            var memberSessions = members.Select(member =>
                sessions.All().Single(s => s.Role?.Id == (long)member.Self!.Uuid)).ToArray();
            var guild = new GuildStore.State { Name = "VoiceInvites", Status = (int)GuildStatus.Normal };
            for (int i = 0; i < memberSessions.Length; i++)
            {
                var session = memberSessions[i];
                int official = i + 1;
                guild.Members[session.Role!.Id] = new GuildStore.Member
                {
                    RoleId = session.Role.Id, Name = session.Role.Name, Official = official,
                };
            }
            guild.Rights[GuildVoiceService.VoiceRight] = [1];
            Assert.True(host.Service<GuildStore>().Save(guild));
            guildId = guild.Id;
            for (int i = 0; i < memberSessions.Length; i++)
                memberSessions[i].Guild = new DBRoleGuildInfo { GuildId = guild.Id, Official = i + 1 };
            return Task.CompletedTask;
        });
        return guildId;
    }

    private static Task<GuildVoiceChannelJoinRsp> Seat(BotClient bot, int channel = 0) =>
        Ask(bot, GuildVoiceService.JoinMsg, new GuildVoiceChannelJoinReq { ChannelIndex = channel },
            GuildVoiceChannelJoinRsp.Parser);

    private static Task<GuildVoiceChannelInviteRsp> Invite(BotClient bot, int channel, params long[] targets) =>
        Ask(bot, GuildVoiceService.InviteMsg,
            new GuildVoiceChannelInviteReq { ChannelIndex = channel, TargetPlayerIds = { targets } },
            GuildVoiceChannelInviteRsp.Parser);

    private static Task<GuildVoiceChannelInviteResponseRsp> Respond(BotClient bot, int channel, long inviter,
        bool accept, bool rejectAll = false) =>
        Ask(bot, GuildVoiceService.InviteResponseMsg, new GuildVoiceChannelInviteResponseReq
        {
            ChannelIndex = channel, InviteUserId = inviter, IsAccept = accept, RejectAll = rejectAll,
        }, GuildVoiceChannelInviteResponseRsp.Parser);

    private static Task<GuildVoiceGetNotInCdPlayerResp> Eligible(BotClient bot) =>
        Ask(bot, GuildVoiceService.NotInCooldownMsg, new GuildVoiceGetNotInCdPlayerReq(),
            GuildVoiceGetNotInCdPlayerResp.Parser);

    [Fact]
    public async Task InviteReturnsPerTargetFailuresAndCreatesOneSessionBoundPendingAction()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");
        await using var bob = await host.InWorldAsync("Bob");
        await using var dave = await host.InWorldAsync("Dave");
        var eve = await host.InWorldAsync("Eve");
        await using var carol = await host.InWorldAsync("Carol");
        await Seed(host, alice, bob, dave, eve);
        long aliceId = (long)alice.Self!.Uuid;
        long bobId = (long)bob.Self!.Uuid;
        long daveId = (long)dave.Self!.Uuid;
        long eveId = (long)eve.Self!.Uuid;
        long carolId = (long)carol.Self!.Uuid;
        Assert.Equal(0, (await Seat(alice)).ErrorCode);
        Assert.Equal(0, (await Seat(dave, 1)).ErrorCode);
        await eve.DisposeAsync();

        using var invited = bob.Inbox.Expect(e => e.IsGame(GuildVoiceService.InviteNotify));
        var response = await Invite(alice, 0, bobId, carolId, daveId, eveId);
        Assert.Equal(0, response.ErrorCode);
        var failures = response.PlayersResult.ToDictionary(x => x.PlayerId, x => x.ErrorCode);
        Assert.Equal(ErrorCodes.GuildVoiceTargetNotInGuild, failures[carolId]);
        Assert.Equal(ErrorCodes.GuildVoiceTargetAlreadyInChannel, failures[daveId]);
        Assert.Equal(ErrorCodes.GuildVoiceTargetOffline, failures[eveId]);
        var notify = GuildVoiceChannelInviteNotify.Parser.ParseFrom((await invited.WaitAsync(Wait)).Body);
        Assert.Equal((0, aliceId, 30), (notify.ChannelIndex, notify.InviterPlayerId, notify.TimeoutSeconds));

        response = await Invite(alice, 0, bobId);
        Assert.Equal(0, response.ErrorCode);
        Assert.Equal(ErrorCodes.GuildVoiceInvitePending, Assert.Single(response.PlayersResult).ErrorCode);

        await using var replacement = await host.InWorldAsync("Bob", create: false);
        Assert.Equal(ErrorCodes.Parameter, (await Respond(replacement, 0, aliceId, accept: true)).ErrorCode);
    }

    [Fact]
    public async Task ResponseAcceptsAValidInviteAndRejectsItAtThirtySeconds()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero));
        await using var host = await SyntheticHost.StartAsync(clock: clock);
        await using var alice = await host.InWorldAsync("Alice");
        await using var bob = await host.InWorldAsync("Bob");
        await Seed(host, alice, bob);
        long aliceId = (long)alice.Self!.Uuid;
        long bobId = (long)bob.Self!.Uuid;
        Assert.Equal(0, (await Seat(alice)).ErrorCode);

        using (var invited = bob.Inbox.Expect(e => e.IsGame(GuildVoiceService.InviteNotify)))
        {
            Assert.Equal(0, (await Invite(alice, 0, bobId)).ErrorCode);
            await invited.WaitAsync(Wait);
        }
        using var answered = alice.Inbox.Expect(e => e.IsGame(GuildVoiceService.InviteResponseNotify));
        using var updated = alice.Inbox.Expect(e => e.IsGame(GuildVoiceService.UpdateNotify)
            && GuildVoiceChannelUpdateNotify.Parser.ParseFrom(e.Body).ChannelInfo.Members.Any(m => m.PlayerId == bobId));
        var accepted = await Respond(bob, 0, aliceId, accept: true);
        Assert.Equal(0, accepted.ErrorCode);
        Assert.Equal([aliceId, bobId], accepted.ChannelInfo.Members.Select(m => m.PlayerId));
        var answer = GuildVoiceChannelInviteResponseNotify.Parser.ParseFrom((await answered.WaitAsync(Wait)).Body);
        Assert.Equal((0, bobId, true), (answer.ChannelIndex, answer.TargetPlayerId, answer.IsAccept));
        await updated.WaitAsync(Wait);

        Assert.Equal(0, (await Ask(bob, GuildVoiceService.LeaveMsg,
            new GuildVoiceChannelLeaveReq { ChannelIndex = 0 }, GuildVoiceChannelLeaveRsp.Parser)).ErrorCode);
        clock.Advance(TimeSpan.FromSeconds(15));
        using (var invited = bob.Inbox.Expect(e => e.IsGame(GuildVoiceService.InviteNotify)))
        {
            Assert.Equal(0, (await Invite(alice, 0, bobId)).ErrorCode);
            await invited.WaitAsync(Wait);
        }
        clock.Advance(TimeSpan.FromSeconds(30));
        var expired = await Respond(bob, 0, aliceId, accept: true);
        Assert.Equal(ErrorCodes.Parameter, expired.ErrorCode);
        Assert.Null(expired.ChannelInfo);
    }

    [Fact]
    public async Task CooldownRejectAllAndEligibilityUseTheRecoveredMillisecondWindows()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero));
        await using var host = await SyntheticHost.StartAsync(clock: clock);
        await using var alice = await host.InWorldAsync("Alice");
        await using var bob = await host.InWorldAsync("Bob");
        await Seed(host, alice, bob);
        long aliceId = (long)alice.Self!.Uuid;
        long bobId = (long)bob.Self!.Uuid;
        Assert.Equal(0, (await Seat(alice)).ErrorCode);
        Assert.Equal([bobId], (await Eligible(alice)).UserIds);

        using (var invited = bob.Inbox.Expect(e => e.IsGame(GuildVoiceService.InviteNotify)))
        {
            Assert.Equal(0, (await Invite(alice, 0, bobId)).ErrorCode);
            await invited.WaitAsync(Wait);
        }
        Assert.Empty((await Eligible(alice)).UserIds); // pending
        Assert.Equal(0, (await Respond(bob, 0, aliceId, accept: false)).ErrorCode);
        Assert.Equal(ErrorCodes.GuildVoiceInviteCooldown,
            Assert.Single((await Invite(alice, 0, bobId)).PlayersResult).ErrorCode);

        clock.Advance(TimeSpan.FromMilliseconds(15_000));
        using (var invited = bob.Inbox.Expect(e => e.IsGame(GuildVoiceService.InviteNotify)))
        {
            Assert.Equal(0, (await Invite(alice, 0, bobId)).ErrorCode);
            await invited.WaitAsync(Wait);
        }
        Assert.Equal(0, (await Respond(bob, 0, aliceId, accept: false, rejectAll: true)).ErrorCode);
        clock.Advance(TimeSpan.FromMilliseconds(15_000));
        Assert.Equal(ErrorCodes.GuildVoiceInviteDeclined,
            Assert.Single((await Invite(alice, 0, bobId)).PlayersResult).ErrorCode);
        Assert.Empty((await Eligible(alice)).UserIds);

        clock.Advance(TimeSpan.FromMilliseconds(585_000));
        Assert.Equal([bobId], (await Eligible(alice)).UserIds);
    }

    [Fact]
    public async Task FullChannelFailsClosedWithoutCreatingAnInviteOrNotifications()
    {
        await using var host = await SyntheticHost.StartAsync(o => o.Voice.MaxRoomSize = 1);
        await using var alice = await host.InWorldAsync("Alice");
        await using var bob = await host.InWorldAsync("Bob");
        await Seed(host, alice, bob);
        long aliceId = (long)alice.Self!.Uuid;
        long bobId = (long)bob.Self!.Uuid;
        Assert.Equal(0, (await Seat(alice)).ErrorCode);
        using var inviteNotify = bob.Inbox.Expect(e => e.IsGame(GuildVoiceService.InviteNotify));
        using var responseNotify = alice.Inbox.Expect(e => e.IsGame(GuildVoiceService.InviteResponseNotify));

        var invite = await Invite(alice, 0, bobId);
        Assert.Equal(ErrorCodes.Unknown, invite.ErrorCode);
        Assert.Empty(invite.PlayersResult);
        await Assert.ThrowsAsync<TimeoutException>(() => inviteNotify.WaitAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Equal(ErrorCodes.Parameter, (await Respond(bob, 0, aliceId, accept: true)).ErrorCode);
        await Assert.ThrowsAsync<TimeoutException>(() => responseNotify.WaitAsync(TimeSpan.FromMilliseconds(300)));
        var eligible = await Eligible(alice);
        Assert.Equal(ErrorCodes.Unknown, eligible.ErrorCode);
        Assert.Empty(eligible.UserIds);
    }

    [Fact]
    public async Task AcceptanceRechecksCapacityAndDoesNotNotifyOnFailure()
    {
        await using var host = await SyntheticHost.StartAsync(o => o.Voice.MaxRoomSize = 2);
        await using var alice = await host.InWorldAsync("Alice");
        await using var bob = await host.InWorldAsync("Bob");
        await using var dave = await host.InWorldAsync("Dave");
        await Seed(host, alice, bob, dave);
        long aliceId = (long)alice.Self!.Uuid;
        long bobId = (long)bob.Self!.Uuid;
        Assert.Equal(0, (await Seat(alice)).ErrorCode);
        using (var invited = bob.Inbox.Expect(e => e.IsGame(GuildVoiceService.InviteNotify)))
        {
            Assert.Equal(0, (await Invite(alice, 0, bobId)).ErrorCode);
            await invited.WaitAsync(Wait);
        }
        Assert.Equal(0, (await Seat(dave)).ErrorCode);
        using var responseNotify = alice.Inbox.Expect(e => e.IsGame(GuildVoiceService.InviteResponseNotify));

        var response = await Respond(bob, 0, aliceId, accept: true);
        Assert.Equal(ErrorCodes.Unknown, response.ErrorCode);
        Assert.Null(response.ChannelInfo);
        await Assert.ThrowsAsync<TimeoutException>(() => responseNotify.WaitAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Equal(ErrorCodes.Parameter, (await Respond(bob, 0, aliceId, accept: true)).ErrorCode);
        var counts = await Ask(alice, GuildVoiceService.AllMemberCountMsg,
            new GuildVoiceChannelGetAllMemberCountReq(), GuildVoiceChannelGetAllMemberCountRsp.Parser);
        Assert.Equal(2, counts.ChannelCounts.Single(c => c.ChannelIndex == 0).MemberCount);
    }
}
