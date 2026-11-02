using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>What a cleanse removes (CombatService.CleanseKinds, Cleanses). Except in the client data test, the ids are invented.</summary>
public sealed class CleanseRulesTests
{
    private static JsonElement Skill(string script, params int[] logic) =>
        JsonSerializer.SerializeToElement(new { _kScriptLogic = script, _kLogicData = logic });

    private static BuffConfig Buff(int type, params int[] categories) => Row(type, 1, categories);

    private static BuffConfig Row(int type, int disperse, params int[] categories) =>
        new(JsonSerializer.SerializeToElement(new { _iType = type, _iDisperse = disperse, _kBuffCategory = categories }));

    [Fact]
    public void AHealingSkillRemovesTheKindsItsDataNames()
    {
        Assert.Equal([3, 4], CombatService.CleanseKinds(Skill("Cleanse", 3, 4)));
        Assert.Equal([3, 4, 7], CombatService.CleanseKinds(Skill("Cleanse2", 3, 4, 7)));
    }

    [Fact]
    public void AntidoteAndDetoxifyRemovePoisonedWhateverTheirData()
    {
        Assert.Equal([1], CombatService.CleanseKinds(Skill("jieduji")));
        Assert.Equal([1], CombatService.CleanseKinds(Skill("PoisonBoom", 9999)));
    }

    [Fact]
    public void AnOrdinarySkillRemovesNothing() => Assert.Empty(CombatService.CleanseKinds(Skill("Heal", 3, 4)));

    [Theory]
    [InlineData(3, true)] // the status itself
    [InlineData(53, true)] // its copy at + 50
    [InlineData(82, true)] // its copy at + 79
    [InlineData(4, false)] // another status
    public void ACleanseMatchesAHostileBuffByItsFirstCategory(int kind, bool removed) =>
        Assert.Equal(removed, CombatService.Cleanses([3], Buff(2, kind, 999)));

    [Fact]
    public void ACleanseLeavesFriendlyBuffsAndLaterCategoriesAlone()
    {
        Assert.False(CombatService.Cleanses([3], Buff(1, 3)));
        Assert.False(CombatService.Cleanses([3], Buff(2, 999, 3)));
        Assert.False(CombatService.Cleanses([3], Buff(2)));
    }

    [Fact]
    public void ACleanseLeavesABuffThatCannotBeDispersed() =>
        Assert.False(CombatService.Cleanses([3], Row(2, 0, 3)));

    [ClientDataFact]
    public void TheStrongCleanseLeavesABossFrozenButNotAnOrdinaryOne()
    {
        var data = new GameData(Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") }),
            NullLogger<GameData>.Instance);
        int[] kinds = CombatService.CleanseKinds(data.Row("SkillConfig", 1401411)!.Value);
        Assert.Contains(9, kinds);
        Assert.False(CombatService.Cleanses(kinds, BuffConfig.Get(data, 4115501)!.Value)); // boss skill 4118801, kind 9, _iDisperse 0
        Assert.Contains(BuffConfig.All(data), buff => buff.BuffCategory is [9, ..] && CombatService.Cleanses(kinds, buff));
    }
}
