using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task BuffCastConditionUsesGroupsActorExpiryAndUnambiguousStackBounds()
    {
        await using var bot = await InWorld("BuffCondition");
        var source = Session("buffcondition"); var combat = _app.Services.GetRequiredService<CombatService>();
        long future = MonsterService.NowMs + 60_000;
        ActiveBuff Buff(int id, int count = 1, long owner = 123, bool expired = false) =>
            new(owner, id, expired ? 1 : future, 60_000, [], owner, PileCount: count);
        Assert.True(combat.EvaluateBuffCastCondition(source, [1004, 15006], [Buff(1500610)]));
        Assert.False(combat.EvaluateBuffCastCondition(source, [1004, 1500601], [Buff(1500601)]));
        Assert.False(combat.EvaluateBuffCastCondition(source, [1004, 15006], [Buff(1500601, expired: true)]));
        Assert.Null(combat.EvaluateBuffCastCondition(source, [1004, 15006], null));
        Assert.True(combat.EvaluateBuffCastCondition(source, [2000, 4110301, 3, 2], [Buff(4110301, 2)]));
        Assert.True(combat.EvaluateBuffCastCondition(source, [2000, 4110301, 3, 2], [Buff(4110301, 3)]));
        Assert.False(combat.EvaluateBuffCastCondition(source, [2000, 4110301, 3, 2], [Buff(4110301, 4)]));
        Assert.False(combat.EvaluateBuffCastCondition(source, [2000, 4110301, 3, 2], [Buff(4110302, 2)]));
        Assert.False(combat.EvaluateBuffCastCondition(source, [2000, 4110301, 3, 2], [Buff(4110301, 2, expired: true)]));
        Assert.Null(combat.EvaluateBuffCastCondition(source, [2000, 4110301, 3, 2], [Buff(4110301, 2), Buff(4110301, 4, 456)]));
        Assert.True(combat.EvaluateBuffCastCondition(source, [2000, 4110301, 3, 2], [Buff(4110301, 2), Buff(4110301, 3, 456)]));
        Assert.Null(combat.EvaluateBuffCastCondition(source, [2000, 4110301, 3, 2], null));
        await Loop.InvokeAsync(async () =>
        {
            // 角色身上的Buff不等於控制actor的Buff；傳入空actor集合仍應false。
            var buffs = _app.Services.GetRequiredService<BuffService>();
            await buffs.AddAsync(source, 1500601, 60_000, 456);
            Assert.True(combat.EvaluateBuffCastCondition(source, [1004, 15006], source.Buffs));
            Assert.False(combat.EvaluateBuffCastCondition(source, [1004, 15006], []));
            await buffs.ClearOnDeathAsync(source);
            Assert.False(combat.EvaluateBuffCastCondition(source, [1004, 15006], source.Buffs));
        });
    }

    [ClientDataFact]
    public async Task CardDarkInstantRequiresActualConcealmentGroupWithoutPermanentlyLearning()
    {
        await using var bot = await InWorld("DarkCondition");
        await Gm("darkcondition", "!lvl 80"); await Gm("darkcondition", "!item 1450401 1");
        var source = Session("darkcondition");
        await Loop.InvokeAsync(() => { EnsureCardFixtureGear(source, 4); return Task.CompletedTask; });
        await bot.SendGameAsync(CardService.SlotInsert, new EquipmentCardInsertReq
        { EquipPos = (EquipGridType)4, SlotIndex = 1, CardGuid = source.Items.Single(i => i.Entry == 1450401).Guid });
        Assert.Equal(0, (await Next(bot, CardService.SlotInsert, EquipmentCardInsertResp.Parser)).Result);
        Assert.Equal(1, source.Progress!.GrantedSkills.GetValueOrDefault(20045));
        Assert.DoesNotContain(20045, source.Progress.Skills.Keys);
        var target = await Target("darkcondition", stationary: true, immobile: true); await CardOffenceDummyAsync(target);
        var request = new CastSkillReq { SkillId = 2004501, TargetId = target.Uid, TargetPos = source.Position.Clone() };
        await bot.SendGameAsync(CombatService.UseSkill, request);
        Assert.Equal(ErrorCodes.SkillRequirements, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.False(source.SkillCds.ContainsKey(20045));
        // 同張卡授予偽裝，使用真施放建立15006群，不GM永久學會黑暗瞬間。
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2004001 });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            m => m.UnitId == source.Role!.Id && m.Buffs.Any(b => b.ConfigId == 1500601));
        await Task.Delay(700);
        await bot.SendGameAsync(CombatService.UseSkill, request);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            m => m.SkillId == 2004501 && m.Dsts.Any(d => d.DstID == target.Uid && d.Damage > 0));
        Assert.DoesNotContain(20045, source.Progress.Skills.Keys);
    }

    [ClientDataFact]
    public async Task NotRidingConditionChangesOnlyWithBattleMountRidingState()
    {
        var fixture = await BombingFixtureAsync(1); await using var bot = fixture.Bot;
        var source = fixture.Source; var combat = _app.Services.GetRequiredService<CombatService>();
        Assert.False(combat.EvaluateBuffCastCondition(source, [2004, 1500300], null));
        await bot.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = (eRiderType)2 });
        Assert.Equal(0, (await Next(bot, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        Assert.Equal(0, CombatService.CurrentBattleMount(source));
        Assert.True(combat.EvaluateBuffCastCondition(source, [2004, 1500300], null));
    }
}
