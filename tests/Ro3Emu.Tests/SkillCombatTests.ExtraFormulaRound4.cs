using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1210711, 1, 110, 500, 1)]
    [InlineData(1210711, 5, 110, 500, 1)]
    [InlineData(1210720, 1, 200, 500, 1)]
    [InlineData(1210720, 5, 200, 500, 1)]
    [InlineData(1221011, 1, 845, 400, 0.75)]
    [InlineData(1221011, 5, 845, 400, 0.75)]
    [InlineData(1221020, 1, 1700, 400, 0.75)]
    [InlineData(1221020, 5, 1700, 400, 0.75)]
    [InlineData(1610401, 1, 640, 500, 1)]
    [InlineData(1610401, 5, 640, 500, 1)]
    [InlineData(1610410, 1, 1180, 500, 1)]
    [InlineData(1610410, 5, 1180, 500, 1)]
    public async Task ExtraFormulaHitTargetCountUsesLowAndHighestRowsInRealDamage(int id, int count, int baseline,
        int bonus, double element)
    {
        await using var bot = await InWorld("FormulaCount");
        var player = Session("formulacount");
        await HealingStat(bot, 103, 1000); await HealingStat(bot, 104, 1000);
        Assert.Equal(1000, Character.Stats(player)[103]); Assert.Equal(1000, Character.Stats(player)[104]);
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var data = _app.Services.GetRequiredService<GameData>();
        var row = SkillConfig.Get(data, id) ?? throw new InvalidOperationException("Paired skill row is required.");
        var targets = new List<MonsterUnit>();
        for (int i = 0; i < count; i++)
        {
            var target = await Target("formulacount", stationary: true);
            await CardOffenceDummyAsync(target); targets.Add(target);
        }
        await Loop.InvokeAsync(() => combat.CastExtraAsync(player, row.Row, targets));
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == id);
        long expected = (long)Math.Round(baseline * (1 + count * bonus / 10000d) * element);
        Assert.Equal(count, hit.Dsts.Count);
        Assert.All(hit.Dsts, d => Assert.Equal(expected, d.Damage));
        Assert.All(targets, t => Assert.Equal(1_000_000 - expected, t.Hp));
    }

    [ClientDataTheory]
    [InlineData(1310201, 2, 20, 660, 5)]
    [InlineData(1310205, 2, 400, 1100, 5)]
    [InlineData(1311001, 2, 20, 33, 5)]
    [InlineData(1311005, 2, 400, 55, 5)]
    [InlineData(1631401, 1, 20, 800, 10)]
    [InlineData(1631403, 1, 400, 1000, 10)]
    public async Task ExtraFormulaAttributeBonusUsesLowAndHighestAuthoredDamage(int id, int attribute, int value, int baseline, int coefficient)
    {
        await using var bot = await InWorld("FormulaAttribute");
        var player = Session("formulaattribute");
        await HealingStat(bot, attribute, value); await HealingStat(bot, 103, 1000);
        // 高STR/AGI仍會衍生ATK；測試讀取真實輸入，獨立以原件係數計算期望，不反覆改寫GM基礎值。
        Assert.Equal(value, Character.Stats(player)[attribute]);
        long attack = Character.Stats(player)[103]; Assert.True(attack > 0);
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var data = _app.Services.GetRequiredService<GameData>();
        var row = SkillConfig.Get(data, id) ?? throw new InvalidOperationException("Paired skill row is required.");
        var target = await Target("formulaattribute", stationary: true); await CardOffenceDummyAsync(target);
        Assert.Equal(baseline, 1000 * row.DamageParam1[0] / 10000d + row.DamageParam2.ElementAtOrDefault(0));
        long expected = (long)Math.Round((attack * row.DamageParam1[0] / 10000d + row.DamageParam2.ElementAtOrDefault(0))
            * (1 + value * coefficient / 10000d));
        Assert.Equal(expected, await CardOffenceHitAsync(bot, player, target, row.Row));
    }

    [ClientDataFact]
    public async Task ExtraFormulaTargetCountIsSharedBeforeAnyMixedPvpOrMonsterDamage()
    {
        await using var alice = await InWorld("FormulaMixedA");
        await using var bob = await InWorld("FormulaMixedB");
        var source = Session("formulamixeda"); var target = Session("formulamixedb");
        await CardOffenceRoom((alice, source), (bob, target));
        await HealingStat(alice, 104, 1000);
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var data = _app.Services.GetRequiredService<GameData>();
        var row = SkillConfig.Get(data, 1221011) ?? throw new InvalidOperationException("Paired skill row is required.");
        var monsters = new List<MonsterUnit>();
        for (int i = 0; i < 2; i++)
        {
            var monster = await Target("formulamixeda", stationary: true);
            await CardOffenceDummyAsync(monster); monsters.Add(monster);
        }
        await Loop.InvokeAsync(() =>
        {
            combat.SetEventPvPHandlers((a, b) => a == source && b == target, (_, _) => Task.CompletedTask);
            target.Position = monsters[0].Pos.Clone();
            return combat.CastExtraAsync(source, row.Row, monsters);
        });
        const long expected = 710; // (1000*0.68+165)*1.12*0.75 = 709.8，三個共同目標。
        var pvp = await Next(bob, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SkillId == 1221011 && h.Dsts.Any(d => d.DstID == target.Role!.Id));
        Assert.Equal(expected, Assert.Single(pvp.Dsts).Damage);
        Assert.Equal(1_000_000 - expected, target.CurrentHp);
        var pve = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SkillId == 1221011 && h.Dsts.Any(d => d.DstID == monsters[0].Uid));
        Assert.Equal(2, pve.Dsts.Count); Assert.All(pve.Dsts, d => Assert.Equal(expected, d.Damage));
        Assert.All(monsters, m => Assert.Equal(1_000_000 - expected, m.Hp));
    }
}
