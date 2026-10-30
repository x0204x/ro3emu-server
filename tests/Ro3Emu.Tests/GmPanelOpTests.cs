using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>How the GM panel names ops: GMConfig._kScript and GMCombinationConfig steps use GmOpEnum names, one uses a number.</summary>
public class GmPanelOpTests
{
    [Theory]
    [InlineData("GmOpAddItem", 1)]
    [InlineData("GmequipWeapon", 173)] // the presets' own spelling of GMEquipWeapon
    [InlineData("251", 251)] // GMWarRobe, the one step that names its op by number
    public void PresetStepsResolveTheirOp(string name, int op) => Assert.Equal(op, GmCommands.OpNumber(name));

    [Theory]
    [InlineData("lightupallappearance")]
    [InlineData("123456")]
    public void UnknownNamesAndNumbersDoNotResolve(string name) => Assert.Null(GmCommands.OpNumber(name));

    [Fact]
    public void RefusalsNameTheOp() => Assert.Equal("4 GmOpStrengthen", GmCommands.OpLabel(4));
}
