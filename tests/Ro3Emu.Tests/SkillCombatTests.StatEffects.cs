using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private sealed class FixedStatRolls : CombatRolls
    {
        public double CritValue { get; set; } = 1;
        public override double Hit() => 0;
        public override double DamageFloat() => 0.5;
        public override double Crit() => CritValue;
    }

    [ClientDataFact]
    public async Task EveryAllocatedStatChangesItsDerivedAndCombatValues()
    {
        var combat = _app.Services.GetRequiredService<CombatService>();
        var template = _app.Services.GetRequiredService<MonsterService>().Template(5000007)!;
        int[][] properties =
        [
            [103, 105, 1031],
            [110, 115],
            [101, 107, 1011, 1071],
            [102, 104, 106, 108, 1021, 1041, 1081],
            [109, 116],
            [109, 110, 111],
        ];

        for (int stat = 1; stat <= 6; stat++) await Verify(stat);

        async Task Verify(int stat)
        {
            string account = "stat" + stat;
            await using var bot = await InWorld(account);
            await Gm(account, "!lvl 10");
            var low = await Snapshot(account);
            await AllocateStatAsync(bot, account, stat);
            var high = await Snapshot(account);

            Assert.Equal(low.Stats[stat] + 9, high.Stats[stat]);
            foreach (int property in properties[stat - 1])
                Assert.True(high.Stats[property] > low.Stats[property],
                    $"Stat {stat} did not raise property {property}: {low.Stats[property]} -> {high.Stats[property]}");

            if (stat == 1)
            {
                Assert.True(high.Duel.PlayerDps > low.Duel.PlayerDps);
                Assert.True(high.Duel.MonsterDps < low.Duel.MonsterDps);
            }
            else if (stat == 2) Assert.True(high.Duel.PlayerDps > low.Duel.PlayerDps);
            else if (stat == 3)
            {
                Assert.True(high.Duel.PlayerHp > low.Duel.PlayerHp);
                Assert.True(high.Regen.Hp > low.Regen.Hp);
            }
            else if (stat == 4) Assert.True(high.Regen.Sp > low.Regen.Sp);
            else if (stat == 6) Assert.True(high.Duel.PlayerDps > low.Duel.PlayerDps);
        }

        Task<(StatSheet Stats, (double PlayerDps, double MonsterDps, long PlayerHp) Duel, (long Hp, long Sp) Regen)> Snapshot(
            string account) => Loop.InvokeAsync(() =>
        {
            var session = Session(account);
            var stats = Character.Stats(session);
            return Task.FromResult((stats, combat.Duel(session, template), CombatService.Regeneration(stats)));
        });
    }

    [ClientDataFact]
    public async Task StrengthRaisesRealBashDamage()
    {
        await using var swordman = await InWorld("Strength");
        await Gm("strength", "!lvl 10");
        await Gm("strength", "!job 1100");
        await Teach("strength", 11001);
        await UseFixedRollsAsync();

        var low = await CastDamageAsync(swordman, await DurableTarget("strength"), 1100101);
        await Task.Delay(1100);
        await AllocateStatAsync(swordman, "strength", 1);
        var high = await CastDamageAsync(swordman, await DurableTarget("strength"), 1100101);

        Assert.True(high.Damage > low.Damage, $"STR did not raise Bash damage: {low.Damage} -> {high.Damage}");
    }

    [ClientDataFact]
    public async Task AgilityShortensTheRealNormalAttackTimeline()
    {
        await using var novice = await InWorld("Agility");
        await Gm("agility", "!lvl 10");
        var lowTarget = await DurableTarget("agility");
        int low = await StartAndInterruptAsync(novice, 1000001, lowTarget.Uid);
        await AllocateStatAsync(novice, "agility", 2);
        var highTarget = await DurableTarget("agility");
        int high = await StartAndInterruptAsync(novice, 1000001, highTarget.Uid);

        Assert.True(high < low, $"AGI did not shorten the normal-attack timeline: {low} -> {high}");
    }

    [ClientDataFact]
    public async Task IntelligenceRaisesRealFireBoltDamage()
    {
        await using var mage = await InWorld("Intelligence");
        await Gm("intelligence", "!lvl 10");
        await Gm("intelligence", "!job 2100");
        await Teach("intelligence", 12004);
        await UseFixedRollsAsync();

        var low = await CastDamageAsync(mage, await DurableTarget("intelligence"), 1200401);
        await Task.Delay(600);
        await AllocateStatAsync(mage, "intelligence", 4);
        var high = await CastDamageAsync(mage, await DurableTarget("intelligence"), 1200401);

        Assert.True(high.Damage > low.Damage, $"INT did not raise Fire Bolt damage: {low.Damage} -> {high.Damage}");
    }

    [ClientDataFact]
    public async Task DexterityShortensTheRealFireBoltChant()
    {
        await using var mage = await InWorld("Dexterity");
        await Gm("dexterity", "!lvl 10");
        await Gm("dexterity", "!job 2100");
        await Teach("dexterity", 12004);

        int low = await StartAndInterruptAsync(mage, 1200401, (await DurableTarget("dexterity")).Uid, chant: true);
        await Task.Delay(600);
        await AllocateStatAsync(mage, "dexterity", 5);
        int high = await StartAndInterruptAsync(mage, 1200401, (await DurableTarget("dexterity")).Uid, chant: true);

        Assert.True(high < low, $"DEX did not shorten Fire Bolt chant: {low} -> {high}");
    }

    [ClientDataFact]
    public async Task LuckChangesARealBashFromNormalToCriticalAtTheThreshold()
    {
        await using var swordman = await InWorld("Luck");
        await Gm("luck", "!lvl 10");
        await Gm("luck", "!job 1100");
        await Teach("luck", 11001);
        var rolls = new FixedStatRolls();
        await Loop.InvokeAsync(() =>
        {
            rolls.CritValue = (Character.Stats(Session("luck"))[111] + 0.5) / 10000.0;
            _app.Services.GetRequiredService<CombatService>().Rolls = rolls;
            return Task.CompletedTask;
        });

        var low = await CastDamageAsync(swordman, await DurableTarget("luck"), 1100101);
        Assert.Equal(DamageHitResult.Normal, low.Result);
        await Task.Delay(1100);
        await AllocateStatAsync(swordman, "luck", 6);
        var high = await CastDamageAsync(swordman, await DurableTarget("luck"), 1100101);

        Assert.Equal(DamageHitResult.Critical, high.Result);
        Assert.True(high.Damage > low.Damage);
    }

    private async Task AllocateStatAsync(BotClient bot, string account, int stat)
    {
        var request = new AllocateAttrPointReq { PlanIndex = 0 };
        foreach (int id in Enumerable.Range(1, 6)) request.Points[id] = id == stat ? 10 : 1;
        await bot.SendGameAsync(CharacterService.AllocateAttrPoints, request);
        Assert.Equal(0, (await Next(bot, CharacterService.AllocateAttrPoints, AllocateAttrPointRsp.Parser)).Result);
        var changed = await Next(bot, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            m => m.Property.Propertys.Any(p => p.PropertyId == stat && p.Data == 10)
                && m.AttrModTypes.Contains((int)EAttrModType.Base));
        Assert.Equal([(int)EAttrModType.Base], changed.AttrModTypes);
        Assert.Equal(10, Session(account).Progress!.Stats[stat - 1]);
    }

    private Task UseFixedRollsAsync() => Loop.InvokeAsync(() =>
    {
        _app.Services.GetRequiredService<CombatService>().Rolls = new FixedStatRolls();
        return Task.CompletedTask;
    });

    private async Task<MonsterUnit> DurableTarget(string account)
    {
        var target = await Target(account, stationary: true, npcId: 5000007);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        return target;
    }

    private async Task<(long Damage, DamageHitResult Result)> CastDamageAsync(BotClient bot, MonsterUnit target, int skill)
    {
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target.Uid });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, m => m.SessionId == accepted.SessionId);
        var damage = Assert.Single(hit.Dsts, d => d.DstID == target.Uid);
        return (damage.Damage, damage.HitResult);
    }

    private async Task<int> StartAndInterruptAsync(BotClient bot, int skill, long target, bool chant = false)
    {
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var timeline = await Next(bot, CombatService.UseInSkill, UseInSkillInfo.Parser, m => m.SessionId == accepted.SessionId);
        await bot.SendGameAsync(CombatService.SkillInterrupt, new SkillInterruptReq { InterruptSkillId = skill });
        Assert.True((await Next(bot, CombatService.SkillInterrupt, SkillInterruptResp.Parser)).IsInterruptSucc);
        await Next(bot, CombatService.SkillEnd, SkillEndNotify.Parser, m => m.SessionId == accepted.SessionId);
        await Task.Delay(timeline.SkillMs + 10);
        return chant ? timeline.ChantMs : timeline.SkillMs;
    }
}
