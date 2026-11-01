using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>
    /// 千星逐月為指定方向前方矩形。長18m有配對說明、模板射程共同支持；寬度沿SkillConfig的6m，
    /// 與原指示器／說明4m不一致，4～6m帶的原服命中語義仍UNKNOWN，不能以此LAB邊界測試當原服驗收。
    /// 只修本族，其他Arrow／矩形技能不由本規則推定。
    /// </summary>
    internal static bool? VolleyArrowAreaContains(JsonElement row, Vec3 origin, float face, Vec3 point)
    {
        if (new SkillConfig(row).ScriptLogic != "VolleyArrows") return null;
        var range = Numbers(row, "_kRange");
        if (range.Length < 2 || range[0] <= 0 || range[1] <= 0) return false;
        var (forward, side) = FacingOffset(point.X - origin.X, point.Z - origin.Z, face);
        return forward >= 0 && forward <= range[0] / 100 && Math.Abs(side) <= range[1] / 200;
    }

    /// <summary>
    /// 配對140083 skill_13320／133201的0點不計傷害（inferred）：mode0剩7點×P1符合1120～1400%總傷；
    /// mode1前10點×P1符合1600～2000%，末點P1[1]=15000對應11336301「結束時一段150%」。
    /// 採原模板各點而不自行加一秒；動畫到結算的時點映射沿既有ImpactOffset，仍是inferred。
    /// ShowDmg=2只是每段分割飄字，不能再加倍實扣。0點保留起始Buff1，不重複刷新。
    /// </summary>
    private static AuthoredDamageStage[] VolleyArrowStages(SkillConfig row, double[] points)
    {
        if (row.ScriptLogic != "VolleyArrows" || points.Length == 0 || points[0] != 0) return [];
        bool enhanced = row.Mode == 1;
        int pulses = enhanced ? 10 : 7;
        if (row.Mode is not (0 or 1) || points.Length != pulses + (enhanced ? 2 : 1)
            || row.DamageParam1.Length != (enhanced ? 2 : 1)) return [];
        int split = (int)Math.Max(1, row.ShowDmg.FirstOrDefault(1));
        return [.. points.Skip(1).Select((point, index) =>
            new AuthoredDamageStage(point, enhanced && index == pulses ? 1 : 0, split))];
    }

    // 初始化只套本列Buff1；不走傷害、CastResolved、成本、CD或完成事件。
    private async Task PrepareVolleyArrowsAsync(GameSession source, ActiveSkillCast cast)
    {
        if (new SkillConfig(cast.Row).ScriptLogic != "VolleyArrows") return;
        if (Now < cast.StartMs)
        {
            ScheduleCastDeadline(cast, 0, () => PrepareVolleyArrowsAsync(source, cast));
            return;
        }
        foreach (var tuple in Tuples(cast.Row, "_kBuff1"))
        {
            if (!Live(source, cast)) return;
            if (RollBuff(tuple, out int id, out int duration) && SkillBuffAllowed?.Invoke(source, source, id) != false)
                await _buffs.AddSupportAsync(source, id, duration, cast.OwnerId);
        }
    }
}
