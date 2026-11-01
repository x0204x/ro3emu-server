using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [Fact]
    public void MountConditionBooleanTreesRequireEveryLeafToBeUnderstood()
    {
        static JsonElement Leaf(int id, bool inverse = false) => JsonSerializer.SerializeToElement(new
        { m_eType = 3, m_kData = new[] { 2003, id }, m_bInverse = inverse });
        static JsonElement Branch(int type, params JsonElement[] children) => JsonSerializer.SerializeToElement(new
        { m_eType = type, m_kSubConditionList = children });
        var mado = Leaf(1500300); var wolf = Leaf(1500400);
        Assert.True(CombatService.EvaluateMountCondition(mado, 1500300));
        Assert.False(CombatService.EvaluateMountCondition(mado, 0));
        Assert.True(CombatService.EvaluateMountCondition(Leaf(1500300, true), 0));
        Assert.False(CombatService.EvaluateMountCondition(Leaf(1500300, true), 1500300));
        Assert.True(CombatService.EvaluateMountCondition(Branch(2, mado, wolf), 1500400));
        Assert.False(CombatService.EvaluateMountCondition(Branch(1, mado, wolf), 1500400));
        Assert.True(CombatService.EvaluateMountCondition(Branch(1, Branch(2, mado, wolf), Leaf(0, true)), 1500300));
        var unknown = JsonSerializer.SerializeToElement(new { m_eType = 3, m_kData = new[] { 2001, 50 } });
        Assert.Null(CombatService.EvaluateMountCondition(Branch(2, mado, unknown), 1500300));
        Assert.Null(CombatService.EvaluateMountCondition(Branch(1, wolf, unknown), 1500300));
        var dynamicLeaf = JsonSerializer.SerializeToElement(new { m_eType = 3, m_kData = new[] { 2003, 1500300 }, m_kDynamicData = new[] { 1 } });
        Assert.Null(CombatService.EvaluateMountCondition(dynamicLeaf, 1500300));
        var inverseContainer = JsonSerializer.SerializeToElement(new { m_eType = 1, m_bInverse = true, m_kSubConditionList = new[] { mado } });
        var dynamicContainer = JsonSerializer.SerializeToElement(new { m_eType = 2, m_kDynamicData = new[] { 1 }, m_kSubConditionList = new[] { mado } });
        Assert.Null(CombatService.EvaluateMountCondition(inverseContainer, 1500300));
        Assert.Null(CombatService.EvaluateMountCondition(dynamicContainer, 1500300));
    }

    [ClientDataFact]
    public async Task BattleMountConditionIgnoresQuestAndSystemRidersAndPreservesMixedUnknownTree()
    {
        await using var bot = await InWorld("MountCondition");
        var source = Session("mountcondition");
        await Loop.InvokeAsync(() =>
        {
            source.MountState.QuestMount = 1500300;
            Assert.Equal(0, CombatService.CurrentBattleMount(source));
            Assert.Equal(1500300, MountService.Rider(source));
            Assert.NotNull(source.Mounts);
            source.Mounts.MountIds[1] = new MountInfo { MountId = 1500300, IsRiding = true };
            Assert.Equal(0, CombatService.CurrentBattleMount(source));
            source.Mounts.MountIds[2] = new MountInfo { MountId = 1500400, IsRiding = false };
            Assert.Equal(0, CombatService.CurrentBattleMount(source));
            source.Mounts.MountIds[2].IsRiding = true;
            Assert.Equal(1500400, CombatService.CurrentBattleMount(source));
            source.MountState.QuestMount = 0; source.Mounts.MountIds.Clear();
            return Task.CompletedTask;
        });
        var data = _app.Services.GetRequiredService<GameData>();
        var mixed = SkillConfig.Get(data, 1161301) ?? throw new InvalidOperationException("Gryphon skill required.");
        Assert.Null(CombatService.EvaluateMountCondition(data.Row("LC_ConditionList", mixed.SkillCondition)
            ?? throw new InvalidOperationException("Mixed condition required."), 1500206));
    }
}
