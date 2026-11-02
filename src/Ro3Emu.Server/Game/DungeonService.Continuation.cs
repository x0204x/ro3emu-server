using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    /// <summary>(inferred) Seconds between a cleared scene's 30403 and the entry into the scene that continues it.</summary>
    internal const int ContinueSeconds = 3;

    /// <summary>
    /// The story scene that continues <paramref name="scene"/> without a loading screen: DungeonConfig
    /// _iSameDupLoadingType 2 with _kSameDupLoadingTypeParam [scene] (LC_SceneManagerNew), as 1000301 (the Deniro) continues
    /// 1000201 (the passage below the vortex). 0: none. The client only skips the loading screen; how the original server
    /// moved the player on is not shipped, so entering the next scene after the clear, and the delay, are (inferred).
    /// Before, a player who reached the bottom of the passage stayed in the dark with nothing happening until they used
    /// the quest tracker.
    /// </summary>
    private long ContinuationOf(long scene) => DungeonConfig.All(_data)
        .Where(d => d.SameDupLoadingType == 2 && d.SameDupLoadingTypeParam.Contains(scene)).Select(d => d.Id).FirstOrDefault();

    private void ScheduleContinuation(GameSession s, Attempt a, long next)
    {
        long roleId = s.Role?.Id ?? 0;
        _loop.ScheduleLegacy(TimeSpan.FromSeconds(ContinueSeconds), async () =>
        {
            // The player may have left, died out of it, or moved on (the quest tracker's own entry) in the meantime.
            if (s.Role?.Id != roleId || !ReferenceEquals(s.DungeonAttempt, a) || s.InstanceId != a.Room.Id
                || s.SceneLoading || a.Room.Closed) return;
            // The same continuation entry the quest tracker's action 3000 sends from the settled room; the story's next
            // step was accepted with the clear, so admission is the server's own as for that click.
            await EnterAsync(s, new EnterDungeonReq { SceneEntry = next }, alone: true);
        });
    }
}
