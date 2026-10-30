namespace Ro3Emu.Server.Game;

/// <summary>
/// Season activity targets read from other services' state (TaskConfig type 13, accepted while their activity is open,
/// TaskService.ActivityTasks.cs). Neither has a client formatter (LC_ConditionDefine), so R is the last parameter, the
/// number of the ProgressConfig text:
/// - 106609 [n] "Earn a total of ${1} Guild Points in the Territory War" (120006-120010, Realm Campaign 107, beside 106610
///   Battle Merit): the role's guild's points this Territory War season, every round's battle scores summed
///   (TerritoryWarsService.GuildWarPoints; the client's GuildScore is the same sum for one round, the season total is
///   inferred). Read again when a territory battle the role fought in settles, and at every other refresh.
/// - 107101 [phase, pct] "Reach ${2}% completion for ${1}" (2101001-2101004, Genesis Movement 111): the ExplorePhaseConfig
///   phase's DBWorldExplorationPhase.totalRate, which LC_SeasonExplorationHelper.GetChapterProgress shows divided by 100
///   (whole percent here). Read again after every committed note fill (NewActivityService).
/// </summary>
public sealed partial class TaskService
{
    private const int TGuildWarPoints = 106609, TMovement = 107101;

    /// <summary>106609: the season points of the role's guild in the Territory War, -1 when unknown.</summary>
    public Func<GameSession, long>? GuildWarPoints { get; set; }

    private static long EventTargetRequired(long[] t) => t switch
    {
        [TGuildWarPoints, var n] => n,
        [TTrainingChallenge, var n] => n,
        [TMovement, > 0, var n] => n,
        _ => MiscRequired(t), // TaskService.MiscTargets.cs
    };

    private long EventTargetValue(GameSession s, long[] t) => t switch
    {
        [TGuildWarPoints, _] => GuildWarPoints?.Invoke(s) ?? -1,
        [TMovement, var phase, _] => MovementRate(s, phase) is var rate and >= 0 ? rate / 100 : -1,
        _ => MiscPassive(s, t), // TaskService.MiscTargets.cs
    };

    /// <summary>A World Exploration phase's totalRate (basis points), -1 before the activity's state exists.</summary>
    private static long MovementRate(GameSession s, long phase) =>
        s.NewActivities?.ActivityInfo.Values.Select(a => a.ActivityParam?.WorldExploration)
            .FirstOrDefault(w => w?.PhaseInfos.ContainsKey(phase) == true) is { } world ? world.PhaseInfos[phase].TotalRate : -1;

    /// <summary>TerritoryWarsService.BattleSettled: the guild's season points moved (106609).</summary>
    public void OnGuildWarScored(long role)
    {
        if (_sessions.All().FirstOrDefault(s => s.Role?.Id == role) is { } session) QueueRefresh(session);
    }
}
