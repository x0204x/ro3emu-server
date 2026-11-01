using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>參數映射的邊界測試；額外傷害公式與劇本執行另行驗證，不以改動 JSON 宣稱已完成。</summary>
public sealed class SkillModifierCoverageTests
{
    private static readonly Lazy<GameData> Exported = new(() => new GameData(
        Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") }), NullLogger<GameData>.Instance));

    [Theory]
    [InlineData(22, "_iSkillCri", "1000", "6000")]
    [InlineData(28, "_bMovement", "0", "2")]
    [InlineData(31, "_kLogicData", "[3,150]", "[3,250]")]
    [InlineData(32, "_kShowDmg", "[1,2]", "[1,3]")]
    [InlineData(36, "_kRange", "[550,200]", "[550,1000]")]
    public void ScalarAndVectorSelectionChangesOnlyTheSelectedField(int type, string field, string before, string after)
    {
        var row = JsonDocument.Parse($$"""{"_iSkillID":777,"{{field}}":{{before}},"unrelated":123}""").RootElement;
        var expected = JsonDocument.Parse(after).RootElement;
        long value = expected.ValueKind == JsonValueKind.Array ? expected[1].GetInt64() : expected.GetInt64();
        var mod = new SkillParameterRules.Modifier(777, type, 2, value, 1);
        var effective = SkillParameterRules.Effective(row, [mod], []);
        Assert.Equal(expected.GetRawText(), effective.GetProperty(field).GetRawText());
        Assert.Equal(123, effective.GetProperty("unrelated").GetInt32());
        Assert.Equal(before, row.GetProperty(field).GetRawText());
        Assert.Equal(row.GetRawText(), SkillParameterRules.Effective(row, [mod with { Group = 778 }], []).GetRawText());
    }

    [Theory]
    [InlineData(1, 110)]
    [InlineData(2, 10)]
    [InlineData(5, 200)]
    public void ExplicitCostMatrixPreservesResourceIdsAndUntargetedCells(int operation, long expected)
    {
        var row = JsonDocument.Parse("""{"_iSkillID":777,"_kCost":[[1,2000,0,100],[7,2001,101,100],[2,987,100],[8,654,0,100]]}""").RootElement;
        var mod = new SkillParameterRules.Modifier(777, 35, operation, operation == 5 ? 10000 : 10, -1, Column: 3);
        var effective = SkillParameterRules.Effective(row, [mod], []).GetProperty("_kCost");
        foreach (int index in new[] { 0, 1, 3 })
        {
            Assert.Equal(expected, effective[index][3].GetInt64());
            for (int col = 0; col < 3; col++) Assert.Equal(row.GetProperty("_kCost")[index][col].GetInt64(), effective[index][col].GetInt64());
        }
        Assert.Equal(row.GetProperty("_kCost")[2].GetRawText(), effective[2].GetRawText());
        var unrelated = SkillParameterRules.Effective(row, [mod with { Column = 0 }], []);
        Assert.Equal(row.GetRawText(), unrelated.GetRawText());
    }

    [Fact]
    public void ExtraFormulaMatrixChangesTheAuthoredCellWithoutInventingAFormula()
    {
        var row = JsonDocument.Parse("""{"_iSkillID":777,"_kDamageFormulaExtra":["unknown"],"_kDamageParamExtra":[[10000,1],[20000,2,500]]}""").RootElement;
        var mod = new SkillParameterRules.Modifier(777, 37, 1, 3000, -1, Column: 0);
        var effective = SkillParameterRules.Effective(row, [mod], []);
        Assert.Equal("[[13000,1],[23000,2,500]]", effective.GetProperty("_kDamageParamExtra").GetRawText());
        Assert.Equal("unknown", effective.GetProperty("_kDamageFormulaExtra")[0].GetString());
    }

    [Fact]
    public void AllSkillCooldownChangesCooldownWithoutChangingAnUnrelatedField()
    {
        var tuple = JsonDocument.Parse("""["0","-1","0","5","-3000"]""").RootElement;
        var modifier = SkillParameterRules.Parse(tuple);
        Assert.NotNull(modifier);
        Assert.True(SkillParameterRules.IsAllSkillModifier(modifier.Value));
        foreach (int group in new[] { 777, 888 })
        {
            var row = JsonSerializer.SerializeToElement(new { _iSkillID = group, _iCD = 8000, _iActionTime = 1000 });
            var effective = SkillParameterRules.Effective(row, [modifier.Value], []);
            Assert.Equal(5600, effective.GetProperty("_iCD").GetInt64());
            Assert.Equal(1000, effective.GetProperty("_iActionTime").GetInt32());
        }
    }

    [Theory]
    [InlineData(9)]
    [InlineData(38)]
    [InlineData(39)]
    public void UnrecoveredHitStateAndUsabilitySemanticsDoNotInventFields(int type)
    {
        var row = JsonDocument.Parse("""{"_iSkillID":777,"_kSkillStageStates":[[1,1]],"_iSkillHit":0}""").RootElement;
        Assert.Equal(row.GetRawText(), SkillParameterRules.Effective(row,
            [new SkillParameterRules.Modifier(777, type, 2, 2, 0, Column: 1)], []).GetRawText());
    }

    [ClientDataFact]
    public void AllThirtyEightPairedCostTuplesHaveAnExplicitSupportedCostSelector()
    {
        var data = Exported.Value;
        var modifiers = data.Table("BuffConfig").Values.SelectMany(row => SkillParameterRules.BuffModifiers(data, (int)GameData.Int(row, "_iID")))
            .Where(m => m.Type == 35).ToArray();
        Assert.Equal(38, modifiers.Length);
        Assert.Equal(10, modifiers.Count(SkillParameterRules.IsSpCostModifier));
        Assert.All(modifiers, m => Assert.True(SkillParameterRules.IsCostModifier(m)));
        var reduced = SkillParameterRules.BuffModifiers(data, 1153401);
        Assert.Equal(4, reduced.Length);
        foreach (var mod in reduced)
            foreach (var row in data.Table("SkillConfig").Values.Where(r => GameData.Int(r, "_iSkillID") == mod.Group))
            {
                var effective = SkillParameterRules.Effective(row, [mod], []);
                if (row.GetProperty("_kCost").ValueKind != JsonValueKind.Array) continue;
                Assert.Equal(2000, row.GetProperty("_kCost")[0][1].GetInt32());
                Assert.Equal(30, row.GetProperty("_kCost")[0][3].GetInt32());
                Assert.Equal(0, effective.GetProperty("_kCost")[0][3].GetInt64());
            }
    }

    [ClientDataFact]
    public void AuthoredLogicChangesIncludePiercingTargetsWidthAndSonicThreshold()
    {
        var data = Exported.Value;
        var sharpshooting = data.Row("SkillConfig", 1320401)!.Value;
        Assert.Equal("[0,150]", SkillParameterRules.Effective(sharpshooting, SkillParameterRules.BuffModifiers(data, 1323201), [])
            .GetProperty("_kLogicData").GetRawText());
        Assert.Equal("[4,250]", SkillParameterRules.Effective(sharpshooting, SkillParameterRules.BuffModifiers(data, 1323301), [])
            .GetProperty("_kLogicData").GetRawText());
        var sonic = data.Row("SkillConfig", 1510201)!.Value;
        Assert.Equal(7000, SkillParameterRules.Effective(sonic, SkillParameterRules.BuffModifiers(data, 1522701), [])
            .GetProperty("_kLogicData")[2].GetInt64());
        Assert.Equal(5000, sonic.GetProperty("_kLogicData")[2].GetInt64());
    }
}
