using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>16318 的指定方向15×10m與配對指示器[10,15]相符；不改其他Rect360技能。</summary>
    internal static bool? HighAltitudeBombingAreaContains(JsonElement row, Vec3 origin, float face, Vec3 point)
    {
        var skill = new SkillConfig(row);
        if (skill.SkillId != 16318 || skill.Mode != 0 || skill.ScriptLogic != "HighAltitudeBombing") return null;
        var range = Numbers(row, "_kRange");
        if (range.Length < 2 || range[0] <= 0 || range[1] <= 0) return false;
        var (forward, side) = FacingOffset(point.X - origin.X, point.Z - origin.Z, face);
        return forward >= 0 && forward <= range[0] / 100 && Math.Abs(side) <= range[1] / 200;
    }

    /// <summary>
    /// inferred：0.0167點為Effect1631822起飛隱藏事件，後七點對應七次260～300%，
    /// 與說明1820～2100%總傷相符。ShowDmg=3僅拆飄字，不能乘三次實扣。
    /// 本列Buff1為空；不把同號、實屬16312降CD的Buff1631801錯套為起飛狀態。
    /// </summary>
    private static AuthoredDamageStage[] HighAltitudeBombingStages(SkillConfig row, double[] points)
    {
        if (row.SkillId != 16318 || row.Mode != 0 || row.ScriptLogic != "HighAltitudeBombing"
            || row.DamageParam1.Length != 1 || points.Length != 8 || points[0] >= .02) return [];
        int split = (int)Math.Max(1, row.ShowDmg.FirstOrDefault(1));
        return [.. points.Skip(1).Select(point => new AuthoredDamageStage(point, 0, split))];
    }
}
