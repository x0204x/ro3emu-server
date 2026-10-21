using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    /// <summary>Whale's 30400 enters a guild-owned preparation room, using the ordinary scene/AOI pipeline.</summary>
    internal async Task<Attempt?> EnterGuildChallengeAsync(GameSession s, DungeonCatalog.Plan plan,
        GuildChallengeService.Run run, Attempt? existing)
    {
        var a = existing;
        if (a == null)
        {
            var room = _instances.Create(plan.SceneEntry, [s]);
            a = new Attempt
            {
                Room = room, Plan = plan, Leader = s.Role!.Id, TeamId = 0,
                PlayBook = _seasons.PlayBook, SeasonId = _seasons.SeasonId, GuildChallenge = run
            };
        }
        else if (!_instances.AddParticipant(a.Room, s, continuation: s.DungeonAttempt is { Settled: true })) return null;
        a.Stats[s.Role!.Id] = new RoleStatisticsInfo
        {
            BaseInfo = new StatisticsRoleBaseInfo
            {
                Id = s.Role.Id, Name = s.Role.Name, Job = s.Progress!.Career, Area = 1,
                RewardData = new StatisticsAwardCheckData { PlayerLevel = s.Progress.Level, Job = s.Progress.Career }
            }
        };
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

    internal Task StartGuildChallengeAsync(Attempt a) => StartAsync(a);
    internal Task StopGuildChallengeAsync(Attempt a) => SettleAsync(a, false);
}
