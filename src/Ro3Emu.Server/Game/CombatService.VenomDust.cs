using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    // 原15106描述為目標點周圍3m最多5敵；配對模板Indicator7/[3,3]、圓形背景與8m施放距離。
    // RangeType1在這一族不是單體；Release8仍沿既有地面瞄點驗證與共同PvE/PvP人數上限。
    private static bool AuthoredVenomDustArea(JsonElement row) =>
        new SkillConfig(row) is { SkillId: 15106, Release: 8 };
}
