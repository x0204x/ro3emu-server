using System.Text.Json;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>合成成本：只驗證已核對的全技能 SP tuple，不開放其他 wildcard 或改動非 SP 成本。</summary>
public sealed class SkillCostModifierTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void AllSkillSpPercentageChangesOnlyTheAuthoredCostCell(int level)
    {
        var modifier = Parse(level);
        var row = JsonDocument.Parse("""{"_iSkillID":777,"_kCost":[[1,2001,0,100],[1,2000,0,50],[2,876,3],[8,123,0,20]]}""").RootElement;
        Assert.True(SkillParameterRules.IsSpCostModifier(modifier));
        Assert.True(SkillParameterRules.Matches(modifier, 777, []));
        Assert.True(SkillParameterRules.Matches(modifier, 888, []));
        var effective = SkillParameterRules.Effective(row, [modifier], []);
        Assert.Equal(100 - level, effective.GetProperty("_kCost")[0][3].GetInt64());
        for (int i = 1; i < 4; i++)
            Assert.Equal(row.GetProperty("_kCost")[i].GetRawText(), effective.GetProperty("_kCost")[i].GetRawText());
        Assert.Equal(100, row.GetProperty("_kCost")[0][3].GetInt64());
    }

    [Theory]
    [InlineData("[[1,2000,0,100],[1,2001,0,100]]")]
    [InlineData("[[2,876,100],[1,2001,0,100]]")]
    [InlineData("[[8,123,0,100],[1,2001,0,100]]")]
    [InlineData("[]")]
    public void NonSpFirstCostAndAbsentCostAreUnchanged(string costs)
    {
        var row = JsonDocument.Parse("{\"_iSkillID\":777,\"_kCost\":" + costs + "}").RootElement;
        var effective = SkillParameterRules.Effective(row, [Parse(10)], []);
        Assert.Equal(row.GetProperty("_kCost").GetRawText(), effective.GetProperty("_kCost").GetRawText());
    }

    [Fact]
    public void OtherWildcardStaysUnsupportedAndExplicitSkillCostIsApplied()
    {
        Assert.Null(SkillParameterRules.Parse(JsonDocument.Parse("""["0","-1","34","1","500","0","2"]""").RootElement));
        var targeted = SkillParameterRules.Parse(JsonDocument.Parse("""["0","777","35","5","-1000","0","3"]""").RootElement);
        Assert.NotNull(targeted);
        Assert.False(SkillParameterRules.IsSpCostModifier(targeted.Value));
        var row = JsonDocument.Parse("""{"_iSkillID":777,"_kCost":[[1,2001,0,100]]}""").RootElement;
        Assert.Equal(90, SkillParameterRules.Effective(row, [targeted.Value], []).GetProperty("_kCost")[0][3].GetInt64());
    }

    private static SkillParameterRules.Modifier Parse(int level)
    {
        var tuple = JsonSerializer.SerializeToElement(new[] { 0, -1, SkillParameterRules.SkillCostType, 5, -100 * level, 0, 3 });
        var parsed = SkillParameterRules.Parse(tuple);
        Assert.NotNull(parsed);
        return parsed.Value;
    }
}
