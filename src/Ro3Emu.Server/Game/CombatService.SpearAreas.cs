using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    // 11310普通/強化、11316說明給前方寬×長；原_kRange=[0]不是零半徑圓。
    // (inferred) 前方矩形沿描述尺寸；LogicData的padding與-500未解讀，不能當成原服邊界。
    // UltimateSpears暫沿強化基底10×20m，尚未還原原文「多方向疊加」的幾何，不能稱完整原服形狀。
    internal static bool? SpearAreaContains(JsonElement row, Vec3 origin, float face, Vec3 point)
    {
        var skill = new SkillConfig(row);
        if (skill.ScriptLogic is not ("HundredSpears" or "UltimateSpears" or "Sonicblast")) return null;
        var desc = skill.DescData;
        if (desc.Length < 2 || !double.TryParse(desc[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double width)
            || !double.TryParse(desc[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double length)
            || width <= 0 || length <= 0) return false;
        var (forward, side) = FacingOffset(point.X - origin.X, point.Z - origin.Z, face);
        return forward >= 0 && forward <= length && Math.Abs(side) <= width / 2;
    }
}
