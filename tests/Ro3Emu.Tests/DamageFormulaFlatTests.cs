using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class DamageFormulaFlatTests
{
    private static SkillConfig Row(string formula, string extra) => new(JsonDocument.Parse(
        "{\"_kDamageFormulaExtra\":[\"" + formula + "\"],\"_kDamageParamExtra\":" + extra + "}").RootElement.Clone());

    // The airship cannons 5353001 and 5353501: T_AXaddBadd3_etc [0,0,1,3000], read as on the buff rows that heal by it.
    [Fact]
    public void TheCannonsFormulaAddsItsFlatPart()
    {
        Assert.Equal(3000, DamageFormula.Flat(Row("T_AXaddBadd3_etc", "[[0,0,1,3000]]"), 0));
        Assert.Equal(0, DamageFormula.Flat(Row("T_AXadd0_etc", "[[1,5,2,1]]"), 0));
        Assert.Equal(0, DamageFormula.Flat(Row("T_AXaddBadd3_etc", "[[0,5,1,3000]]"), 0)); // a coefficient: not this shape
    }
}
