using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1, 5035)]
    [InlineData(10, 5260)]
    public async Task PassivePvpPreyUsesOneLiveNegativeStateAndRevokesWithTheLearnedSkill(int level, long expected)
    {
        await using var bot = await InWorld("PreyPvp"); await using var victim = await InWorld("PreyVictim");
        await Gm("preypvp", "!job 5400"); await Gm("preyvictim", "!job 5400");
        await Teach("preypvp", 15316, level);
        var source = Session("preypvp"); var target = Session("preyvictim");
        var combat = await CastPassiveRoomAsync(bot, source, victim, target);
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var row = SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), 1000001)!.Value;
        Assert.Equal(5000, row.DamageParam1[0]); Assert.Equal(10, row.DamageParam2[0]);
        Assert.Equal(10000, Character.Stats(source)[103]); // 原普攻為50% ATK+10，不是100% ATK。
        async Task<long> Hit()
        {
            long hp = target.CurrentHp!.Value;
            await Loop.InvokeAsync(() => combat.CastExtraOnPlayerAsync(source, row, target));
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SkillId == row.Id);
            long damage = Assert.Single(hit.Dsts, x => x.DstID == target.Role!.Id).Damage;
            Assert.Equal(hp - damage, target.CurrentHp); return damage;
        }
        Assert.Equal(5010, await Hit());
        await Loop.InvokeAsync(() => buffs.AddAsync(target, 901601, 10000, source.Role!.Id));
        Assert.Contains(target.Buffs, b => b.ConfigId == 901601);
        Assert.Equal(expected, await Hit());
        // 同時兩種負面狀態仍只加一次；清除後即回復基礎傷害。
        await Loop.InvokeAsync(() => buffs.AddAsync(target, 902301, 10000, source.Role!.Id));
        Assert.Contains(target.Buffs, b => b.ConfigId == 902301);
        Assert.Equal(expected, await Hit());
        await Loop.InvokeAsync(async () =>
        {
            foreach (var buff in target.Buffs.ToArray()) await buffs.RemoveAsync(target, buff);
        });
        Assert.Equal(5010, await Hit());
        await Loop.InvokeAsync(() => buffs.AddAsync(target, 901001, 10000, source.Role!.Id));
        Assert.Equal(5010, await Hit()); // 正面狀態不算異常狀態。
        await Loop.InvokeAsync(async () =>
        {
            await buffs.AddAsync(target, 901601, 10000, source.Role!.Id);
            source.Progress!.Skills.Remove(15316);
        });
        Assert.Equal(5010, await Hit());
    }

    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task PassivePvpSoulDrainRestoresTheAuthoredPointOnePercentOnlyAfterARealKill(int level)
    {
        await using var bot = await InWorld("DrainPvp"); await using var victim = await InWorld("DrainVictim");
        await Gm("drainpvp", "!job 2400"); await Gm("drainvictim", "!job 2400");
        await Teach("drainpvp", 12209, level);
        var source = Session("drainpvp"); var target = Session("drainvictim");
        var combat = await CastPassiveRoomAsync(bot, source, victim, target);
        var row = SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), 1000001)!.Value;
        Assert.Equal("0.1", SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), 1220900 + level)!.Value.DescData[1]);
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(source, null, 100));
        long maxSp = Character.Stats(source)[102];
        Assert.Equal(10, (long)Math.Round(maxSp / 1000d)); // 原永久SP加成會影響最終MaxSP；取實際值驗0.1%。
        await Loop.InvokeAsync(() => combat.CastExtraOnPlayerAsync(source, row, target));
        Assert.False(target.Dead); Assert.Equal(100, source.CurrentSp);
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(target, 1, null));
        await Loop.InvokeAsync(() => combat.CastExtraOnPlayerAsync(source, row, target));
        Assert.True(target.Dead); Assert.Equal(0, target.CurrentHp);
        Assert.Equal(110, source.CurrentSp); // 低級與滿級原表同為0.1%，不是10%。
        // 死亡目標再送攻擊不能再發一次擊殺事件或SP。
        await Loop.InvokeAsync(() => combat.CastExtraOnPlayerAsync(source, row, target));
        Assert.Equal(110, source.CurrentSp);
    }
}
