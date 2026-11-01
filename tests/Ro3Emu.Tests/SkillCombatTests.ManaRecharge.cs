using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public void ManaRechargeAllTenLevelsMatchThePairedSpCostPattern()
    {
        var data = _app.Services.GetRequiredService<GameData>();
        for (int level = 1; level <= 10; level++)
        {
            var modifier = Assert.Single(SkillParameterRules.BuffModifiers(data, 1420400 + level));
            Assert.True(SkillParameterRules.IsSpCostModifier(modifier));
            Assert.Equal(-100 * level, modifier.Value);
            var row = data.Row("BuffConfig", 1420400 + level);
            Assert.True(row.HasValue);
            Assert.Contains(GameData.Pairs(row.Value, "_kChangeAttri"), p => p.Id == AttributeId.PerSpDedution && p.Value == modifier.Value);
        }
    }

    [ClientDataFact]
    public async Task ManaRechargePublishesTheCostAndChargesSpOnlyOnce()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99");
        await Gm("alice", "!job 4300");
        await Teach("alice", 14201);
        await Teach("alice", 14204, 10);
        var combat = _app.Services.GetRequiredService<CombatService>();
        var player = Session("alice");
        await Loop.InvokeAsync(() => combat.RefreshSkillParametersAsync(player));
        var dynamic = await Next(alice, CombatService.SkillAffectedEx, SkillBeAffectedParamExNotify.Parser,
            n => n.AffectParamDict.TryGetValue(14201, out var skill) && skill.AffectParam.ContainsKey(SkillParameterRules.SkillCostType));
        var cost = Assert.Single(dynamic.AffectParamDict[14201].AffectParam[SkillParameterRules.SkillCostType].AffectValueListList);
        Assert.Equal([1L, AttributeId.Sp, 0L, 14L], cost.AffectValueList.ToArray());
        await Loop.InvokeAsync(async () =>
        {
            Assert.Equal(-1000, Character.Stats(player)[AttributeId.PerSpDedution]);
            Assert.Equal(14, combat.EffectiveSkillRow(player, 1420101)!.Value.GetProperty("_kCost")[0][3].GetInt64());
            await Character.SetHpSpAsync(player, null, 14);
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1420101 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(0, player.CurrentSp);
            return Task.CompletedTask;
        });
        await Loop.InvokeAsync(async () =>
        {
            using var held = await CharacterService.Lock(player);
            player.Progress!.Skills.Remove(14204);
            await combat.RefreshSkillParametersAsync(player);
            Assert.Equal(15, combat.EffectiveSkillRow(player, 1420101)!.Value.GetProperty("_kCost")[0][3].GetInt64());
        });
        await Next(alice, CombatService.SkillAffectedEx, SkillBeAffectedParamExNotify.Parser,
            n => n.IsAll && (!n.AffectParamDict.TryGetValue(14201, out var skill) || !skill.AffectParam.ContainsKey(SkillParameterRules.SkillCostType)));
    }
}
