using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>The 2 s party refresh: a member's card is sent when it changes and not again while it stays the same,
/// also for a member who went offline (whose role row is read once, not every refresh).</summary>
public sealed class TeamRefreshTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait)).Body);

    [Fact]
    public async Task AnOfflineMembersCardIsSentOnceThenLeftAlone()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");
        var bob = await host.InWorldAsync("Bob");
        long b = (long)bob.Self!.Uuid;

        await alice.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        var created = await Next(alice, TeamService.TeamCreate, TeamCreateResp.Parser);
        Assert.Equal(0, created.Result);
        await alice.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = b });
        Assert.Equal(0, (await Next(alice, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
        await Next(bob, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
        await bob.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp
        { TeamId = created.TeamId, IsAgree = true, InviteGuid = (long)alice.Self!.Uuid });
        Assert.Equal(0, (await Next(bob, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);

        await bob.DisposeAsync();
        static bool OfflineBob(TeamMemberChangeResp u, long b) => u.MemberList.TryGetValue(b, out var card)
            && card.OnlineState == TeamMemberOnlineState.Offline;
        var update = await Next(alice, TeamService.TeamMemberUpdate, TeamMemberChangeResp.Parser, u => OfflineBob(u, b));
        Assert.Equal("Bob", update.MemberList[b].Name);

        // Two more refreshes: Bob's card hasn't changed, so it isn't sent again.
        int Cards() => alice.Inbox.Snapshot().Count(e => e.IsGame(TeamService.TeamMemberUpdate)
            && TeamMemberChangeResp.Parser.ParseFrom(e.Body).MemberList.ContainsKey(b));
        int before = Cards();
        await Task.Delay(TimeSpan.FromSeconds(4.5));
        Assert.Equal(before, Cards());
    }
}
