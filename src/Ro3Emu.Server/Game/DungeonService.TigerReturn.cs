namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    // G TaskConfig[10102200] enters scene 1022001; DungeonQuestConfig[1022001] ends on Eddga.
    private const long TigerKingSceneEntry = 1022001;
    // (inferred) G's readable client sends 30402 on EndDungeon but does not reveal the original server's
    // automatic return timing. Leave a brief result/cutscene window before returning this story scene.
    private static readonly TimeSpan TigerKingReturnDelay = TimeSpan.FromSeconds(3);

    private void ScheduleTigerKingReturn(Attempt attempt, GameSession session, long roleId)
    {
        var room = attempt.Room;
        var attachment = session.Attachment;
        _loop.ScheduleLegacy(TigerKingReturnDelay, async () =>
        {
            // A manual 30402, disconnect, role switch, or another scene transfer wins the race.
            if (!attempt.Settled || room.Closed || session.Role?.Id != roleId
                || !ReferenceEquals(session.Attachment, attachment)
                || !ReferenceEquals(session.DungeonAttempt, attempt)
                || session.InstanceId != room.Id || session.SceneEntry != room.SceneEntry
                || session.SceneLoading || !ReferenceEquals(_instances.Find(room.Id), room)
                || room.MemberFor(session) == null) return;
            await _instances.LeaveAsync(session, recoverDeadOnReturn: true);
        }, "Tiger King story return (inferred)");
    }
}
