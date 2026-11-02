using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    // ---- item use: requirements, use-time limits and award boxes ----

    /// <summary>The realm clock of the 05:00 use-time periods; ItemBoxService assigns the host's TimeProvider.</summary>
    internal TimeProvider UseClock { get; set; } = TimeProvider.System;

    // ErrorCodeConfig 157 "Uses: Insufficient", 172 "Level too low", 173 "Wrong Job", 174 "Gender mismatch".
    internal const int ErrUseTimes = 157, ErrLevelNeed = 172, ErrJobNeed = 173, ErrSexNeed = 174;
    private const int SubTypeChooseGift = 303, SubTypeSelfChoosePet = 912;

    /// <summary>
    /// DBItemsInfo.useTimes key 0 (no ItemConfig row; the client's GetUseTimeType skips it) holds the game day the counts
    /// were last written on, so a period that has ended counts as zero without a separate column.
    /// </summary>
    private const int UseTimesStampKey = 0;

    /// <summary>
    /// Refusals the client data names for a 31323 use. LC_ItemHelper.CanUseByInfo: level &lt; _iLevelNeed, _kJobNeed
    /// without 0 or the career, _iSexNeed other than 0 and the role's sex (the client only greys the item out; the
    /// server refusal and its codes are inferred). The scene's item limit: <see cref="ItemMapLimit"/>. _kUseTimes
    /// [period, limit] (enum_ItemUseLimit 1 day, 2 week, 3 month) caps the units used per period (GetItemUseTimeLimited).
    /// </summary>
    private int ItemRuleError(GameSession session, JsonElement row, int entry, int count)
    {
        var p = session.Progress!;
        if (p.Level < GameData.Int(row, "_iLevelNeed")) return ErrLevelNeed;
        var jobs = Numbers(row, "_kJobNeed");
        if (jobs.Length > 0 && !jobs.Contains(0) && !jobs.Contains(p.Career)) return ErrJobNeed;
        if (GameData.Int(row, "_iSexNeed") is var sex and not 0 && sex != session.Role!.Sex) return ErrSexNeed;
        if (!ItemMapLimit.Allows(data, session.SceneEntry, row)) return ErrorCodes.ItemUseAreaForbidden;
        var (period, limit) = UseTimesRule(row);
        if (period != 0 && CurrentUseTimes(session.BagState.Info).GetValueOrDefault(entry) + (long)count > limit)
            return ErrUseTimes;
        return 0;
    }

    private static long[] Numbers(JsonElement row, string field) =>
        row.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0))] : [];

    private static (int Period, int Limit) UseTimesRule(JsonElement row) =>
        Numbers(row, "_kUseTimes") is [var period and (1 or 2 or 3), var limit, ..] ? ((int)period, (int)limit) : (0, 0);

    private DateTime UseDay() => DailyActivityService.GameDay(UseClock.GetLocalNow().DateTime);

    private static int DayNumber(DateTime day) => (day.Date - new DateTime(1970, 1, 1)).Days;

    /// <summary>
    /// Day: the 05:00 game day; week: from Monday 05:00 (both Lua_TimeHelper refresh times, as DailyActivityService);
    /// month: from the 1st at 05:00 (inferred, as the shop's monthly limits).
    /// </summary>
    private static bool SamePeriod(int period, DateTime a, DateTime b) => period switch
    {
        1 => a.Date == b.Date,
        2 => WeekOf(a) == WeekOf(b),
        _ => a.Year == b.Year && a.Month == b.Month,
    };

    /// <summary>The Monday of a game day's week (the days are already shifted to 05:00, unlike GameWeek's input).</summary>
    private static DateTime WeekOf(DateTime gameDay) => gameDay.Date.AddDays(-(((int)gameDay.DayOfWeek + 6) % 7));

    /// <summary>The counts of the periods still running: what 5009 useTimes shows and the limit checks.</summary>
    private Dictionary<int, int> CurrentUseTimes(DBItemsInfo info)
    {
        var result = new Dictionary<int, int>();
        if (!info.UseTimes.TryGetValue(UseTimesStampKey, out int stamp)) return result;
        var written = new DateTime(1970, 1, 1).AddDays(stamp);
        var today = UseDay();
        foreach (var (entry, used) in info.UseTimes)
            if (entry != UseTimesStampKey && used > 0 && data.Row("ItemConfig", entry) is { } row
                && UseTimesRule(row) is { Period: not 0 } rule && SamePeriod(rule.Period, written, today))
                result[entry] = used;
        return result;
    }

    /// <summary>Counts <paramref name="count"/> more units into the staged bag state; returns the item's new count (0: unlimited).</summary>
    private int StageUseTimes(DBItemsInfo stored, JsonElement row, int entry, int count)
    {
        if (UseTimesRule(row).Period == 0) return 0;
        var current = CurrentUseTimes(stored);
        current[entry] = current.GetValueOrDefault(entry) + count;
        stored.UseTimes.Clear();
        foreach (var (key, used) in current) stored.UseTimes[key] = used;
        stored.UseTimes[UseTimesStampKey] = DayNumber(UseDay());
        return current[entry];
    }

    /// <summary>5009 useTimes after a period rollover; the removed keys reach the client as deletions.</summary>
    internal async Task SyncUseTimesAsync(GameSession session)
    {
        using var _ = await LockAsync(session);
        if (session.Role != null && session.Progress != null) await SyncItemsAsync(session);
    }

    internal bool HasUseTimes(GameSession session) => session.BagState.Info.UseTimes.Count > 1;

    /// <summary>
    /// A usable ItemConfig row whose server content is its _iAward (MultiverseGroupConfig): no use effect, or only
    /// client-hosted ones (1300103's [142,...] PathFindingNormal, played by the client after the reply).
    /// </summary>
    internal bool IsAwardBox(JsonElement row) => GameData.Int(row, "_iIsUse") == 1 && GameData.Int(row, "_iAward") > 0
        && GameData.Int(row, "_iSubType") != SubTypeSelfChoosePet && UseActions(row).All(a => a == 0 || ClientHosted(a));

    internal bool IsChoiceBox(JsonElement row) => IsAwardBox(row) && GameData.Int(row, "_iSubType") == SubTypeChooseGift;

    /// <summary>LC_ItemHelper._useItemForBox: a 303 box sends selfChooseGift, a _kAwardperformance box isFirstHeartBox.</summary>
    private bool CarriesBoxParam(JsonElement row, ItemUseReq req) => IsAwardBox(row) && req.ItemParam.Count == 1
        && req.ItemParam[0].VarCase is ItemUseParamInfo.VarOneofCase.SelfChooseGift or ItemUseParamInfo.VarOneofCase.IsFirstHeartBox;

    /// <summary>31322 for an opened box: one entry per item and binding, each new equipment instance with its roll.</summary>
    internal GetRewardNotify BoxRewardNotify(GameSession session, IReadOnlyList<(int Entry, int Count, int Bind)> goods,
        IReadOnlyList<InvItem> granted, int manifestType)
    {
        var notify = new GetRewardNotify { TargetUid = session.Role!.Id, RewardType = { manifestType } };
        foreach (var (entry, count, bind) in goods)
        {
            if (IsEquipment(entry))
                foreach (var item in granted.Where(i => i.Entry == entry && i.BindType == bind)) notify.Rewards.Add(ItemInfo(item));
            else notify.Rewards.Add(ItemInfo(new InvItem { Entry = entry, Stack = count, BindType = bind }));
        }
        return notify;
    }
}
