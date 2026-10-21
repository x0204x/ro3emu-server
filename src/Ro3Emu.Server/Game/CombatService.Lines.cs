using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private readonly Dictionary<long, int> _pendingSceneWork = new();

    /// <summary>Scheduled hit/end work and suspended hit settlement pin their original public line.</summary>
    public bool HasPendingMapWork(RuntimeMapKey map) => _pendingSceneWork.GetValueOrDefault(map.WorldScene) != 0;

    private void ScheduleRuntime(long map, TimeSpan delay, Func<Task> callback)
    {
        _pendingSceneWork[map] = _pendingSceneWork.GetValueOrDefault(map) + 1;
        _loop.ScheduleRuntime(delay, async () =>
        {
            try { await callback(); }
            finally
            {
                if (--_pendingSceneWork[map] == 0) _pendingSceneWork.Remove(map);
            }
        });
    }

    private static bool CurrentCastMap(GameSession session, ActiveSkillCast cast) => !cast.Cancelled
        && !session.SceneLoading && !session.SceneRecoveryRequired && !session.Dead
        && session.Role?.Id == cast.OwnerId && session.WorldScene == cast.WorldScene
        && ReferenceEquals(session.Attachment, cast.Attachment) && cast.Attachment?.Cancelled != true
        && session.SceneGeneration == cast.SceneGeneration && (cast.SourceEligible?.Invoke() ?? true);
}
