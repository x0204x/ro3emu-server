using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    /// <summary>(inferred) Seconds between a plane scene's 30403 success and the return to the field.</summary>
    internal const int PlaneExitSeconds = 5;

    /// <summary>
    /// A solo story plane (DungeonConfig _iPlaneMapID set, _iRoleCntLimit 1: Southern Payon's portal waves 1021301, Culvert,
    /// Payon Forest...) shows no result window: 30403 only raises LC_DunGeon_Base's DungeonSettlement event, which no
    /// story panel takes, so a cleared plane kept the player in an empty copy of the field until they pressed exit. The
    /// story continues on the field itself (10102100's successor 10102110 is delivered to NPC 400037 on map 10213, the
    /// map this plane copies). The original's encounter scripts are not in the client, so returning the player after the
    /// clear is (inferred), as is the delay; the return is the exit button's own path, a 30402 that
    /// LC_DunGeon_Base.CELL_LEAVE_DUNGEON also accepts unasked.
    /// </summary>
    private static bool LeavesAfterClear(Attempt a) =>
        a.Plan.Multi == null && new DungeonConfig(a.Plan.Config) is { PlaneMapId: not 0, RoleCntLimit: 1 };

    private void ScheduleLeaveAfterClear(GameSession s, Attempt a)
    {
        long roleId = s.Role?.Id ?? 0;
        _loop.ScheduleLegacy(TimeSpan.FromSeconds(PlaneExitSeconds), async () =>
        {
            // The player may have left, died out of it, or moved on to another run in the meantime.
            if (s.Role?.Id != roleId || !ReferenceEquals(s.DungeonAttempt, a) || s.InstanceId != a.Room.Id
                || s.SceneLoading || a.Room.Closed) return;
            await LeaveAsync(s);
        });
    }
}
