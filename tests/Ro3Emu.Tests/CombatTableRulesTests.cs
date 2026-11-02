using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Client tables the combat rules read: Job Resonance (JobResonance*Config), the totem counter (TotemRestraintConfig),
/// pet star scaling (PetAttrChangeConfig) and the Spirit Tower power suppression (PowerSuppressConfig).
/// </summary>
public sealed class CombatTableRulesTests
{
    private static readonly Lazy<GameData> Exported = new(() => new GameData(
        Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") }), NullLogger<GameData>.Instance));

    private static GameData Data => Exported.Value;

    private static RoleProgress Role(int career, params int[] skills)
    {
        var p = new RoleProgress { Career = career, Level = 99 };
        foreach (int entry in skills) p.Skills[entry / 100] = entry % 100;
        return p;
    }

    // ---- 4. Job Resonance ----

    [ClientDataFact]
    public void ResonanceIsTheFirstRowWhoseSkillLevelsTheCurrentPlanMeets()
    {
        var resonance = new JobResonance(Data);
        // Lord Knight 1400: row 1 (Shrined, 2) needs 11303/11301/11302/11314 at 5; row 2 (Blade Soul, 3) 11310/11316 at 5.
        Assert.Equal(2, resonance.ResonanceId(Role(1400, 1130305, 1130105, 1130205, 1131405)));
        Assert.Equal(3, resonance.ResonanceId(Role(1400, 1131005, 1131605)));
        Assert.Equal(2, resonance.ResonanceId(Role(1400, 1130305, 1130105, 1130205, 1131405, 1131005, 1131605)));
        Assert.Equal(JobResonance.Inactive, resonance.ResonanceId(Role(1400, 1130305, 1130105, 1130205, 1131404)));
        // High Priest 4400 / profess 431: Gospel (5); a Tetra Vortex 2 needs only level 2 (1231602, Warlock 2400).
        Assert.Equal(5, resonance.ResonanceId(Role(4400, 1430110, 1430310, 1431504, 1430501)));
        Assert.Equal(4, resonance.ResonanceId(Role(2400, 1230803, 1231003, 1231203, 1231403, 1231602, 1231501, 1231901)));
        // A career without rows (Novice, second jobs) has none.
        Assert.Equal(JobResonance.Inactive, resonance.ResonanceId(Role(1000)));
        Assert.Equal(JobResonance.Inactive, resonance.ResonanceId(Role(1300, 1130305, 1130105, 1130205, 1131405)));
    }

    [ClientDataFact]
    public void TheSkillSchoolFunctionGateHoldsForEveryResonanceJob()
    {
        // FunctionConfig 1001 SkillSchool opens at condition 2000103 = job rank >= 5; every _iJob is a rank-5 job.
        Assert.Equal(2000103, GameData.Int(Data.Row("FunctionConfig", 1001)!.Value, "_kOpenCondition"));
        var sub = Data.Row("LC_ConditionList", 2000103)!.Value.GetProperty("m_kSubConditionList")[0].GetProperty("m_kData");
        Assert.Equal([1019d, 5d], [.. sub.EnumerateArray().Select(v => v.GetDouble())]);
        foreach (var row in Data.Table("JobResonanceActiveConfig").Values)
            Assert.Equal(5, GameData.Int(Data.Row("JobConfig", GameData.Int(row, "_iJob"))!.Value, "_iJobRank"));
    }

    [ClientDataFact]
    public void ResonanceCountersAreAttackerRowTargetColumn()
    {
        var resonance = new JobResonance(Data);
        // Shrined > Blade Soul > Stellar Vortex > Shrined: +10% one way, -10% the other.
        Assert.Equal(11000, resonance.Multiplier(2, 3));
        Assert.Equal(9000, resonance.Multiplier(3, 2));
        Assert.Equal(11000, resonance.Multiplier(3, 4));
        Assert.Equal(11000, resonance.Multiplier(4, 2));
        Assert.Equal(9000, resonance.Multiplier(2, 4));
        // Inactive deals -10% to and takes +10% from every resonance; two inactive roles are even.
        Assert.Equal(9000, resonance.Multiplier(1, 5));
        Assert.Equal(11000, resonance.Multiplier(3, 1));
        Assert.Equal(10000, resonance.Multiplier(1, 1));
        // Gospel is neutral against the three, as UIWord 33238 says.
        Assert.Equal(10000, resonance.Multiplier(5, 2));
        Assert.Equal(10000, resonance.Multiplier(4, 5));
        Assert.Equal(10000, resonance.Multiplier(9, 2));
    }

    [ClientDataFact]
    public void GospelGivesHealingDoneAndEveryResonanceItsPower()
    {
        var resonance = new JobResonance(Data);
        Assert.Equal([(207L, 500d)], resonance.Attributes(5));
        Assert.Empty(resonance.Attributes(2));
        Assert.Empty(resonance.Attributes(JobResonance.Inactive));
        Assert.Equal("per_treat", Data.Row("AttributeConfig", 207)!.Value.GetProperty("_kVariable").GetString());
        Assert.Equal(500, resonance.Power(2));
        Assert.Equal(0, resonance.Power(JobResonance.Inactive));
    }

    // ---- 5. totem counter ----

    [ClientDataFact]
    public void TotemTypesCounterInOneCycle()
    {
        var totems = new TotemRestraint(Data);
        Assert.Equal(1, totems.TotemType(1400));
        Assert.Equal(2, totems.TotemType(5400));
        Assert.Equal(3, totems.TotemType(3400));
        Assert.Equal(4, totems.TotemType(4400));
        // 1 > 4 > 3 > 2 > 1, +20% each; nothing the other way or between equals.
        Assert.Equal(2000, totems.Bonus(1400, 4400));
        Assert.Equal(2000, totems.Bonus(4400, 3400));
        Assert.Equal(2000, totems.Bonus(3400, 5400));
        Assert.Equal(2000, totems.Bonus(5400, 1400));
        Assert.Equal(0, totems.Bonus(4400, 1400));
        Assert.Equal(0, totems.Bonus(1400, 1410));
        Assert.Equal(0, totems.Bonus(1400, 0));
    }

    [ClientDataFact]
    public void APlayerHitStacksTheResonanceAndTotemCounters()
    {
        var resonance = new JobResonance(Data);
        var totems = new TotemRestraint(Data);
        var shrined = Role(1400, 1130305, 1130105, 1130205, 1131405);   // resonance 2, totem 1
        var bladeSoul = Role(1400, 1131005, 1131605);                  // resonance 3, totem 1
        var gospel = Role(4400, 1430110, 1430310, 1431504, 1430501);   // resonance 5, totem 4
        Assert.Equal(1100, PlayerRestraints.Apply(resonance, totems, shrined, bladeSoul, 1000));
        Assert.Equal(900, PlayerRestraints.Apply(resonance, totems, bladeSoul, shrined, 1000));
        // Totem 1 beats 4 (+20%); Gospel is neutral for the resonance.
        Assert.Equal(1200, PlayerRestraints.Apply(resonance, totems, shrined, gospel, 1000));
        Assert.Equal(1000, PlayerRestraints.Apply(resonance, totems, gospel, shrined, 1000));
        // An inactive Novice deals -10% to a resonance holder; a miss stays a miss.
        Assert.Equal(900, PlayerRestraints.Apply(resonance, totems, Role(1000), bladeSoul, 1000));
        Assert.Equal(0, PlayerRestraints.Apply(resonance, totems, shrined, bladeSoul, 0));
    }

    [ClientDataFact]
    public void ATitleLevelAboveTheTargetsAddsDamageAndBelowItTakesSome()
    {
        var titles = new TitleDamage(Data);
        // TitleConfig 1 is level 1 (TitleBuffConfig 400), 6 level 2 (800), 21 level 5 (2000).
        Assert.Equal((0, 400, 800, 2000), (titles.Term(0), titles.Term(1), titles.Term(6), titles.Term(21)));
        Assert.Equal(1160, titles.Apply(21, 1, 1000));
        Assert.Equal(840, titles.Apply(1, 21, 1000));
        Assert.Equal(1000, titles.Apply(2, 6, 1000));
        Assert.Equal(1040, titles.Apply(1, 0, 1000));
        Assert.Equal(0, titles.Apply(21, 1, 0));
    }

    // ---- 6. pet star scaling ----

    [ClientDataFact]
    public void QualityFivePetsScaleHpAndAttackByTheirOwnCurves()
    {
        // Pet 30001 is quality 5: PetAttrChangeConfig star 1 = HP 22500 / ATK 11000, star 6 = HP 45000 / ATK 15400;
        // PetStarConfig._iBaseAttr (DEF and the rest) goes 10000 -> 20000.
        Assert.Equal(5, GameData.Int(Data.Row("PetBaseConfig", 30001)!.Value, "_iQuality"));
        var one = PetCombatRules.Attributes(Data, 30001, new PetData { PetId = 30001, Level = 12, Star = 1, StarProgress = 0 })!;
        var six = PetCombatRules.Attributes(Data, 30001, new PetData { PetId = 30001, Level = 12, Star = 6, StarProgress = 0 })!;
        Assert.Equal(2.0, six[101] / one[101], 6);
        Assert.Equal(1.4, six[103] / one[103], 6);
        Assert.Equal(1.4, six[104] / one[104], 6);
        Assert.Equal(2.0, six[105] / one[105], 6);
        Assert.Equal(new Dictionary<long, long> { [101] = 22500, [103] = 11000, [104] = 11000 },
            PetCombatRules.AttrChange(Data, 5, 1, 0));
        Assert.Null(PetCombatRules.AttrChange(Data, 5, 6, 1));
    }

    // ---- 7. Spirit Tower power suppression ----

    [ClientDataFact]
    public void TowerFloorsPickTheSuppressionBuffByPowerPercent()
    {
        var floor = Data.Row("PetFloorConfig", 1)!.Value;
        Assert.Equal(6000, GameData.Int(floor, "_iPowerRecommend"));
        Assert.Equal(new TowerService.Suppression(5000001, -1000, -1000), TowerService.Suppress(Data, floor, 0, 10000));
        Assert.Equal(new TowerService.Suppression(5000001, -1000, -1000), TowerService.Suppress(Data, floor, 4799, 10000));
        Assert.Equal(new TowerService.Suppression(5000101, -500, -500), TowerService.Suppress(Data, floor, 4800, 10000));
        Assert.Equal(new TowerService.Suppression(0, 0, 0), TowerService.Suppress(Data, floor, 6000, 10000));
        Assert.Equal(new TowerService.Suppression(5000201, 1000, 1000), TowerService.Suppress(Data, floor, 6600, 10000));
        // The daily reduction lowers the recommendation: 90% of 6000 = 5400.
        Assert.Equal(new TowerService.Suppression(0, 0, 0), TowerService.Suppress(Data, floor, 5400, 9000));
        Assert.All(Data.Table("PetFloorConfig").Values, f => Assert.Equal(1, GameData.Int(f, "_iPowerSuppress")));
    }
}
