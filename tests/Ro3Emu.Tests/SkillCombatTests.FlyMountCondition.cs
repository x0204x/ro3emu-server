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
    public void FlyMountConditionUsesTheThirdFlagAndPreservesUnknownFields()
    {
        static JsonElement Map(int fly, params int[] flags) =>
            JsonSerializer.SerializeToElement(new { _iFly = fly, _kRiderForbidden = flags });
        Assert.True(CombatService.FlyMountMapAllowed(Map(0, 1, 1, 0)));
        Assert.False(CombatService.FlyMountMapAllowed(Map(1, 0, 0, 1)));
        Assert.False(CombatService.FlyMountMapAllowed(null));
        Assert.Null(CombatService.FlyMountMapAllowed(Map(1, 0, 0)));
        Assert.Null(CombatService.FlyMountMapAllowed(JsonSerializer.SerializeToElement(new { _iFly = 1 })));
        Assert.Null(CombatService.FlyMountMapAllowed(JsonSerializer.SerializeToElement(new { _kRiderForbidden = new[] { "0", "0", "0" } })));
        Assert.Null(CombatService.FlyMountMapAllowed(JsonSerializer.SerializeToElement(new { _kRiderForbidden = new[] { 0.0, 0.0, 0.5 } })));
    }

    [ClientDataFact]
    public async Task FlyMountConditionAllThreeAuthoredSkillsUseMapEntryNotRuntimeScene()
    {
        await using var bot = await InWorld("FlyCondition");
        var source = Session("flycondition");
        var data = _app.Services.GetRequiredService<GameData>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        await Loop.InvokeAsync(() =>
        {
            long originalMap = source.SceneEntry, originalInstance = source.InstanceId;
            try
            {
                source.InstanceId = 900_000_000;
                int allowed = 0, differentFromFly = 0, count = 0;
                foreach (var row in data.Table("MapConfig").Values)
                {
                    var map = new MapConfig(row);
                    Assert.True(map.RiderForbidden.Length >= 3);
                    source.SceneEntry = map.Id;
                    Assert.NotEqual(source.SceneEntry, source.WorldScene);
                    bool expected = map.RiderForbidden[2] == 0;
                    Assert.Equal(expected, combat.EvaluateFlyMountCastCondition(source, [2009, 1]));
                    Assert.Equal(expected, combat.EvaluateFlyMountCastCondition(source, [2009, -999]));
                    if (expected) allowed++;
                    if (expected != (map.Fly == 1)) differentFromFly++;
                    count++;
                }
                Assert.Equal(121, count); Assert.Equal(42, allowed); Assert.Equal(34, differentFromFly);
                foreach (int id in new[] { 3110101, 3111101, 3112101 })
                {
                    var skill = SkillConfig.Get(data, id) ?? throw new InvalidOperationException("原起飛技能列缺失。");
                    var condition = data.Row("LC_ConditionList", skill.SkillCondition)
                        ?? throw new InvalidOperationException("原場景條件缺失。");
                    Assert.True(CombatService.EvaluateKnownSkillCondition(condition, values =>
                    {
                        Assert.Equal(new long[] { 2009, 1 }, values);
                        return true;
                    }));
                }
                Assert.Null(combat.EvaluateFlyMountCastCondition(source, [2003, 1]));
                Assert.Null(combat.EvaluateFlyMountCastCondition(source, [2009]));
            }
            finally { source.SceneEntry = originalMap; source.InstanceId = originalInstance; }
            return Task.CompletedTask;
        });
    }

    [ClientDataTheory]
    [InlineData(3110101)]
    [InlineData(3111101)]
    [InlineData(3112101)]
    public async Task FlyMountConditionRealSkillAdmissionRefusesForbiddenMapBeforeCosts(int skillId)
    {
        await using var bot = await InWorld("FlyAdmission");
        var source = Session("flyadmission");
        var data = _app.Services.GetRequiredService<GameData>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        var skill = SkillConfig.Get(data, skillId) ?? throw new InvalidOperationException("原起飛技能列缺失。");
        long forbidden = data.Table("MapConfig").Values.Select(r => new MapConfig(r))
            .First(m => m.Fly == 1 && m.RiderForbidden.Length >= 3 && m.RiderForbidden[2] == 1).Id;
        long allowed = data.Table("MapConfig").Values.Select(r => new MapConfig(r))
            .First(m => m.RiderForbidden.Length >= 3 && m.RiderForbidden[2] == 0).Id;
        long originalMap = source.SceneEntry;
        long? originalSp = source.CurrentSp;
        // 目前沒有產品休閒坐騎起飛授技owner；此adapter只隔離真31102 admission，不冒稱可起飛。
        bool Granted(GameSession actor, int id) => ReferenceEquals(actor, source) && id == skillId;
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skillId });
        Assert.Equal(ErrorCodes.SkillNotLearned, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Loop.InvokeAsync(() =>
        {
            combat.GrantedSkill.Add(Granted); source.SceneEntry = forbidden;
            return Task.CompletedTask;
        });
        try
        {
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skillId });
            Assert.Equal(ErrorCodes.SkillRequirements, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await Loop.InvokeAsync(() =>
            {
                Assert.Null(source.SkillCast); Assert.Equal(originalSp, source.CurrentSp);
                Assert.Equal(0, source.SkillCds.GetValueOrDefault((int)skill.SkillId));
                Assert.Equal(0, source.SkillModeCds.GetValueOrDefault(((int)skill.SkillId, 0)));
                Assert.False(source.WorldState.Flying); source.SceneEntry = allowed;
                return Task.CompletedTask;
            });
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skillId });
            Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await Loop.InvokeAsync(() =>
            {
                Assert.NotNull(source.SkillCast); Assert.Equal(skillId, source.SkillCast.Request.SkillId);
                Assert.Equal(originalSp, source.CurrentSp); Assert.False(source.WorldState.Flying);
                return Task.CompletedTask;
            });
        }
        finally
        {
            await Loop.InvokeAsync(async () =>
            {
                await combat.InterruptAsync(source);
                combat.GrantedSkill.Remove(Granted); source.SceneEntry = originalMap;
            });
        }
    }
}
