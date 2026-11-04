using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>How a Splash passive reads its buff and proc row, on invented rows. CardRulesTests plays it on the real cards.</summary>
public sealed class CardSplashRuleTests
{
    private static BuffConfig Buff(int group, string logic, string description) => new(JsonDocument.Parse(
        $$"""{"_iBuffID":{{group}},"_kLogicData":{{logic}},"_kDescData":{{description}}}""").RootElement);

    private static SkillConfig Row(string logic = "[77,250]", string extra = "[[1,3,0,3,99999,159,4200]]") => new(JsonDocument.Parse(
        $$"""{"_iTargetMax":7,"_kLogicData":{{logic}},"_kDamageParamExtra":{{extra}}}""").RootElement);

    [Fact]
    public void TheChanceGroupNamesItsChanceAndTheOtherAlwaysSplashes()
    {
        Assert.Equal(new CardEffects.Splash(1234, 1500, 2.5f, 7, 4200, 77),
            CardEffects.SplashOf(Buff(20508, "[1234,99]", """["12.34","2.5","7","42","1.5"]"""), Row()));
        Assert.Equal(new CardEffects.Splash(10000, 250, 2.5f, 7, 4200, 77),
            CardEffects.SplashOf(Buff(20509, "[99]", """["2.5","7","42","0.25"]"""), Row()));
    }

    [Fact]
    public void OtherPassivesAndIncompleteRowsAreNoSplash()
    {
        Assert.Null(CardEffects.SplashOf(Buff(20501, "[750]", """["7.5"]"""), Row()));
        Assert.Null(CardEffects.SplashOf(Buff(20509, "[99]", """["0.25"]"""), Row(logic: "[77]")));
        Assert.Null(CardEffects.SplashOf(Buff(20509, "[99]", """["0.25"]"""), Row(extra: "{}")));
        Assert.Null(CardEffects.SplashOf(Buff(20509, "[99]", """[""]"""), Row()));
    }
}
