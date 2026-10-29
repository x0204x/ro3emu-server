using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    /// <summary>
    /// LC_DunGeon_VarDefine 10021 PGuideWayList (a table of numbers, kProcessComp GuideGfx): the scene editor guide
    /// routes LC_SceneCom_GuideGfx draws as guide arrows (OnDungeonVarChanged -> _updateGuideWayList shows the listed
    /// routes and removes the others).
    /// </summary>
    internal const int GuideWayVar = 10021;

    /// <summary>
    /// A solo story scene's guide arrows: every route of its scene editor file (m_kGuidePoints) while the run is open,
    /// none once it is settled. (inferred) The original encounter scripts that set 10021 are not in the client, so when
    /// each route was shown is unknown; a scene's routes chain into one path (the Culvert plane's route 19 ends where
    /// 21 starts), so all of them are shown from the start. Null for scenes without routes and for group content.
    /// </summary>
    private DungeonVar? GuideWays(Attempt a)
    {
        if (a.Plan.Multi != null || a.GuildChallenge != null || a.Dracula != null
            || new DungeonConfig(a.Plan.Config).RoleCntLimit != 1) return null;
        if (_catalog.GuideRoutes(a.Plan.SceneEntry) is not { Count: > 0 } routes) return null;
        var ways = new DungeonVar { VarId = GuideWayVar };
        if (!a.Settled) ways.Vi64.AddRange(routes);
        return ways;
    }

    /// <summary>After the clear: 10021 with no routes, so the arrows go away (DungeonEventDungeonVarChange).</summary>
    private Task HideGuideWaysAsync(GameSession s, Attempt a) => GuideWays(a) is { } ways
        ? s.SendGameAsync(Event, new DungeonEventNotify
        {
            DungeonEntry = a.Plan.SceneEntry, EventType = (DungeonEventType)4, StageNum = a.Wave + 1, LevelNum = 1, Vars = { ways },
        })
        : Task.CompletedTask;
}
