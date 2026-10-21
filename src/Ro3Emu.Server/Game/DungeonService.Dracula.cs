using Google.Protobuf;
using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    /// <summary>
    /// Dracula's Lair (DraculaWeddingService owns its admission): one room per guild and occurrence on the ordinary
    /// scene/AOI/wave pipeline. The client enters after its own story 3001 (PlotInterpretation action 3000 EnterDungeon),
    /// so members arrive one by one: the first entry creates the room and later ones join it, keeping their outside return
    /// point from the guild home (InstanceService continuation).
    /// </summary>
    internal async Task<Attempt?> EnterDraculaAsync(GameSession s, DungeonCatalog.Plan plan, DraculaWeddingService.Run run,
        Attempt? existing)
    {
        var a = existing;
        if (a == null)
        {
            var room = _instances.Create(plan.SceneEntry, [s]);
            a = new Attempt
            {
                Room = room, Plan = plan, Leader = s.Role!.Id, TeamId = 0,
                PlayBook = _seasons.PlayBook, SeasonId = _seasons.SeasonId, Dracula = run
            };
        }
        else if (!_instances.AddParticipant(a.Room, s, continuation: true)) return null;
        // A member who left and came back keeps the damage done so far (the MVP is read from it).
        a.Stats.TryAdd(s.Role!.Id, new RoleStatisticsInfo
        {
            BaseInfo = new StatisticsRoleBaseInfo
            {
                Id = s.Role.Id, Name = s.Role.Name, Job = s.Progress!.Career, Area = 1,
                RewardData = new StatisticsAwardCheckData { PlayerLevel = s.Progress.Level, Job = s.Progress.Career }
            }
        });
        a.StartingItems[s] = Inventory(s);
        await SendQuietAsync(s, CreateResult, new CreateDungeonResultNotify());
        if (!await _instances.EnterAsync(a.Room, s, 3)) return null;
        s.DungeonAttempt = a;
        a.Entering = false;
        await SendQuietAsync(s, EnterSceneResult, new ToSceneEnterDungeonResult
        { SceneId = plan.SceneEntry, DungeonId = a.Room.Id });
        var entering = a;
        _loop.ScheduleLegacy(TimeSpan.FromSeconds(60), async () =>
        {
            if (ReferenceEquals(s.DungeonAttempt, entering) && !entering.Loaded.Contains(s))
                await _instances.LeaveAsync(s);
        });
        return a;
    }

    internal Task StopDraculaAsync(Attempt a) => SettleAsync(a, false);

    /// <summary>The room's participants that finished loading it.</summary>
    internal static IReadOnlyList<GameSession> LoadedMembers(Attempt a) => Present(a).Where(a.Loaded.Contains).ToArray();

    /// <summary>A message to every loaded participant (a failed channel is logged, never thrown).</summary>
    internal Task SendToRoomAsync(Attempt a, uint id, IMessage msg) => BroadcastAsync(a, id, msg);
}
