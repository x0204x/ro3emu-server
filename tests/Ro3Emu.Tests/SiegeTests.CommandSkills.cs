using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>The commander's skills in a running siege: the panel's charges, buffs and revive for its own side, and the
/// Emergency Call.</summary>
public sealed partial class SiegeTests
{
    [ClientDataFact]
    public async Task TheCommanderBuffsRevivesAndCallsItsOwnSide()
    {
        await using var attack = await Bot("SiegeGm"); // the fixture's GM account seeds the siege
        await using var ally = await Bot("SiegeSkAlly");
        await using var defense = await Bot("SiegeSkDef");
        long ga = await Guild(attack, "SgSkA"), gd = await Guild(defense, "SgSkD");
        await OnLoop(() => Service<CharacterService>().SetLevelAsync(Session(ally), 99, null));
        await attack.SendGameAsync(GuildService.Edit, new GuildEditReq { Tp = (GuildEditReq.Types.EditType)6, AutoAcceptApply = true });
        Assert.Equal(0, (await Next(attack, GuildService.Edit, GuildEditResp.Parser)).Result);
        await ally.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { ga } });
        Assert.Equal(0, Assert.Single((await Next(ally, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
        await Next(ally, GuildService.Full, GuildFullInfoNtf.Parser, g => g.Guid == ga);
        await attack.JoinChatAsync("4_1");
        _clock.Set(_siege.Start + 25);
        await attack.SendChatAsync("4_1", 2, $"!siege seed 1001 {ga} {gd}", channelIndex: 4);
        await Next(attack, GmCommands.BroadcastNotify, BroadCastNotify.Parser);
        await Base(attack, currentCity: 1001);
        await Enter(attack, 1); await Enter(ally, 1); await Enter(defense, 2);

        // Loading into the battle sends the panel: every skill ready, Blessing of Light with two charges.
        var panel = await Next(attack, TerritoryWarsService.CommandSkillNotify, DungeonCommandSkillNotify.Parser);
        Assert.Equal([4909001, 4909101, 4909201, 4909301], panel.SkillInfos.Keys.Order());
        Assert.Equal(2, panel.SkillInfos[TerritoryWarsService.BlessingOfLight].MaxUseTimes);
        Assert.All(panel.SkillInfos.Values, i => Assert.Equal(0, i.UseTimes));

        // Only guild right 12 casts them; Battle Command buffs the commander's side.
        await Cast(ally, TerritoryWarsService.BattleCommand);
        Assert.Equal(ErrorCodes.NoPermission, (await Next(ally, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Cast(attack, TerritoryWarsService.BattleCommand);
        Assert.Equal(0, (await Next(attack, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var used = await Next(ally, TerritoryWarsService.CommandSkillNotify, DungeonCommandSkillNotify.Parser,
            n => n.SkillInfos[TerritoryWarsService.BattleCommand].UseTimes == 1);
        Assert.True(used.SkillInfos[TerritoryWarsService.BattleCommand].SkillCdEnd > used.SkillInfos[TerritoryWarsService.BattleCommand].SkillCdStart);
        Assert.Equal(used.CommonCdStartMs + 5000, used.CommonCdEndMs);
        long allyId = Session(ally).Role!.Id;
        await Next(ally, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            n => n.UnitId == allyId && n.Buffs.Any(b => b.ConfigId == TerritoryWarsService.BattleCommand));
        await Cast(attack, TerritoryWarsService.BattleCommand);
        Assert.Equal(ErrorCodes.SkillCooldown, (await Next(attack, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.DoesNotContain(defense.Inbox.Snapshot(), e => e.IsGame(TerritoryWarsService.CommandSkillNotify)
            && DungeonCommandSkillNotify.Parser.ParseFrom(e.Body).SkillInfos.Values.Any(i => i.UseTimes > 0));

        // Defend the Glory revives the fallen ally where it lies, with 70% HP.
        await ClearCommonCd(attack, ga);
        await OnLoop(() => { Session(ally).Dead = true; return Task.CompletedTask; });
        await Cast(attack, TerritoryWarsService.DefendTheGlory);
        Assert.Equal(0, (await Next(attack, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(ally, 30112, SyncClientPlayerReviveInfoNtf.Parser, n => !n.BDead);
        Assert.False(Session(ally).Dead);
        long maxHp = Service<CharacterService>().Stats(Session(ally))[101];
        Assert.Equal(maxHp * 7000 / 10000, Session(ally).CurrentHp);

        // Emergency Call: the side is invited, an ally far away accepts and lands at the commander.
        await ClearCommonCd(attack, ga);
        var origin = Session(attack).Position.Clone();
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(ally), new MapPoint(origin.X + 30, origin.Y, origin.Z, 0)));
        await Cast(attack, TerritoryWarsService.EmergencyCall);
        Assert.Equal(0, (await Next(attack, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var invite = await Next(ally, TerritoryWarsService.TeleportInvite, DungeonTeleportInviteNotify.Parser);
        Assert.Equal(Session(attack).Role!.Id, invite.InviteGuid);
        await ally.SendGameAsync(TerritoryWarsService.TeleportResponse, new DungeonTeleportResponseReq { IsAgree = true });
        Assert.Equal(0, (await Next(ally, TerritoryWarsService.TeleportResponse, DungeonTeleportResponseRsp.Parser)).Result);
        await Next(attack, TerritoryWarsService.TeleportCount, DungeonTeleportAcceptCountNotify.Parser, n => n.AcceptCount == 1 && n.MaxCount == 20);
        Assert.True(MonsterService.Distance(Session(ally).Position.X, Session(ally).Position.Z, origin.X, origin.Z) < 1);
        // A second answer finds the invitation used.
        await ally.SendGameAsync(TerritoryWarsService.TeleportResponse, new DungeonTeleportResponseReq { IsAgree = true });
        Assert.Equal(ErrorCodes.TeleportExpired, (await Next(ally, TerritoryWarsService.TeleportResponse, DungeonTeleportResponseRsp.Parser)).Result);
        Assert.DoesNotContain(defense.Inbox.Snapshot(), e => e.IsGame(TerritoryWarsService.TeleportInvite));
    }

    private async Task Cast(BotClient bot, int skill) => await bot.SendGameAsync(CombatService.UseSkill,
        new CastSkillReq { SkillId = skill, TargetPos = Session(bot).Position.Clone() });

    /// <summary>Skips the 5 s common cooldown the panel shares (MonsterService.NowMs is the wall clock).</summary>
    private Task ClearCommonCd(BotClient commander, long guild) => OnLoop(() =>
    {
        Session(commander).SiegeBattle!.Command.Skills[guild].CommonCdEnd = 0;
        return Task.CompletedTask;
    });
}
