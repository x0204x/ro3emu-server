using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>保留既有內容測試入口；矩形方向、長寬與邊界餘裕統一沿用上游判定。</summary>
    internal static bool? VolleyArrowAreaContains(JsonElement row, Vec3 origin, float face, Vec3 point)
    {
        if (new SkillConfig(row).ScriptLogic != "VolleyArrows") return null;
        return SkillAreaContains(row, origin, origin, face, point);
    }
}
