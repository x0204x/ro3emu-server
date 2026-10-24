using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class LifeJobService
{
    private const int LifeLevelCondition = 11001; // LC_InteractCondition.CheckLifeLevel

    /// <summary>
    /// A gathering resource unlocks itself once its LifeCollectionConfig._iCondition holds. Every collection has
    /// _iMapIds, and for those the client's Unlock button (UI_Sys_Life_ContentWidgetCtrl.OnClickUnlock) only walks to
    /// the gathering area: it never sends StudyCollection, so Lv. 2 ore stayed locked at Mining 3 (#bug-reports
    /// "Level 3 mining does not grant access to level 2 resources", "Cannot unlock Lv.2 Gardener"). The conditions are
    /// the profession's own level (LC_ConditionList 11001 [job, 1, level]), read from <paramref name="next"/> so a
    /// level gained by this very gather counts before it is committed.
    /// </summary>
    private void UnlockEarnedCollections(GameSession s, DBLifeJobInfo next)
    {
        foreach (var row in _data.Table("LifeCollectionConfig").Values)
        {
            var c = new LifeCollectionConfig(row);
            long job = c.JobId;
            if (!Current(row) || !next.ActiveLifejob.Values.Contains(job) || !next.LifeJobInfoMap.TryGetValue(job, out var info)
                || info.JobLevel <= 0 || info.GatherInfo.ItemDetails.ContainsKey(c.CollectionId)
                || !EarnedBy(s, next, c.Condition)) continue;
            UnlockCollection(next, row);
        }
    }

    private bool EarnedBy(GameSession s, DBLifeJobInfo next, long conditionId)
    {
        if (conditionId == 0) return true;
        if (LCConditionList.Get(_data, conditionId)?.SubConditionList is not { ValueKind: JsonValueKind.Array } subs)
            return _functions.ConditionMet(s, conditionId);
        var levels = new List<(long Job, long Level)>();
        foreach (var sub in subs.EnumerateArray())
        {
            var condition = new LCConditionList(sub);
            var d = condition.Data;
            if (d.Length < 3 || d[0] != LifeLevelCondition || condition.Inverse)
                return _functions.ConditionMet(s, conditionId);
            levels.Add((d[1], d[2]));
        }
        return levels.All(l => next.ActiveLifejob.Values.Contains(l.Job) && next.LifeJobInfoMap.TryGetValue(l.Job, out var info)
            && info.JobLevel >= l.Level);
    }

}
