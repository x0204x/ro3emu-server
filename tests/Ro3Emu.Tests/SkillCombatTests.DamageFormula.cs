using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private static SkillConfig FormulaRow(string name, params long[][] tuples) => new(JsonSerializer.SerializeToElement(new
    {
        _kDamageFormulaExtra = Enumerable.Repeat(name, tuples.Length).ToArray(),
        _kDamageParamExtra = tuples,
    }));

    private static long StatsOf(int id) => id switch { 2 => 100, 6 => 250, 115 => 200, 101 => 50000, _ => 0 };

    [Fact]
    public void AnAgilityBonusFollowsItsTuple() => // Blitz Beat: every 20 AGI, 1%
        Assert.Equal(1.05, DamageFormula.Factor(FormulaRow("T_AXadd0_etc", [1, 5, 2, 1]), 0, StatsOf), 6);

    [Fact]
    public void ResonanceStopsAtItsLimit() => // Luck Resonance 0.1% a point, up to 100 of the 250 here
        Assert.Equal(1.10, DamageFormula.Factor(FormulaRow("T_AXadd0_etc_withlimit", [1, 10, 6, 1, 100]), 0, StatsOf), 6);

    [Fact]
    public void AnAspdBonusComesFromTheWholeAspd() => // 0.5% per 1%, at 200%
        Assert.Equal(2.0, DamageFormula.Factor(FormulaRow("AXaddBplus1_withlimit", [50, 115, 10000, 500]), 0, StatsOf), 6);

    [Fact]
    public void AnAspdBonusStopsAtItsLimit() =>
        Assert.Equal(1.1, DamageFormula.Factor(FormulaRow("AXaddBplus1_withlimit", [50, 115, 10000, 20]), 0, StatsOf), 6);

    [Theory]
    [InlineData(2, 10000, 101, 3)] // a shield or Resurrection amount from the target's HP
    [InlineData(1, 10000, 101, 3)] // an amount from the caster's HP
    public void AnAmountIsNoFactor(long source, long coefficient, long attr, long mode) =>
        Assert.Equal(1, DamageFormula.Factor(FormulaRow("T_AXadd0_etc", [source, coefficient, attr, mode]), 0, StatsOf));

    [Fact]
    public void AFormulaPerValueGoesWithItsValue()
    {
        var row = FormulaRow("AXaddBplus1_withlimit", [10, 115, 10000, 500], [50, 115, 10000, 500]);
        Assert.Equal(1.2, DamageFormula.Factor(row, 0, StatsOf), 6);
        Assert.Equal(2.0, DamageFormula.Factor(row, 1, StatsOf), 6);
    }

    [Fact]
    public void HealsGrowWithTheLevelAndHitsDoNot()
    {
        var row = FormulaRow("T_AXadd0_Xisskilllv", [1, 10, 0, 1]);
        Assert.Equal(1.08, DamageFormula.HealFactor(row, StatsOf, 80), 6);
        Assert.Equal(1, DamageFormula.Factor(row, 0, StatsOf));
    }

    [ClientDataFact]
    public async Task ARealRowsFormulaReadsTheCastersStats()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100");
        var a = Session("alice");
        var data = _app.Services.GetRequiredService<GameData>();
        var stats = Character.Stats(a);
        long aspd = stats[115];
        // 1160201: [50, 115, 10000, 500], "For every 1% ASPD, this DMG increases by 0.5%".
        Assert.Equal(1 + 50 * aspd / 10000.0, DamageFormula.Factor(SkillConfig.Get(data, 1160201)!.Value, 0, id => stats[id]), 6);
        // 1310201 Blitz Beat: [1, 5, 2, 1], AGI.
        Assert.Equal(1 + 5 * stats[2] / 10000.0, DamageFormula.Factor(SkillConfig.Get(data, 1310201)!.Value, 0, id => stats[id]), 6);
        // 1411201 Resurrection's amounts stay out of it.
        Assert.Equal(1, DamageFormula.Factor(SkillConfig.Get(data, 1411201)!.Value, 0, id => stats[id]));
    }
}
