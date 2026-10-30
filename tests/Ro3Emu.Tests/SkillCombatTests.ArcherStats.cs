using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private const int ArcherBow = 2110120;
    private const int DoubleStrafe = 1300301;
    private const int FireArrow = 1002012;

    private sealed class StableArcherDamageRolls : CombatRolls
    {
        public override double Hit() => 0;
        public override double DamageFloat() => 0.5;
        public override double Crit() => 1;
    }

    [ClientDataFact]
    public async Task ArcherDexRaisesAttackAndDoubleStrafeDamage()
    {
        await using var archer = await InWorld("Archer");
        await PrepareArcherAsync(archer, "archer");
        await Loop.InvokeAsync(() =>
        {
            _app.Services.GetRequiredService<CombatService>().Rolls = new StableArcherDamageRolls();
            return Task.CompletedTask;
        });
        var lowStats = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("archer"))));
        var lowTarget = await Target("archer", stationary: true, npcId: 5000007);
        var (lowBullet, lowHit) = await CastDoubleStrafeAsync(archer, lowTarget);
        await Task.Delay(1000);

        var request = new AllocateAttrPointReq { PlanIndex = 0 };
        foreach (int stat in Enumerable.Range(1, 6)) request.Points[stat] = stat == 5 ? 10 : 1;
        await archer.SendGameAsync(CharacterService.AllocateAttrPoints, request);
        Assert.Equal(0, (await Next(archer, CharacterService.AllocateAttrPoints, AllocateAttrPointRsp.Parser)).Result);
        var changed = await Next(archer, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            m => m.Property.Propertys.Any(p => p.PropertyId == 5)
                && m.AttrModTypes.Contains((int)EAttrModType.Base));
        Assert.Equal([(int)EAttrModType.Base], changed.AttrModTypes);

        var highStats = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("archer"))));
        Assert.Equal(10, Session("archer").Progress!.Stats[4]);
        Assert.Equal(lowStats[5] + 9, highStats[5]);
        Assert.Equal(lowStats[109] + 9, highStats[109]);
        Assert.Equal(lowStats[1031] + 180, highStats[1031]);
        Assert.Equal(lowStats[116] + 18, highStats[116]);
        Assert.True(highStats[103] > lowStats[103], $"DEX did not raise Archer ATK: {lowStats[103]} -> {highStats[103]}");

        var highTarget = await Target("archer", stationary: true, npcId: 5000007);
        var (highBullet, highHit) = await CastDoubleStrafeAsync(archer, highTarget);
        Assert.Equal(1300304, lowBullet.EffectId); // the G client uses this EffectData for the two flying arrows
        Assert.Equal(1300304, highBullet.EffectId);
        Assert.Equal(2, lowBullet.Split);
        Assert.Equal(1300305, lowHit.ShowPoint); // the following ActionDamage row owns the target hit effect
        Assert.Equal(1300305, highHit.ShowPoint);
        Assert.Equal(2, lowHit.Split);
        Assert.Equal(4, lowHit.SrcElement);
        Assert.True(Assert.Single(highHit.Dsts).Damage > Assert.Single(lowHit.Dsts).Damage,
            $"DEX did not raise Double Strafe damage: {lowHit.Dsts[0].Damage} -> {highHit.Dsts[0].Damage}");
    }

    private async Task PrepareArcherAsync(BotClient bot, string account)
    {
        await Gm(account, "!job 3100");
        await Teach(account, 13003);
        await Gm(account, $"!item {ArcherBow}");
        var granted = await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.GetValueOrDefault(2)?.Inc?.Items.Values.Any(i => i.All?.ItemEntry == ArcherBow) == true);
        long bow = granted.PackInfo[2].Inc.Items.Values.Single(i => i.All?.ItemEntry == ArcherBow).All.ItemGuid;
        await bot.SendGameAsync(CharacterService.EquipItemOpr,
            new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = bow });
        Assert.Equal(0, (await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);

        // An archer picks any ArrowsItemId arrow without owning it: the client lists them with count -1 (#198).
        await bot.SendGameAsync(CharacterService.ArrowSwitch, new ItemArrowSwitchReq { ItemId = FireArrow });
        var arrow = await Next(bot, CharacterService.ArrowSwitch, ItemArrowSwitchResp.Parser);
        Assert.Equal(0, arrow.Result);
        Assert.Equal(-1, arrow.ItemCount);
    }

    private static async Task<(SkillFixTimeBulletNotify Bullet, SkillHitInfo Hit)> CastDoubleStrafeAsync(
        BotClient bot, MonsterUnit target)
    {
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = DoubleStrafe, TargetId = target.Uid });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var timeline = await Next(bot, CombatService.UseInSkill, UseInSkillInfo.Parser,
            m => m.SessionId == accepted.SessionId);
        var bullet = await Next(bot, CombatService.SkillBullet, SkillFixTimeBulletNotify.Parser,
            m => m.SkillConfigId == DoubleStrafe && m.TargetId == target.Uid);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, m => m.SessionId == accepted.SessionId);
        var packets = bot.Inbox.Snapshot().ToList();
        int timelineIndex = packets.FindIndex(e => e.IsGame(CombatService.UseInSkill)
            && UseInSkillInfo.Parser.ParseFrom(e.Body).SessionId == timeline.SessionId);
        int bulletIndex = packets.FindIndex(e => e.IsGame(CombatService.SkillBullet)
            && SkillFixTimeBulletNotify.Parser.ParseFrom(e.Body).TargetId == target.Uid);
        int hitIndex = packets.FindIndex(e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SessionId == accepted.SessionId);
        Assert.True(timelineIndex >= 0 && timelineIndex < bulletIndex && bulletIndex < hitIndex,
            $"Double Strafe packet order was {timelineIndex}, {bulletIndex}, {hitIndex}");
        return (bullet, hit);
    }
}
