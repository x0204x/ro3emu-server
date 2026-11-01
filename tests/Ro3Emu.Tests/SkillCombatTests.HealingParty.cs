using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private async Task JoinHealingParty(BotClient leader, BotClient member)
    {
        await leader.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        var created = await Next(leader, TeamService.TeamCreate, TeamCreateResp.Parser);
        Assert.Equal(0, created.Result);
        await leader.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = (long)member.Self!.Uuid });
        Assert.Equal(0, (await Next(leader, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
        await Next(member, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
        await member.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp
        { TeamId = created.TeamId, IsAgree = true, InviteGuid = (long)leader.Self!.Uuid });
        Assert.Equal(0, (await Next(member, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task HealingHarmonyUsesCurrentPartyAndDropsBonusImmediatelyAfterLeaving()
    {
        await using var leader = await InWorld("HarmonySource");
        await using var member = await InWorld("HarmonyMember");
        await using var stranger = await InWorld("HarmonyOther");
        IsolateDirectHealing();
        await HealingStat(leader, 104, 1000);
        await HealingStat(member, 101, 10000);
        await HealingStat(stranger, 101, 10000);
        await JoinHealingParty(leader, member);
        var source = Session("harmonysource"); var target = Session("harmonymember"); var other = Session("harmonyother");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var row = _app.Services.GetRequiredService<GameData>().Row("SkillConfig", 11430301)!.Value;
        await Loop.InvokeAsync(async () =>
        {
            await Character.SetHpSpAsync(target, 1, null);
            await Character.SetHpSpAsync(other, 1, null);
            await combat.CastExtraOnAlliesAsync(source, row, [target, other]);
        });
        var hit = await Next(leader, CombatService.SkillHit, SkillHitInfo.Parser, n => n.SkillId == 11430301);
        Assert.Equal(600, Assert.Single(hit.Dsts, d => d.DstID == target.Role!.Id).Damage);
        Assert.Equal(300, Assert.Single(hit.Dsts, d => d.DstID == other.Role!.Id).Damage);
        Assert.Equal(601, target.CurrentHp); Assert.Equal(301, other.CurrentHp);
        await member.SendGameAsync(TeamService.TeamLeave, new LeaveTeamReq());
        Assert.Equal(0, (await Next(member, TeamService.TeamLeave, LeaveTeamResp.Parser)).Result);
        await Loop.InvokeAsync(() => combat.CastExtraOnAlliesAsync(source, row, [target]));
        var after = await Next(leader, CombatService.SkillHit, SkillHitInfo.Parser, n => n.SkillId == 11430301 && n.SessionId != hit.SessionId);
        Assert.Equal(300, Assert.Single(after.Dsts).Damage);
        Assert.Equal(901, target.CurrentHp);
    }

    [ClientDataFact]
    public async Task HealingColuseoPrioritizesThePartyMemberOverNearerStrangers()
    {
        await using var leader = await InWorld("PartyHealSrc");
        await using var member = await InWorld("PartyHealMate");
        await using var other1 = await InWorld("PartyHealOne");
        await using var other2 = await InWorld("PartyHealTwo");
        await using var other3 = await InWorld("PartyHealThree");
        await using var other4 = await InWorld("PartyHealFour");
        IsolateDirectHealing();
        await Gm("partyhealsrc", "!job 4400");
        await Teach("partyhealsrc", 14303);
        await HealingStat(leader, 102, 10000);
        await JoinHealingParty(leader, member);
        var source = Session("partyhealsrc"); var mate = Session("partyhealmate");
        await Loop.InvokeAsync(async () =>
        {
            mate.Position = source.Position.Clone(); mate.Position.X += 6;
            await Character.SetHpSpAsync(source, 1, 10000);
            foreach (var name in new[] { "partyhealmate", "partyhealone", "partyhealtwo", "partyhealthree", "partyhealfour" })
            {
                var session = Session(name);
                if (session != mate) session.Position = source.Position.Clone();
                await Character.SetHpSpAsync(session, 1, null);
            }
        });
        await leader.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1430301 });
        var accepted = await Next(leader, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(leader, CombatService.SkillHit, SkillHitInfo.Parser, n => n.SessionId == accepted.SessionId);
        Assert.Equal(5, hit.Dsts.Count);
        Assert.Contains(hit.Dsts, d => d.DstID == mate.Role!.Id && d.Damage > 0);
        Assert.True(mate.CurrentHp > 1);
    }
}
