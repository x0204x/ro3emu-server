using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The growth manual ("Growth Emblem", NewbieTitlesConfig): seven titles, each a Chapter task (TaskConfig type 8,
/// targets [104004, child] for every child) with its ChapterChild tasks (type 7) in _iSubTaskId. All of them are
/// _kAcceptTask [1] with an empty _kPreTask and _kDeliverTask [3] (claimed in the panel with 31205), so the pre-task
/// graph never offers them. The main story needs them: 10104100 and 10105030 wait for [104004, 701990] / [104004,
/// 702990] ("Advance a Growth Emblem once") and track the panel (_kTrack [1, TOPBAR, UI_Sys_GrowthManualPopPanel]).
///
/// The panel shows the first title, in _iLev order, whose chapter task is not delivered and whose previous title's is,
/// reading the chapter and child tasks from the task dataset (re/clientB/lua-src/UI/GrowthManual/Controllers/
/// UI_Sys_GrowthManualPopWidgetCtrl.lua:476-560, chain from Config/CFG/Lua_CFG_NewbieTitlesConfig.lua:47-190,366-376).
/// So the server offers exactly that title: its chapter and child tasks are accepted together, the first one at once
/// (the rows' own auto accept with no prerequisite, like the story root) and each next one after the previous chapter is
/// claimed. A chapter whose _iCondition is unmet is still offered, flagged isChapterLocked: the panel then shows the
/// condition instead of the claim (UI_Sys_GrowthManualPopWidgetCtrl.lua:613-623,744-770), and the claim is refused.
/// </summary>
public sealed partial class TaskService
{
    private List<(long Chapter, long[] Children)>? _growthTitles;

    /// <summary>NewbieTitlesConfig rows in _iLev order: chapter task and child tasks.</summary>
    private List<(long Chapter, long[] Children)> GrowthTitles() => _growthTitles ??= _data.Table("NewbieTitlesConfig").Values
        .Where(r => r.ValueKind == System.Text.Json.JsonValueKind.Object && GameData.Int(r, "_iTaskID") > 0)
        .OrderBy(r => GameData.Int(r, "_iLev")).ThenBy(r => GameData.Int(r, "_iID"))
        .Select(r => (GameData.Int(r, "_iTaskID"), Longs(r, "_iSubTaskId")))
        .Where(t => Defs().ContainsKey(t.Item1)).ToList();

    /// <summary>Offers the current growth title (see the class remarks); returns how many tasks were accepted.</summary>
    private int AcceptGrowthTitle(GameSession session)
    {
        int accepted = 0;
        foreach (var (chapter, children) in GrowthTitles())
        {
            if (IsDelivered(session, chapter)) continue;
            foreach (long id in children.Append(chapter))
            {
                if (!Defs().ContainsKey(id) || Active(session, id) || IsDelivered(session, id)) continue;
                Accept(session, id);
                accepted++;
            }
            if (session.Tasks!.Infos.TryGetValue(chapter, out var info))
                info.IsChapterLocked = Defs()[chapter].Condition != 0 && !_functions.ConditionMet(session, Defs()[chapter].Condition);
            break;
        }
        return accepted;
    }

    /// <summary>A claim of a locked chapter is refused: its condition is what the panel shows instead.</summary>
    private static bool ChapterLocked(TaskInfo info) => info.IsChapterLocked;
}
