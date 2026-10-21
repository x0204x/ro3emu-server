using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    /// <summary>Dungeon victory is the authority for a Journey boss act, independent of a client completion request.</summary>
    internal Func<GameSession, JourneyVictory, Action<RoleItemPlanBuilder>, Action, Task<bool>>? JourneyWon { get; set; }
    internal Func<GameSession, bool>? CanStageJourneyVictory { get; set; }

    /// <summary>The exact act and activity generation earned by the frozen dungeon source. A later
    /// activity cycle must never consume this older victory or draw its result again.</summary>
    public sealed record JourneyVictory(string Source, long SceneEntry, long ActId, long NodeId,
        long ActivityStart, long ActivityEnd, long PlayBook);

    private JourneyVictory? CaptureJourneyVictory(GameSession s, long sceneEntry, string source)
    {
        if (CanStageJourneyVictory?.Invoke(s) != true
            || s.NewActivities?.ActivityInfo.TryGetValue(112, out var activity) != true
            || activity.ActivityParam?.Journey is not { } journey) return null;
        foreach (long id in journey.CurActIds.Keys)
            if (_data.Row("JourneyActConfig", id) is { } row
                && row.TryGetProperty("_iAction", out var actions) && actions.ValueKind == JsonValueKind.Array
                && actions.GetArrayLength() > 0 && actions[0].ValueKind == JsonValueKind.Array
                && actions[0].GetArrayLength() >= 3 && GameData.Number(actions[0][0]) == 407
                && GameData.Number(actions[0][1]) == sceneEntry
                && _functions.ConditionMet(s, GameData.Int(row, "_iCondition")))
                return new(source, sceneEntry, id, journey.CurNodeId, activity.CurStartTime, activity.CurEndTime, activity.Playbook);
        return null;
    }

    /// <summary>Drain after activities are loaded, or after the personal dungeon delivery. Failure keeps
    /// the original source; the Journey award and acknowledgement must use the same transaction.</summary>
    public async Task DeliverJourneyPendingAsync(GameSession s)
    {
        if (s.Role == null || s.Dungeons == null || !s.ActivitiesReady || JourneyWon == null) return;
        using var scope = await _owners.EnterAsync("dungeon/journey-followup", Ownership.ReservationKey.Role(s.Role.Id));
        if (s.Owner is { } owner) scope.Context.Pin(owner);
        foreach (var victory in s.Dungeons.PendingJourney.Values.ToArray())
        {
            var next = Copy(s.Dungeons);
            if (!next.PendingJourney.Remove(victory.Source)) continue;
            try
            {
                bool delivered = await JourneyWon(s, victory,
                    tx => tx.RoleBlob(s.Role.Id, "dungeons", JsonSerializer.SerializeToUtf8Bytes(next)),
                    () => s.Dungeons = next);
                if (!delivered) _log.LogWarning("Journey continuation {Source} requires recovery for role {Role}; its captured activity or act no longer matches",
                    victory.Source, s.Role.Id);
            }
            catch (Exception e)
            {
                _log.LogError(e, "Journey continuation {Source} remains pending for role {Role}", victory.Source, s.Role.Id);
                if (s.Owner?.State == Ownership.RoleOwnerState.RecoveryRequired) throw;
            }
        }
    }

    private bool JourneyCanEnter(GameSession session, long sceneEntry)
    {
        if (session.NewActivities == null || !session.NewActivities.ActivityInfo.TryGetValue(112, out var activity)
            || activity.ActivityParam?.Journey is not { } journey || session.SceneLoading || session.Role == null)
            return false;
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        if (activity.IsShielded || activity.State == "_close"
            || now < activity.CurStartTime || now >= activity.CurEndTime) return false;
        foreach (long actId in journey.CurActIds.Keys)
        {
            if (_data.Row("JourneyActConfig", actId) is not { } row
                || !row.TryGetProperty("_iAction", out var actions) || actions.ValueKind != JsonValueKind.Array
                || actions.GetArrayLength() == 0 || actions[0].ValueKind != JsonValueKind.Array
                || actions[0].GetArrayLength() < 3 || GameData.Number(actions[0][0]) != 407
                || GameData.Number(actions[0][1]) != sceneEntry) continue;
            long npc = (long)(GameData.Number(actions[0][2]) ?? 0);
            return _data.Table("NpcLevelPointRespawnConfig").Values.Any(r =>
                GameData.Int(r, "_iMapid") == sceneEntry && GameData.Int(r, "_iNpcid") == npc);
        }
        return false;
    }

    /// <summary>Replays a settled solo Journey encounter through normal 30401/scene-load/monster settlement.</summary>
    public async Task<int> RestartJourneyAsync(GameSession session)
    {
        if (session.DungeonAttempt is not { } prior || !prior.Settled
            || session.InstanceId != prior.Room.Id || prior.Room.Closed
            || GameData.Int(prior.Plan.Config, "_iGamePlayType") != 24
            || prior.Room.ActiveMembers.Count != 1 || !JourneyCanEnter(session, prior.Plan.SceneEntry))
            return ErrorCodes.DungeonBusy;
        var plan = _catalog.Resolve(new EnterDungeonReq { SceneEntry = prior.Plan.SceneEntry });
        if (plan == null || plan.Waves.Count == 0) return ErrorCodes.DungeonDataMissing;
        if (session.Dead) await _combat.ReviveAsync(session, new EntityReliveReq());
        int admission = Admission(session, plan, continuation: true);
        if (admission != 0) return admission;
        await BeginAsync(plan, [session], session.Role!.Id, prior.TeamId);
        return session.DungeonAttempt is { } restarted && !ReferenceEquals(restarted, prior)
            && session.InstanceId == restarted.Room.Id ? 0 : ErrorCodes.SceneMissing;
    }
}
