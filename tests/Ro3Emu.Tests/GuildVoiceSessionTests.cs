using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Guild voice lifecycle and permission updates without proprietary client data.</summary>
public sealed class GuildVoiceSessionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);

    private static async Task<T> Ask<T>(BotClient bot, uint id, IMessage request, MessageParser<T> parser)
        where T : IMessage<T>
    {
        using var reply = bot.Inbox.Expect(e => e.IsGame(id));
        await bot.SendGameAsync(id, request);
        return parser.ParseFrom((await reply.WaitAsync(Wait)).Body);
    }

    private static async Task Seed(SyntheticHost host, BotClient alice, BotClient bob, bool bobManager = false)
    {
        var sessions = host.Service<SessionRegistry>();
        var a = sessions.All().Single(s => s.Role?.Id == (long)alice.Self!.Uuid);
        var b = sessions.All().Single(s => s.Role?.Id == (long)bob.Self!.Uuid);
        await host.Service<GameLoop>().InvokeAsync(() =>
        {
            var guild = new GuildStore.State { Name = "VoiceSessions", Status = (int)GuildStatus.Normal };
            guild.Members[a.Role!.Id] = new GuildStore.Member { RoleId = a.Role.Id, Name = "Alice", Official = 1 };
            guild.Members[b.Role!.Id] = new GuildStore.Member { RoleId = b.Role.Id, Name = "Bob", Official = 2 };
            guild.Rights[23] = bobManager ? [1, 2] : [1];
            Assert.True(host.Service<GuildStore>().Save(guild));
            a.Guild = new DBRoleGuildInfo { GuildId = guild.Id, Official = 1 };
            b.Guild = new DBRoleGuildInfo { GuildId = guild.Id, Official = 2 };
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task ReplacementLoginBetweenSweepsMustNotInheritOldSeat(bool sweepBeforeJoin, int channel)
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");
        await using var bob = await host.InWorldAsync("Bob");
        await Seed(host, alice, bob);
        var service = host.Service<GuildVoiceService>();
        var loop = host.Service<GameLoop>();
        // Hold just this ticker to deterministically represent a replacement between its 1-second sweeps.
        await loop.InvokeAsync(() => { loop.Remove(service); return Task.CompletedTask; });
        Assert.Equal(0, (await Ask(bob, GuildVoiceService.JoinMsg, new GuildVoiceChannelJoinReq(),
            GuildVoiceChannelJoinRsp.Parser)).ErrorCode);
        await using var replacement = await host.InWorldAsync("Bob", create: false);
        // Both the sweep and an earlier Join must recognize a new session with the same role and guild.
        if (sweepBeforeJoin)
        {
            await loop.InvokeAsync(() =>
            {
                service.Tick(new GameTime(TimeSpan.FromHours(1), TimeSpan.FromSeconds(1)));
                return Task.CompletedTask;
            });
            var swept = await Ask(alice, GuildVoiceService.AllMemberCountMsg, new GuildVoiceChannelGetAllMemberCountReq(),
                GuildVoiceChannelGetAllMemberCountRsp.Parser);
            Assert.Equal(0, swept.ChannelCounts.Sum(c => c.MemberCount));
        }
        var join = await Ask(replacement, GuildVoiceService.JoinMsg, new GuildVoiceChannelJoinReq { ChannelIndex = channel },
            GuildVoiceChannelJoinRsp.Parser);
        Assert.Equal(0, join.ErrorCode);
        Assert.Equal(channel, join.ChannelInfo.ChannelIndex);
        Assert.Single(join.ChannelInfo.Members);
        var counts = await Ask(alice, GuildVoiceService.AllMemberCountMsg, new GuildVoiceChannelGetAllMemberCountReq(),
            GuildVoiceChannelGetAllMemberCountRsp.Parser);
        Assert.Equal(1, counts.ChannelCounts.Sum(c => c.MemberCount));
        Assert.Equal(1, counts.ChannelCounts.Single(c => c.ChannelIndex == channel).MemberCount);
        Assert.Equal(ErrorCodes.GuildVoiceAlreadyInChannel, (await Ask(replacement, GuildVoiceService.JoinMsg,
            new GuildVoiceChannelJoinReq { ChannelIndex = channel }, GuildVoiceChannelJoinRsp.Parser)).ErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditingGuildVoiceRightMustRefreshTheChannelPermission(bool initiallyGranted)
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");
        await using var bob = await host.InWorldAsync("Bob");
        await Seed(host, alice, bob, bobManager: initiallyGranted);
        // This update must come from the committed rights edit, without waiting for a sweep.
        await host.Service<GameLoop>().InvokeAsync(() =>
        {
            host.Service<GameLoop>().Remove(host.Service<GuildVoiceService>());
            return Task.CompletedTask;
        });
        Assert.Equal(0, (await Ask(alice, GuildVoiceService.JoinMsg, new GuildVoiceChannelJoinReq(),
            GuildVoiceChannelJoinRsp.Parser)).ErrorCode);
        Assert.Equal(0, (await Ask(bob, GuildVoiceService.JoinMsg, new GuildVoiceChannelJoinReq(),
            GuildVoiceChannelJoinRsp.Parser)).ErrorCode);
        using var ordered = bob.Inbox.Expect(e => e.IsGame(GuildVoiceService.UpdateNotify)
            && GuildVoiceChannelUpdateNotify.Parser.ParseFrom(e.Body).ChannelInfo.SpeakMode == GuildSpeakAuthorityType.Order);
        Assert.Equal(0, (await Ask(alice, GuildVoiceService.SetSpeakModeMsg,
            new GuildVoiceChannelSetSpeakModeReq { SpeakMode = GuildSpeakAuthorityType.Order },
            GuildVoiceChannelSetSpeakModeRsp.Parser)).ErrorCode);
        var initial = GuildVoiceChannelUpdateNotify.Parser.ParseFrom((await ordered.WaitAsync(Wait)).Body);
        Assert.Equal(initiallyGranted, initial.ChannelInfo.Members.Single(m => m.PlayerId == (long)bob.Self!.Uuid).HasSpeakPermission);

        using var updated = bob.Inbox.Expect(e => e.IsGame(GuildVoiceService.UpdateNotify)
            && GuildVoiceChannelUpdateNotify.Parser.ParseFrom(e.Body).ChannelInfo.Members
                .Single(m => m.PlayerId == (long)bob.Self!.Uuid).HasSpeakPermission == !initiallyGranted);
        using var leaderUpdated = alice.Inbox.Expect(e => e.IsGame(GuildVoiceService.UpdateNotify)
            && GuildVoiceChannelUpdateNotify.Parser.ParseFrom(e.Body).ChannelInfo.Members
                .Single(m => m.PlayerId == (long)bob.Self!.Uuid).HasSpeakPermission == !initiallyGranted);
        var right = new OfficalRight { RightType = 23, AbledOfficial = { 1 } };
        if (!initiallyGranted) right.AbledOfficial.Add(2);
        var edited = await Ask(alice, GuildService.Edit,
            new GuildEditReq
            {
                Tp = (GuildEditReq.Types.EditType)4,
                Right = new GuildOfficalRight { Data = { right } },
            }, GuildEditResp.Parser);
        Assert.Equal(0, edited.Result);
        Assert.Equal(!initiallyGranted, host.Service<GuildService>().HasRight(host.Service<GuildStore>().ForRole((long)bob.Self!.Uuid)!,
            (long)bob.Self.Uuid, GuildVoiceService.VoiceRight));
        await updated.WaitAsync(Wait);
        await leaderUpdated.WaitAsync(Wait);
    }
}
