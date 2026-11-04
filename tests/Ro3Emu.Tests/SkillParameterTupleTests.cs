using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class SkillParameterTupleTests
{
    private static SkillParameterRules.Modifier Tuple(string json) => SkillParameterRules.Parse(JsonDocument.Parse(json).RootElement)!.Value;

    private static SkillConfig Row(string json, params string[] tuples) => new(SkillParameterRules.Effective(
        JsonDocument.Parse(json).RootElement, [.. tuples.Select(Tuple)], []));

    [Fact] // Survival Instinct: "no longer consumes HP": -100% of _kCost's column 3
    public void ACostTupleChangesItsColumn() =>
        Assert.Equal(0, Row("""{"_iSkillID":10011,"_iID":1,"_kCost":[[1,2000,0,30]]}""", """[0,10011,35,5,-10000,0,3]""").Row
            .GetProperty("_kCost")[0][3].GetInt64());

    [Fact] // Prayer: "all skills" is group -1
    public void AGroupOfMinusOneReachesEverySkill() =>
        Assert.Equal(90, Row("""{"_iSkillID":777,"_iID":1,"_kCost":[[1,2001,0,100]]}""", """[0,-1,35,5,-1000,0,3]""").Row
            .GetProperty("_kCost")[0][3].GetInt64());

    [Fact] // Arrow Flash on Sharp Shooting [3,150]: sets both
    public void ALogicTupleEditsItsIndex() =>
        Assert.Equal([4L, 250L], Row("""{"_iSkillID":13204,"_iID":1,"_kLogicData":[3,150]}""", """[0,13204,31,2,4,0]""", """[0,13204,31,2,250,1]""").LogicData);

    [Theory] // Normal Attack Boost: fixed damage on a row whose _kDamageParam2 Lua wrote as {}
    [InlineData("""{"_iSkillID":10051,"_iID":1,"_kDamageParam2":{}}""")]
    [InlineData("""{"_iSkillID":10051,"_iID":1,"_kDamageParam2":[]}""")]
    public void AFlatTupleLandsOnAnEmptyFlatPart(string row) =>
        Assert.Equal([15L], Row(row, """[0,10051,30,1,15,0]""").DamageParam2);

    [Fact] // Decapitation: "Sonic Blow fixed +10"
    public void TypeTwentyIsAFlatPartAtItsIndex() =>
        Assert.Equal([10L], Row("""{"_iSkillID":15102,"_iID":1,"_kDamageParam2":{}}""", """[0,15102,20,1,10,0]""").DamageParam2);
}
