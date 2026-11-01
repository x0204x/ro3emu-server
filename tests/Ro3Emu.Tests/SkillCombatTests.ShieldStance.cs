using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1, 1500, 1950)]
    [InlineData(5, 2700, 3750)]
    public async Task ShieldStanceTwentiethHitGrantsExactlyThreeEnhancedCasts(int level, long regular, long enhanced)
    {
        await using var bot = await InWorld("ShieldStance");
        await Gm("shieldstance", "!job 1410"); await Teach("shieldstance", 11636, level);
        await EquipConditionFixtureAsync("shieldstance", 2500120, 2);
        await HealingStat(bot, 103, 1000);
        var source = Session("shieldstance"); var target = await Target("shieldstance", stationary: true);
        await CardOffenceDummyAsync(target);
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(async () =>
        {
            await buffs.ReconcileEnergyAsync(source);
            Assert.True(source.Energy.ContainsKey(141002));
            source.Energy[141002].Value = 19; // 真能量表的轉換邊界，不跳過施放/命中流程。
        });
        await OrdinaryCastFinished(bot, source, 1163600 + level, target.Uid);
        Assert.Equal(1_000_000 - regular, target.Hp);
        Assert.Equal(0, source.Energy[141002].Value);
        Assert.Equal(3, Assert.Single(source.Buffs, b => b.ConfigId == 1163101).PileCount);
        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(1100);
            await OrdinaryCastFinished(bot, source, 1163600 + level, target.Uid);
            Assert.Equal(1_000_000 - regular - enhanced * (i + 1), target.Hp);
            Assert.Equal(0, source.Energy[141002].Value);
            if (i < 2) Assert.Equal(2 - i, Assert.Single(source.Buffs, b => b.ConfigId == 1163101).PileCount);
        }
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 1163101);
        Assert.Equal(1163600 + level, GameData.Int(combat.EffectiveSkillRow(source, 1163600 + level)!.Value, "_iID"));
    }

    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task HorizonSlashGrantsTheAuthoredMovementBuffAtBothLevels(int level)
    {
        await using var bot = await InWorld("HorizonMovement");
        await Gm("horizonmovement", "!job 1400"); await Teach("horizonmovement", 11302, level);
        var source = Session("horizonmovement"); var target = await Target("horizonmovement", stationary: true);
        await CardOffenceDummyAsync(target);
        await OrdinaryCastFinished(bot, source, 1130200 + level, target.Uid);
        var buff = Assert.Single(source.Buffs, b => b.ConfigId == 1130201);
        Assert.Equal(5000, buff.DurationMs);
        Assert.Contains(buff.Changes, c => c.Attr == AttributeId.PerSpeed && c.Value == 2000);
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().RemoveAsync(source, buff));
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 1130201);
    }
}
