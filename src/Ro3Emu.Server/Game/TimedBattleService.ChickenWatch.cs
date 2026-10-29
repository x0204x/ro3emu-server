using Romsg;
using static Ro3Emu.Server.Game.ChickenRules;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Survivor Battle spectating. An eliminated player watches a teammate who still fights: the camera follows the unit
/// named by custom data 1002 on the watcher's own entity (LC_DunGeon_Chicken._switchWatchMode enters the
/// ChickenWatchMode 3C, LC_CustomDataComp_Base.ProcessCamera binds the camera), and the watcher shows Watch in the
/// team list (5036 RoundRoleStatus.Watch). 82675 starts the client's countdown; when it runs out the server binds the
/// camera (the client does nothing at 0). 82640 switches to another living teammate.
/// </summary>
public sealed partial class TimedBattleService
{
    public const uint ChangeWatch = 82640, WatchNotify = 82675;

    /// <summary>
    /// Watchers: their client shows what is around the watched teammate, so the server keeps the watcher's own
    /// position (and with it its area of interest) on the teammate, and nobody else sees the watcher meanwhile
    /// (World.CanObserve). Its own client gets no movement for it; a revive teleports it to its chosen point.
    /// (inferred) The original's way is not in the client: an eliminated player's body is gone for the others.
    /// </summary>
    private readonly HashSet<GameSession> _spectators = new(ReferenceEqualityComparer.Instance);

    /// <summary>How far the watched teammate may get before the watcher's view is moved after it (m).</summary>
    private const float WatchViewSlack = 6f;

    private bool CanBeSeen(GameSession viewer, GameSession subject) => viewer == subject || !_spectators.Contains(subject);

    /// <summary>PublicParam EatChickenObserve 10 (s): (inferred) also the wait before spectating starts on its own.</summary>
    private const long AutoWatchMs = 10_000;

    /// <summary>The client refuses a switch within 2 s (LC_DunGeon_Chicken._trySendswitchWatchPlayer, tip 63568); the
    /// server keeps a little slack for the network (inferred).</summary>
    private const long WatchSwitchMs = 1_500;

    /// <summary>82675 to a player just eliminated while a teammate still fights: autoWatchTime is in seconds
    /// (LC_DAL_Chicken._onProcessWatchNotify compares it with TimerManager:GetSystemTime).</summary>
    private async Task OfferWatchAsync(Match match, ChickenRun run, GameSession player, long now)
    {
        var me = run.Players[player];
        if (me.GaveUp || FightingMate(match, run, player) == null) return;
        me.AutoWatchAtMs = now + AutoWatchMs;
        await player.SendGameAsync(WatchNotify, new ChickenWatchNotify { AutoWatchTime = me.AutoWatchAtMs / 1000 });
    }

    /// <summary>The first teammate (by team slot) still in the fight and in the room, as the client's own pick.</summary>
    private GameSession? FightingMate(Match match, ChickenRun run, GameSession player, long except = 0)
    {
        var room = InRoom(match).ToHashSet();
        return Team(match, match.Faction[player])
            .Where(mate => mate != player && room.Contains(mate) && !mate.Dead && run.Players[mate].State == RoundRoleStatus.InWar
                && match.Participants[mate].Id != except)
            .OrderBy(mate => run.Players[mate].TeamIndex).FirstOrDefault();
    }

    /// <summary>
    /// 82640 ChickenChangeWatchPlayerReq{player_id} (LC_ChickenHelper.SendSwitchWatchPlayer): a teammate's portrait, the
    /// Spectating widget's arrows, or the client's own retarget when the watched one falls. CheckCanWatchPlayer allows
    /// it only out of the fight and only toward a living teammate. No response (NullArg, no handler); a refused switch
    /// is dropped.
    /// </summary>
    public async Task ChangeWatchAsync(GameSession session, ChickenChangeWatchPlayerReq req)
    {
        if (Chicken(session) is not (var match, var run, var me)) return;
        await RunMatchTransitionAsync(match, "chicken watch", async () =>
        {
            long now = NowMs();
            if (match.Settled || run.Stage != StageStartGame || me.GaveUp || me.State == RoundRoleStatus.InWar
                || me.RelifeUntilMs > 0 || !session.Dead || now < me.WatchSwitchMs + WatchSwitchMs) return;
            var target = Team(match, match.Faction[session]).FirstOrDefault(mate => mate != session
                && match.Participants[mate].Id == req.PlayerId && !mate.Dead && run.Players[mate].State == RoundRoleStatus.InWar
                && InRoom(match).Contains(mate));
            if (target == null) return;
            await WatchAsync(match, run, session, req.PlayerId, now);
        });
    }

