using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>自造數值測試：只用程式綁定的識別碼，並非複製原服配置或宣稱玩家端驗收。</summary>
public sealed partial class StoryBossSkillTests
{
    private static GameData Data() => new(Options.Create(new ServerOptions { GameDataDir = SyntheticData.Dir }),
        NullLogger<GameData>.Instance);

    private static void Row(GameData data, string table, long id, object row) =>
        ((IDictionary<string, JsonElement>)data.Table(table))[id.ToString(CultureInfo.InvariantCulture)] = JsonSerializer.SerializeToElement(row);

    private static void Skill(GameData data, int id, string logic, int action = 1600) => Row(data, "SkillConfig", id, new
    {
        _iID = id, _kScriptLogic = logic, _iActionTime = action, _iCD = 7000, _iScriptShow = 991101,
        _iRelease = 1, _iRangeType = 2, _kRange = new[] { 700 }, _kDamageParam1 = new[] { 12345 }, _iTargetMax = 5,
    });

    [Fact]
    public void AllObservedSwingsSurviveTheMinResolverWithoutInventingMissingRows()
    {
        var data = Data();
        foreach (int id in new[] { 5300101, 5300601 }) Skill(data, id, "MainStory_HuWangNormalAttack");
        foreach (int id in new[] { 5352001, 5352101, 5352201 }) Skill(data, id, "MainStory_EvilBossNormalAttack1");
        var attacks = new BossNormalAttacks(data);
        Assert.Equal(new long[] { 5300101, 5300601 }, attacks.VariantsFor(5300001));
        Assert.Equal(new long[] { 5352001, 5352101, 5352201 }, attacks.VariantsFor(5300018));
        ((IDictionary<string, JsonElement>)data.Table("SkillConfig")).Remove("5352101");
        Assert.Equal(new long[] { 5352001, 5352201 }, attacks.VariantsFor(5300018));
        Assert.Empty(attacks.VariantsFor(991234));
    }

    [Fact]
    public void StoryExceptionsDoNotEnableFieldMvpOrUnobservedExtraRoutes()
    {
        Assert.True(StoryBossSkillRules.UsesHighLevel(7, 5300006));
        Assert.False(StoryBossSkillRules.UsesHighLevel(0, 5300006));
        Assert.False(StoryBossSkillRules.UsesHighLevel(7, 5020005));
        Assert.True(StoryBossSkillRules.UsesExtra(7, 5300082, 5370501));
        Assert.False(StoryBossSkillRules.UsesExtra(0, 5300082, 5370501));
        Assert.False(StoryBossSkillRules.UsesExtra(7, 5300001, 5370501));
        Assert.False(StoryBossSkillRules.UsesExtra(7, 5300082, 5370201));
    }

    [Fact]
    public void PecoUsesConfiguredWarningRadiusWithoutFabricatingItsDamageTime()
    {
        var data = Data();
        Row(data, "NPCConfig", 5300082, new { _iID = 5300082, _iAISpecial = "MainStory_DaZui" });
        Skill(data, 5370201, "MainStory_DazuiSkill1");
        var effects = (IDictionary<string, JsonElement>)data.SkillTemplateTable("Lua_Config_EffectData");
        effects["5370202"] = JsonSerializer.SerializeToElement(new
        {
            m_kPoint = new
            {
                m_kWarningRange = new
                {
                    m_fDuration = 0.375, m_fRadius = 7.5,
                    m_bCloseWhileSkillInterrupt = true, m_bCloseWhileMasterDestroy = true
                }
            },
        });
        var warning = StoryWarningData.Find(data, 5370202)!.Value;
        Assert.Equal(375, warning.DurationMs);
        Assert.Equal(7.5f, warning.Radius);
        Assert.True(warning.ClosesOnInterrupt && warning.ClosesOnRemoval);
        var skill = Assert.Single(new BossSkillSets(data, new BossSkillOptions()).For(5300082, 0)!.Skills);
        Assert.True(skill.TargetsSelf);
        Assert.Equal(BossSkillArea.AroundSelf, skill.Area);
        Assert.Equal(7.5f, skill.Radius);
        Assert.Empty(skill.HitMs); // 不以 70% 動作時間或警示結束代替原服命中證據。
    }

    [Fact]
    public void EmptyLogicMayaStateSkillIsIncludedButNoDamageCallbackIsInvented()
    {
        var data = Data();
        Row(data, "NPCConfig", 5300006, new { _iID = 5300006, _iAISpecial = "MainStory_Maya" });
        Skill(data, 5360601, "");
        var skill = Assert.Single(new BossSkillSets(data, new BossSkillOptions()).For(5300006, 0)!.Skills);
        Assert.Equal(5360601, skill.Id);
        Assert.True(skill.TargetsSelf);
        Assert.Empty(skill.HitMs);
    }

    [Fact]
    public void NormalSpecialsKeepTheirExistingHitSchedule()
    {
        var data = Data();
        Row(data, "NPCConfig", 991001, new { _iID = 991001, _kSkills = new[] { 991101 } });
        Skill(data, 991101, "SyntheticSpecial");
        var skill = Assert.Single(new BossSkillSets(data, new BossSkillOptions()).For(991001, 0)!.Skills);
        Assert.False(skill.TargetsSelf);
        Assert.NotEmpty(skill.HitMs);
    }

    [Fact]
    public void WarningAndCircleHaveDifferentWireIdentityAndOwnTheirPosition()
    {
        var origin = new Vec3 { X = 11, Y = 12, Z = 13 };
        var warning = CombatService.StoryPecoEffect(77, origin, 81, warning: true);
        var circle = CombatService.StoryPecoEffect(77, origin, 81, warning: false);
        Assert.Equal((77L, 77L, 77L, 5370202, 6, 81f),
            (warning.SourceId, warning.TargetId, warning.MasterId, warning.EffectId, warning.EffectType, warning.Angle));
        Assert.Equal((0L, 0L, 77L, 5370203, 2, 0f),
            (circle.SourceId, circle.TargetId, circle.MasterId, circle.EffectId, circle.EffectType, circle.Angle));
        Assert.Equal(5370201, circle.SkillId);
        origin.X = 99;
        Assert.Equal(11, warning.TargetPos.X);
        Assert.Equal(11, circle.TargetPos.X);
        warning.TargetPos.X = 33;
        Assert.Equal(11, circle.TargetPos.X);
    }
}
