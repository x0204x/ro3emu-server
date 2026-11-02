using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>合成公會語音資料：批次邀請邊界、頻道人數上限及更替連線的席位保護。</summary>
public sealed class GuildVoiceInviteTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private const uint UnhandledInviteResponseNotify = 83717;

    // 更替登入失敗時一併輸出該合成伺服器的例外，避免只留下客戶端 EOF。
    private sealed class FailureLog : ILoggerProvider, ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Dispose() { }
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) Lines.Enqueue(formatter(state, exception) + Environment.NewLine + exception);
        }
    }

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

    private static Task<GuildVoiceChannelLeaveRsp> Leave(BotClient bot, int channel = 0) =>
        Ask(bot, GuildVoiceService.LeaveMsg, new GuildVoiceChannelLeaveReq { ChannelIndex = channel },
            GuildVoiceChannelLeaveRsp.Parser);

    private static Task<GuildVoiceChannelInviteRsp> Invite(BotClient bot, int channel, params long[] targets) =>
        Ask(bot, GuildVoiceService.InviteMsg,
            new GuildVoiceChannelInviteReq { ChannelIndex = channel, TargetPlayerIds = { targets } },
            GuildVoiceChannelInviteRsp.Parser);

    private static Task<GuildVoiceChannelInviteResponseRsp> Accept(BotClient bot, int channel, long inviter) =>
        Ask(bot, GuildVoiceService.InviteResponseMsg, new GuildVoiceChannelInviteResponseReq
        {
            ChannelIndex = channel, InviteUserId = inviter, IsAccept = true,
        }, GuildVoiceChannelInviteResponseRsp.Parser);

    private static Task<GuildVoiceGetNotInCdPlayerResp> Eligible(BotClient bot) =>
        Ask(bot, GuildVoiceService.NotInCooldownMsg, new GuildVoiceGetNotInCdPlayerReq(),
            GuildVoiceGetNotInCdPlayerResp.Parser);

    private static Task<GuildVoiceChannelGetAllMemberCountRsp> Counts(BotClient bot) =>
        Ask(bot, GuildVoiceService.AllMemberCountMsg, new GuildVoiceChannelGetAllMemberCountReq(),
            GuildVoiceChannelGetAllMemberCountRsp.Parser);

    [Fact]
    public async Task MixedBatchReportsEveryDistinctTargetAndUsesTheMillisecondCooldown()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero));
        await using var host = await SyntheticHost.StartAsync(clock: clock);
        await using var alice = await host.InWorldAsync("Alice");
        await using var bob = await host.InWorldAsync("Bob");
        await using var dave = await host.InWorldAsync("Dave");
        await using var eve = await host.InWorldAsync("Eve");
        await using var carol = await host.InWorldAsync("Carol");
        await Seed(host, alice, bob, dave, eve);
        long aliceId = (long)alice.Self!.Uuid;
        long bobId = (long)bob.Self!.Uuid;
        long daveId = (long)dave.Self!.Uuid;
        long eveId = (long)eve.Self!.Uuid;
        long carolId = (long)carol.Self!.Uuid;
        Assert.Equal(0, (await Seat(alice)).ErrorCode);
        Assert.Equal(0, (await Seat(dave, 1)).ErrorCode);
        await eve.SwitchRoleAsync();
        await eve.DisposeAsync();
        Assert.DoesNotContain(eveId, (await Eligible(alice)).UserIds);

        using var invited = bob.Inbox.Expect(e => e.IsGame(GuildVoiceService.InviteNotify));
        var response = await Invite(alice, 0, bobId, bobId, carolId, daveId, eveId);
        Assert.Equal(0, response.ErrorCode);
        Assert.Equal([(bobId, 0), (carolId, ErrorCodes.GuildVoiceTargetNotInGuild),
                (daveId, ErrorCodes.GuildVoiceTargetInChannel), (eveId, ErrorCodes.GuildVoiceTargetOffline)],
            response.PlayersResult.Select(p => (p.PlayerId, p.ErrorCode)));
        var notify = GuildVoiceChannelInviteNotify.Parser.ParseFrom((await invited.WaitAsync(Wait)).Body);
        Assert.Equal((0, aliceId, 15_000), (notify.ChannelIndex, notify.InviterPlayerId, notify.TimeoutSeconds));

        clock.Advance(TimeSpan.FromMilliseconds(14_999));
        var refused = await Invite(alice, 0, bobId, carolId);
        Assert.Equal(ErrorCodes.GuildVoiceInviteCooldown, refused.ErrorCode);
        Assert.Equal([(bobId, ErrorCodes.GuildVoiceInviteCooldown), (carolId, ErrorCodes.GuildVoiceTargetNotInGuild)],
            refused.PlayersResult.Select(p => (p.PlayerId, p.ErrorCode)));
        Assert.Single(bob.Inbox.Snapshot(), e => e.IsGame(GuildVoiceService.InviteNotify));

        clock.Advance(TimeSpan.FromMilliseconds(1));
        using var invitedAgain = bob.Inbox.Expect(e => e.IsGame(GuildVoiceService.InviteNotify));
        var retried = await Invite(alice, 0, bobId);
        Assert.Equal(0, retried.ErrorCode);
        Assert.Equal((bobId, 0), (Assert.Single(retried.PlayersResult).PlayerId, retried.PlayersResult[0].ErrorCode));
        await invitedAgain.WaitAsync(Wait);
    }

    [Fact]
    public async Task BatchLimitCountsDistinctTargetsAndDoesNotApplyCooldownToOmittedMembers()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");
        await using var bob = await host.InWorldAsync("Bob");
        await using var dave = await host.InWorldAsync("Dave");
        await Seed(host, alice, bob, dave);
        long bobId = (long)bob.Self!.Uuid;
        long daveId = (long)dave.Self!.Uuid;
        Assert.Equal(0, (await Seat(alice)).ErrorCode);
        var empty = await Invite(alice, 0);
        Assert.Equal(ErrorCodes.Parameter, empty.ErrorCode);
        Assert.Empty(empty.PlayersResult);

        // 99 個合成非會員各重複一次；Bob 是第 100 個不同目標，Dave 是第 101 個。
        long[] outsiders = [.. Enumerable.Range(1, 99).Select(i => 1_000_000L + i)];
        long[] targets = [.. outsiders.SelectMany(id => new[] { id, id }), bobId, bobId, daveId];
        using var bobInvited = bob.Inbox.Expect(e => e.IsGame(GuildVoiceService.InviteNotify));
        var response = await Invite(alice, 0, targets);
        Assert.Equal(0, response.ErrorCode);
        Assert.Equal(100, response.PlayersResult.Count);
        Assert.Equal(outsiders.Append(bobId), response.PlayersResult.Select(p => p.PlayerId));
        Assert.All(response.PlayersResult.Take(99), p => Assert.Equal(ErrorCodes.GuildVoiceTargetNotInGuild, p.ErrorCode));
        Assert.Equal(0, response.PlayersResult[^1].ErrorCode);
        await bobInvited.WaitAsync(Wait);
        Assert.Equal([daveId], (await Eligible(alice)).UserIds);
        Assert.DoesNotContain(dave.Inbox.Snapshot(), e => e.IsGame(GuildVoiceService.InviteNotify));

        using var daveInvited = dave.Inbox.Expect(e => e.IsGame(GuildVoiceService.InviteNotify));
        Assert.Equal(0, (await Invite(alice, 0, daveId)).ErrorCode);
        await daveInvited.WaitAsync(Wait);
    }

    [Fact]
    public async Task FullChannelRejectsInvitingAndAcceptingWithoutChangingEligibility()
    {
        await using var host = await SyntheticHost.StartAsync(o => o.Voice.MaxRoomSize = 1);
        await using var alice = await host.InWorldAsync("Alice");
        await using var bob = await host.InWorldAsync("Bob");
        await Seed(host, alice, bob);
        long aliceId = (long)alice.Self!.Uuid;
        long bobId = (long)bob.Self!.Uuid;
        Assert.Equal(0, (await Seat(alice)).ErrorCode);
        using var inviteNotify = bob.Inbox.Expect(e => e.IsGame(GuildVoiceService.InviteNotify));

        var invite = await Invite(alice, 0, bobId);
        Assert.Equal(ErrorCodes.Unknown, invite.ErrorCode);
        Assert.Empty(invite.PlayersResult);
        var accepted = await Accept(bob, 0, aliceId);
        Assert.Equal(ErrorCodes.Unknown, accepted.ErrorCode);
        Assert.Null(accepted.ChannelInfo);
        var eligible = await Eligible(alice);
        Assert.Equal(0, eligible.ErrorCode);
        Assert.Equal([bobId], eligible.UserIds);
        Assert.Equal(1, (await Counts(alice)).ChannelCounts.Single(c => c.ChannelIndex == 0).MemberCount);
        await Assert.ThrowsAsync<TimeoutException>(() => inviteNotify.WaitAsync(TimeSpan.FromMilliseconds(300)));
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(UnhandledInviteResponseNotify));
    }

    [Fact]
    public async Task AcceptanceRechecksCapacityAndCanRetryAfterASeatBecomesFree()
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

        var response = await Accept(bob, 0, aliceId);
        Assert.Equal(ErrorCodes.Unknown, response.ErrorCode);
        Assert.Null(response.ChannelInfo);
        Assert.Equal(2, (await Counts(alice)).ChannelCounts.Single(c => c.ChannelIndex == 0).MemberCount);

        Assert.Equal(0, (await Leave(dave)).ErrorCode);
        // 接受邀請使用共同入席判定；滿員拒絕後不會消耗自訂 pending token。
        var retry = await Accept(bob, 0, aliceId);
        Assert.Equal(0, retry.ErrorCode);
        Assert.Equal([aliceId, bobId], retry.ChannelInfo.Members.Select(m => m.PlayerId));
        Assert.Equal(ErrorCodes.GuildVoiceAlreadyInChannel, (await Accept(bob, 0, aliceId)).ErrorCode);
        Assert.Equal(2, (await Counts(alice)).ChannelCounts.Single(c => c.ChannelIndex == 0).MemberCount);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(UnhandledInviteResponseNotify));
    }

    [Fact]
    public async Task ReplacementSessionCanAcceptWithoutAnInviteTokenAndIgnoresStaleCleanup()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero));
        await using var host = await SyntheticHost.StartAsync(clock: clock);
        var failures = new FailureLog();
        host.Service<ILoggerFactory>().AddProvider(failures);
        await using var alice = await host.InWorldAsync("Alice");
        await using var bob = await host.InWorldAsync("Bob");
        long guildId = await Seed(host, alice, bob);
        long aliceId = (long)alice.Self!.Uuid;
        long bobId = (long)bob.Self!.Uuid;
        Assert.Equal(0, (await Seat(alice)).ErrorCode);
        Assert.Equal(0, (await Seat(bob, 1)).ErrorCode);
        GameSession staleCleanup = null!;
        await host.Service<GameLoop>().InvokeAsync(() =>
        {
            var original = host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == bobId);
            // 保留舊角色身分的合成清理呼叫，驗證比對連線擁有者，而非只比對角色 ID。
            staleCleanup = new GameSession(original.Channel, host.Service<TrafficLog>()) { Role = original.Role };
            return Task.CompletedTask;
        });

        BotClient replacementClient;
        try { replacementClient = await host.InWorldAsync("Bob", create: false); }
        catch (Exception error)
        {
            throw new InvalidOperationException("更替角色登入失敗。伺服器警告／例外：" + Environment.NewLine
                + string.Join(Environment.NewLine, failures.Lines), error);
        }
        await using var replacement = replacementClient;
        Assert.False(host.Service<GuildVoiceService>().IsSeated(bobId, guildId, 1));
        using (var invited = replacement.Inbox.Expect(e => e.IsGame(GuildVoiceService.InviteNotify)))
        {
            Assert.Equal(0, (await Invite(alice, 0, bobId)).ErrorCode);
            await invited.WaitAsync(Wait);
        }
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(0, (await Accept(replacement, 0, aliceId)).ErrorCode);
        await host.Service<GameLoop>().InvokeAsync(() => host.Service<GuildVoiceService>().LeaveRoleAsync(staleCleanup));
        Assert.True(host.Service<GuildVoiceService>().IsSeated(bobId, guildId, 0));
        var counts = await Counts(alice);
        Assert.Equal(2, counts.ChannelCounts.Single(c => c.ChannelIndex == 0).MemberCount);
        Assert.Equal(0, counts.ChannelCounts.Single(c => c.ChannelIndex == 1).MemberCount);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(UnhandledInviteResponseNotify));
    }
}
