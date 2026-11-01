using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    // 配對skill15303模板AtkRange=5／Indicator[2,5]；技能描述、release2、TargetMax3
    // 均為自身圓範圍。該族原表RangeType1不能沿通用鎖定分支只打TargetId。
    // 15306的ScriptShow15406配對模板亦為circle_middle、Indicator[5,5]，原文5m最多5敵。
    // 共鳴mode1沿原列半徑6m；只修這兩個有交叉證據家族，不擴其它RangeType1技能。
    private static bool AuthoredAssassinSelfArea(JsonElement row) =>
        new SkillConfig(row) is { SkillId: 15303 or 15306, Release: 2 };
}
