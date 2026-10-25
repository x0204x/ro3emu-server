using Google.Protobuf;

namespace Ro3Emu.Server.Game;

public sealed partial class TaskService
{
    /// <summary>E05e: MVP target counters share the settled source; normal task settlement resumes durable debt.</summary>
    internal CharacterService.FirstCardSightPlan? StageMvpParticipant(GameSession session, string source)
        => StageEventTarget(session, "mvp/" + source, target => target[0] is TMvp or TMvpOnce);

    /// <summary>The match count targets, plus (Survivor Battle) what the match brought to the weekly targets.</summary>
    internal CharacterService.FirstCardSightPlan? StageTimedBattleSettlement(GameSession session, int entry, string source,
        Func<long[], long>? weekly = null)
        => entry is 2 or 6 ? StageEventTarget(session, "timed-battle/" + source,
            target => TimedBattleTarget(target, entry) ? 1 : weekly?.Invoke(target) ?? 0) : null;

    internal CharacterService.FirstCardSightPlan? StageOnePieceReward(GameSession session, string source)
        => StageEventTarget(session, "one-piece/" + source,
            target => target[0] == TOnePieceCount && target.Length >= 4 && target[1] == 200);

    private CharacterService.FirstCardSightPlan? StageEventTarget(GameSession session, string source, Func<long[], bool> matches)
        => StageEventTarget(session, source, target => matches(target) ? 1 : 0);

    private CharacterService.FirstCardSightPlan? StageEventTarget(GameSession session, string source, Func<long[], long> amount)
    {
        if (session.Tasks == null || session.Progress == null) return null;
        session.Owners?.Current?.AssertRole(session.Role!.Id);
        var next = session.Tasks.Clone();
        bool changed = false;
        foreach (var info in next.Infos.Values.Where(i => i.Status == Running))
        {
            if (!Defs().TryGetValue(info.TaskId, out var def)) continue;
            for (int index = 0; index < def.Targets.Length && index < info.CompleteNum.Count; index++)
            {
                var target = def.Targets[index];
                long add = amount(target);
                if (add <= 0) continue;
                long required = Required(session, target);
                if (required <= 0) continue;
                long old = Math.Clamp(info.CompleteNum[index], 0, required);
                long value = Math.Min(required, old + add);
                changed |= value != info.CompleteNum[index];
                info.CompleteNum[index] = value;
            }
        }
        return !changed ? null : new(builder =>
        {
            builder.RoleBlob(session.Role!.Id, "tasks", next.ToByteArray());
            builder.TaskFollowup(session.Role.Id, source);
        }, () => session.Tasks = next);
    }
}
