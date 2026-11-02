using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The level cap and the server level (BaseLvLimitConfig, BaseExpAddConfig).
/// <para>
/// The original caps the base level by server day: BaseLvLimitConfig[10000+day]._iBaseLimit, 39 on day 1 up to 99 from
/// day 36 (its _iLvLimitCorrection scales the EXP gained past the day's cap). Owner decision (2026-10-17): no
/// day-based cap under the all-open policy; the cap is the table's highest _iBaseLimit, 99, which is also what the
/// client treats as the end (Lua_CFG_BaseLvLimitConfig.GetMaxBaseLimit; UI_Sys_RoleInfoWidgetCtrl shows "99 == level"
/// as a full bar). Paragon opens at 99. A role saved above the cap keeps its level: EXP never lowers it.
/// </para>
/// <para>
/// The server ("world") level is BaseLvLimitConfig[10000+day]._iServerLv, day = floor((now - open) / 86400) + 1 as
/// LC_DAL_ServerInfo.PackServerBasicInfo computes iOpenDayCount from 30130 / RoleAssets.server_info; a missing day row
/// is level 0 (Lua_CFG_BaseLvLimitConfig.GetWorldLevel). Under all-open the day still advances, so the bonus does too.
/// BaseExpAddConfig: a role below the server level gets _iPursueExpAdditionPercent (per 10000) more Base EXP, looked up
/// as UI_Sys_RoleTipsPanelCtrl does (Lua_CFG_BaseExpAddConfig.GetPercent(level - serverLevel): the first listed gap
/// at or above the role's, none above the highest = 0): +20% at 5 levels below up to +400% from 20 below. The client
/// computes the shown percent itself from the same server info, so nothing extra is sent.
/// </para>
/// </summary>
public sealed partial class CharacterService
{
    /// <summary>The server's open day (1 = the opening day); set by FunctionService, which owns the season clock.</summary>
    internal Func<int>? ServerDay { get; set; }

    private int _maxLevel;
    private int[]? _serverLevels;
    private (long[] Gaps, long[] Percents)? _pursue;

    /// <summary>Highest base level EXP can reach: the highest BaseLvLimitConfig._iBaseLimit (99).</summary>
    public int MaxLevel => _maxLevel > 0 ? _maxLevel : _maxLevel = ComputeMaxLevel();

    private int ComputeMaxLevel()
    {
        int cap = 0;
        foreach (var row in data.Table("BaseLvLimitConfig").Values) cap = Math.Max(cap, (int)GameData.Int(row, "_iBaseLimit"));
        if (cap > 0) return cap;
        // No limit table: the first BaseLvConfig level that needs no EXP (the table's last level).
        cap = int.MaxValue;
        foreach (var row in data.Table("BaseLvConfig").Values)
            if (GameData.Int(row, "_iBaseExp") == 0) cap = Math.Min(cap, (int)GameData.Int(row, "_iID"));
        return cap == int.MaxValue ? 100 : cap;
    }

    /// <summary>BaseLvLimitConfig[10000+day]._iServerLv for the current server day; 0 without a row or a clock.</summary>
    public int ServerLevel() => ServerDay is { } day ? ServerLevelOn(day()) : 0;

    internal int ServerLevelOn(int day)
    {
        var levels = _serverLevels ??= BuildServerLevels();
        return day >= 1 && day < levels.Length ? levels[day] : 0;
    }

    private int[] BuildServerLevels()
    {
        int last = 0;
        foreach (var row in data.Table("BaseLvLimitConfig").Values) last = Math.Max(last, (int)GameData.Int(row, "_iDay"));
        var levels = new int[last + 1];
        // Keyed as the client looks rows up: 10000 + day.
        for (int day = 1; day <= last; day++)
            levels[day] = data.Row("BaseLvLimitConfig", 10000 + day) is { } row ? (int)GameData.Int(row, "_iServerLv") : 0;
        return levels;
    }

    /// <summary>BaseExpAddConfig catch-up percent (per 10000) for a role <paramref name="level"/> at a server level.</summary>
    public long PursueExpPercent(int level, int serverLevel)
    {
        var (gaps, percents) = _pursue ??= BuildPursue();
        long gap = (long)level - serverLevel;
        // Lua_CFG_BaseExpAddConfig._lowerBound: the first sorted gap >= the role's; past the last one, none.
        int lo = 0, hi = gaps.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (gaps[mid] < gap) lo = mid + 1; else hi = mid;
        }
        return lo < gaps.Length ? percents[lo] : 0;
    }

    private (long[] Gaps, long[] Percents) BuildPursue()
    {
        var rows = data.Table("BaseExpAddConfig").Values
            .Select(r => (Gap: GameData.Int(r, "_iBaseLvGap"), Percent: GameData.Int(r, "_iPursueExpAdditionPercent")))
            .OrderBy(r => r.Gap).ToArray();
        return (rows.Select(r => r.Gap).ToArray(), rows.Select(r => r.Percent).ToArray());
    }

    /// <summary>
    /// Base EXP with the catch-up bonus for a role at <paramref name="level"/>. Applied to monster kills and direct EXP
    /// grants (AddExpAsync); quest, mail and award EXP stay as configured *(inferred: the client names no source)*.
    /// </summary>
    public long WithPursueBonus(int level, long baseExp)
    {
        if (baseExp <= 0) return baseExp;
        long percent = PursueExpPercent(level, ServerLevel());
        return percent <= 0 ? baseExp : checked(baseExp + baseExp * percent / 10000);
    }

    /// <summary>
    /// Job EXP that comes with <paramref name="baseExp"/> of Base EXP: PublicParamConfig.BaseToJobConversionPercent per
    /// 10000, rounded down. *(inferred: the client's Lua never reads the parameter.)* The reading fits the tables: Job
    /// Advance needs Base 30 and Job 40 together (TransformJobLevelLimit), BaseLvConfig asks 1,377,405 EXP up to Base 30
    /// and JobLvConfig rank 2 asks 261,627 up to Job 40, which is 19%. It holds for every source of Base EXP, on the
    /// amount credited: with the catch-up bonus left out, a role below the server level would reach that Advance with
    /// a fraction of the Job EXP it asks. An award's own item 101 adds to it.
    /// </summary>
    public long JobExpFromBase(long baseExp)
    {
        if (baseExp <= 0 || !data.Table("PublicParamConfig").TryGetValue("BaseToJobConversionPercent", out var value)) return 0;
        return checked(baseExp * (long)(GameData.Number(value) ?? 0) / 10000);
    }
}
