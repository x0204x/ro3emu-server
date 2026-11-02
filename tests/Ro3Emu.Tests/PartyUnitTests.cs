using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// A party seen on the units: EntityBase.teamId (31049 TeamInfo) makes members the same group for the client, and a
/// member's death reaches the others' party frames through 31024 and 31049 (LC_Scene_Sync, LC_DAL_Team).
/// </summary>
public sealed class PartyUnitTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private static async Task<long> PartyOfTwoAsync(BotClient leader, BotClient member, long memberId)
    {
        await leader.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        long teamId = (await Next(leader, TeamService.TeamCreate, TeamCreateResp.Parser)).TeamId;
        await leader.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = memberId });
        var invite = await Next(member, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
        await member.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp { TeamId = teamId, IsAgree = true, InviteGuid = invite.InviteGuid });
        Assert.Equal(0, (await Next(member, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);
        return teamId;
    }

    private static bool TeamInfo(EntityBase e, long role) =>
        e.NotifyType == EntityBase.Types.NotifyType.TeamInfo && e.Uuid == (ulong)role;

    [Fact]
    public async Task JoiningAndLeavingPutThePartyIdOnTheUnitForEveryoneInView()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");
        await using var bob = await host.InWorldAsync("Bob");
        long aliceId = (long)alice.Self!.Uuid, bobId = (long)bob.Self!.Uuid;

        long teamId = await PartyOfTwoAsync(alice, bob, bobId);
        Assert.Equal(teamId, (await Next(bob, CombatService.EntityBaseChange, EntityBase.Parser, e => TeamInfo(e, bobId))).TeamId);
        Assert.Equal(teamId, (await Next(alice, CombatService.EntityBaseChange, EntityBase.Parser, e => TeamInfo(e, bobId))).TeamId);
        Assert.Equal(teamId, (await Next(bob, CombatService.EntityBaseChange, EntityBase.Parser, e => TeamInfo(e, aliceId))).TeamId);

        await bob.SendGameAsync(TeamService.TeamLeave, new LeaveTeamReq());
        Assert.Equal(0, (await Next(alice, CombatService.EntityBaseChange, EntityBase.Parser, e => TeamInfo(e, bobId))).TeamId);
    }

    [Fact]
    public async Task ADeathSendsTheLifeStatusBeforeTheBaseChangeThatCarriesIt()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");
        await using var bob = await host.InWorldAsync("Bob");
        long bobId = (long)bob.Self!.Uuid;
        await PartyOfTwoAsync(alice, bob, bobId);

        var session = host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == bobId);
        await host.Service<GameLoop>().InvokeAsync(() => host.Service<CombatService>().KillPlayerAsync(session));
        var dead = await Next(alice, CombatService.EntityBaseChange, EntityBase.Parser,
            e => e.Uuid == (ulong)bobId && e.NotifyType == EntityBase.Types.NotifyType.Base);
        Assert.Equal(EPlayerLifeStatus.Dead, dead.PlayerLifeStatus);
        var order = alice.Inbox.Snapshot().Where(e => e.IsGame(WorldCoreService.LifeStatus) || e.IsGame(CombatService.EntityBaseChange)
            && EntityBase.Parser.ParseFrom(e.Body) is { NotifyType: EntityBase.Types.NotifyType.Base } b && b.Uuid == (ulong)bobId).ToList();
        int status = order.FindIndex(e => e.IsGame(WorldCoreService.LifeStatus)
            && PlayerLifeStatusNotify.Parser.ParseFrom(e.Body) is { PlayerLifeStatus: EPlayerLifeStatus.Dead } n && n.EntityId == bobId);
        int change = order.FindIndex(e => e.IsGame(CombatService.EntityBaseChange));
        Assert.InRange(status, 0, change - 1);
    }
}
