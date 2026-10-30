using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    /// <summary>
    /// GMJobProfessAttr 171: the active stat plan takes a profession's recommended stats at the role's base level,
    /// JobProfessPointConfig[profession * 1000 + level], the row the client's own auto add-point reads
    /// (LC_DAL_PlayerInfo with Lua_CFG_JobProfessPointConfig.GetAddPointConfigByProfessionId). (inferred) The values are
    /// absolute, a reset and an allocation in one step; without a row at the level the nearest one below it; refused
    /// when the role's points do not cover them.
    /// </summary>
    internal async Task<string> SetProfessionStatsAsync(GameSession session, long profession)
    {
        using var _ = await LockAsync(session);
        var p = CloneProgress(session.Progress!);
        var row = Enumerable.Range(1, p.Level).Reverse().Select(level => JobProfessPointConfig.Get(data, profession * 1000 + level))
            .FirstOrDefault(r => r != null);
        if (row is not { } build) return $"Profession {profession} has no recommended stats up to level {p.Level}.";
        int[] stats = build.Stats;
        if (stats.Any(v => v is < 1 or > MaxStat)) return $"Recommended stats {string.Join('/', stats)} are out of range.";
        if (stats.Sum(StatCost) > TotalPoints(p)) return $"Recommended stats {string.Join('/', stats)} cost more than the role's {TotalPoints(p)} points.";
        stats.CopyTo(p.Stats, 0);
        var plans = session.StatPlans.Info.Clone();
        plans.AllPlanBaseProperty[plans.CurPlanIndex] = CurrentPlan(p, plans.AllPlanBaseProperty[plans.CurPlanIndex]);
        await CommitStatsAsync(session, "gm-profession-stats", p, plans);
        await SendStatPlanAsync(session);
        await SendStatsAsync(session, showChange: true);
        foreach (var handler in ProgressHandlers) await handler(session);
        foreach (var handler in StateChanged) handler(session);
        return $"Stats {string.Join('/', stats)} (profession {profession}, level {build.BaseLv}), {TotalPoints(p) - UsedPoints(p)} points left.";
    }
}
