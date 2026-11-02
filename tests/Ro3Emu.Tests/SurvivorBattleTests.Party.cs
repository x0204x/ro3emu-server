using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SurvivorBattleTests
{
    /// <summary>
    /// Party mode (UI_Sys_ChickenContentWidgetBaseCtrl._startMatch): only the leader starts, the members' searches start
    /// with it, and the party is one team of the match whoever else queued around it.
    /// </summary>
    [ClientDataFact]
    public async Task APartyQueuesFromItsLeaderAndFightsAsOneTeam()
    {
        await HostAsync(4, 2);
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var cara = await InWorld("Cara");
        await using var dan = await InWorld("Dan");
        await alice.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        long teamId = (await Next(alice, TeamService.TeamCreate, TeamCreateResp.Parser)).TeamId;
        await alice.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = Id(cara) });
        var invite = await Next(cara, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
        await cara.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp { TeamId = teamId, IsAgree = true, InviteGuid = invite.InviteGuid });
        Assert.Equal(0, (await Next(cara, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);

        await cara.SendGameAsync(TimedBattleService.StartMatch, new StartMatchReq { MatchEntryId = 2 });
        Assert.Equal(ErrorCodes.NoPermission, (await Next(cara, TimedBattleService.StartMatch, StartMatchResp.Parser)).Result);

        async Task StartAsync(BotClient player)
        {
            await player.SendGameAsync(TimedBattleService.StartMatch, new StartMatchReq { MatchEntryId = 2 });
            Assert.Equal(0, (await Next(player, TimedBattleService.StartMatch, StartMatchResp.Parser)).Result);
        }
        await StartAsync(bob);
        await StartAsync(alice);
        var caraSearch = await Next(cara, TimedBattleService.StartMatch, StartMatchResp.Parser, r => r.Result == 0);
        Assert.Equal(2, caraSearch.MatchEntryId);
        Assert.NotEqual(0, caraSearch.MatchStartTime);
        await StartAsync(dan);

        var players = new[] { alice, bob, cara, dan };
        foreach (var player in players)
        {
            var check = await Next(player, TimedBattleService.ReadyNotify, MatchReadyNotify.Parser);
            Assert.Equal(_clock.GetUtcNow().ToUnixTimeSeconds() + 30, check.EndTime); // seconds, as the prepare panel counts
            await player.SendGameAsync(TimedBattleService.Ready, new MatchReadyReq { MatchEntryId = 2, PlayerId = Id(player), IsReady = true });
            Assert.Equal(0, (await Next(player, TimedBattleService.Ready, MatchReadyResp.Parser)).Result);
        }
        foreach (var player in players)
            await Next(player, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, s => s.SceneEntry == 2240101);
        int Faction(BotClient bot) => Session(bot).EventFaction;
        Assert.Equal(Faction(alice), Faction(cara));
        Assert.Equal(Faction(bob), Faction(dan));
        Assert.NotEqual(Faction(alice), Faction(bob));
    }
}