    /// <summary>Binds (or with 0 releases) the watcher's camera and shows it as Watch to its team.</summary>
    private async Task WatchAsync(Match match, ChickenRun run, GameSession watcher, long target, long now)
    {
        var me = run.Players[watcher];
        me.AutoWatchAtMs = 0;
        if (me.Watching == target) return;
        me.Watching = target;
        me.WatchSwitchMs = now;
        if (me.State is RoundRoleStatus.Death or RoundRoleStatus.Watch)
            me.State = target != 0 ? RoundRoleStatus.Watch : RoundRoleStatus.Death;
        await SendCameraAsync(watcher, target);
        await SyncTeamAsync(match, match.Faction[watcher]);
        if (target != 0 && _spectators.Add(watcher)) await _world.RefreshVisibilityAsync(watcher);
        await FollowViewAsync(match, watcher, me);
    }

    /// <summary>Moves a watcher's view onto its watched teammate once it is more than a few metres away.</summary>
    private async Task FollowViewAsync(Match match, GameSession watcher, ChickenPlayer me)
    {
        if (me.Watching == 0 || watcher.SceneLoading
            || Team(match, match.Faction[watcher]).FirstOrDefault(p => match.Participants[p].Id == me.Watching) is not { } target
            || target.SceneLoading || target.WorldScene != watcher.WorldScene) return;
        if (MonsterService.Distance(watcher.Position.X, watcher.Position.Z, target.Position.X, target.Position.Z) <= WatchViewSlack) return;
        var pos = target.Position.Clone();
        watcher.Position = pos;
        await _world.MoveAsync(watcher, new WalkReq { Pos = pos, Face = watcher.Face, BStop = true, SpeedRate = 1 }, forceTeleport: true);
    }

    /// <summary>A watcher that stops watching is seen again (after its revive moved it, or as it leaves).</summary>
    private async Task ReappearAsync(GameSession player)
    {
        if (_spectators.Remove(player) && !player.SceneLoading) await _world.RefreshVisibilityAsync(player);
    }

    /// <summary>31018 with 1002 on the watcher's entity, to the watcher only (see <see cref="CustomCamera"/>).</summary>
    private static Task SendCameraAsync(GameSession watcher, long target) => watcher.Role == null || watcher.SceneLoading
        ? Task.CompletedTask
        : watcher.SendGameAsync(WorldCoreService.CustomChange,
            new CustomDataChange { EntityId = watcher.Role.Id, CustomDatas = { LongCustom(CustomCamera, target) } });

    /// <summary>Per second: the countdowns that ran out bind to the first fighting teammate, and a watcher whose
    /// teammate fell is moved on when its client did not do it itself (inferred fallback).</summary>
    private async Task TickWatchAsync(Match match, ChickenRun run, long now)
    {
        foreach (var (player, me) in run.Players.ToArray())
        {
            if (Chicken(player) == null || !player.Dead || me.RelifeUntilMs > 0 || me.GaveUp)
            {
                me.AutoWatchAtMs = 0;
                continue;
            }
            if (me.Watching == 0)
            {
                if (me.AutoWatchAtMs == 0 || now < me.AutoWatchAtMs) continue;
                if (FightingMate(match, run, player) is { } first) await WatchAsync(match, run, player, match.Participants[first].Id, now);
                else me.AutoWatchAtMs = 0;
                continue;
            }
            bool fighting = Team(match, match.Faction[player]).Any(mate => match.Participants[mate].Id == me.Watching
                && !mate.Dead && run.Players[mate].State == RoundRoleStatus.InWar && InRoom(match).Contains(mate));
            await FollowViewAsync(match, player, me);
            if (fighting || now < me.WatchSwitchMs + 2 * WatchSwitchMs) continue;
            if (FightingMate(match, run, player, me.Watching) is { } next)
                await WatchAsync(match, run, player, match.Participants[next].Id, now);
        }
    }

    /// <summary>A revived player gets its own camera back (and is seen again once its revive has moved it).</summary>
    private async Task StopWatchingAsync(Match match, ChickenRun run, GameSession player)
    {
        var me = run.Players[player];
        me.AutoWatchAtMs = 0;
        _spectators.Remove(player);
        if (me.Watching == 0) return;
        me.Watching = 0;
        await SendCameraAsync(player, 0);
    }
}
