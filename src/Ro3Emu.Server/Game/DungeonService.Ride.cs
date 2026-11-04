using Romsg;
using Kind = Ro3Emu.Server.Game.ReconstructedDungeons.TriggerKind;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// 尚未取得原服證據的通用重建載乘（ScriptedStep.RideAfter）；蟻穴已觀察流程由AntStory處理，不走本推測路徑。
/// The actor a player used to complete a step walks a guide route of the scene
/// editor (m_kGuidePoints) and the participants move with it, before the step's teleport and the next step. The client
/// has no ride rule: the route and the scene's camera animation are data without a trigger, and the original's server
/// script is not shipped. So (inferred) the carrier walks the route leg by leg at its own UnitConfig._fSpeed as a monster
/// walks, a seated player goes with it on its seat, a player who is not seated is moved in short forced-position steps,
/// and the camera animation is not played. The carrier stays where the route ends (only a scene's last step has a ride,
/// so no later step shows it again).
/// </summary>
public sealed partial class DungeonService
{
    private const int RideTickMs = 100;

    /// <summary>(inferred) The carrier waits this long after seating a rider, while the client plays the seat's enter animation.</summary>
    private const int RideStartDelayMs = 1500;

    /// <summary>(inferred) The rider's client plays the seat's exit animation for this long after being put down.</summary>
    private const int RideExitDelayMs = 2500;

    /// <summary>
    /// An actor interaction that seats its user (NPCInteractConfig _iStatusID of a vehicle status, the Deniro's 5300005)
    /// puts the player in the actor's seat, the client's own gameplay state; the actor puts them down when its ride ends,
    /// or when they leave the scene, or at once through the client's exit button.
    /// </summary>
    private async Task SeatRiderAsync(Attempt a, GameSession s, SceneActorUnit actor, int interaction)
    {
        // 蟻穴原服實包已確認第一次不登車、第二次持續載乘，不套用舊的2秒推測。
        if (await TrySeatAntRiderAsync(a, s, actor, interaction)) return;
        // Only the interaction the current step waits on seats anyone: the client may send any of an actor's interactions.
        if (!_vehicles.IsRide(interaction) || !StepWaitsOn(a, interaction)) return;
        long token = await _vehicles.SeatOnSceneActorAsync(s, actor.Uid, actor.NpcId, interaction);
        if (token != 0) a.Riders[s] = token;
    }

    /// <summary>The ride is over: the actor puts its riders down.</summary>
    private async Task PutDownRidersAsync(Attempt a)
    {
        foreach (var (s, token) in a.Riders.ToList()) await _vehicles.PutDownFromSceneActorAsync(s, token);
        a.Riders.Clear();
    }

    /// <summary>Whether the running step is completed by this actor interaction, directly or by its dungeon message.</summary>
    private bool StepWaitsOn(Attempt a, long interaction) => !a.StepDone && Step(a) is { } step
        && step.Complete.Any(t => t.Kind == Kind.Interaction && t.Id == interaction
            || t.Kind == Kind.Message && _catalog.DungeonMessage(interaction) == t.Id);

    private sealed record Ride(IReadOnlyList<(float X, float Y, float Z)> Path, SceneActorUnit Carrier, float Length,
        int Generation, long StartMs, double DurationMs)
    {
        /// <summary>Metres along the route the last tick moved the riders to; -1 before the first tick.</summary>
        public float Walked { get; set; } = -1;

        /// <summary>The route leg the carrier was last told to walk, -1 before the first.</summary>
        public int Leg { get; set; } = -1;
    }

    /// <summary>The carrier's walking speed in metres per second: UnitConfig._fSpeed (cm/s) of its NPC's _iUnitID, 0 if none.</summary>
    private float RideSpeed(SceneActorUnit carrier) =>
        NPCConfig.Get(_data, carrier.NpcId) is { } npc && UnitConfig.Get(_data, npc.UnitId) is { } unit ? (float)unit.Speed / 100f : 0f;

    /// <summary>The horizontal length of a route in metres, as the monsters' walks count distance.</summary>
    internal static float RideLength(IReadOnlyList<(float X, float Y, float Z)> path)
    {
        float length = 0;
        for (int i = 1; i < path.Count; i++) length += MonsterService.Distance(path[i - 1].X, path[i - 1].Z, path[i].X, path[i].Z);
        return length;
    }

    /// <summary>The position and heading after <paramref name="metres"/> along a route (clamped to its ends).</summary>
    internal static (float X, float Y, float Z, float Face) RideAt(IReadOnlyList<(float X, float Y, float Z)> path, float metres)
    {
        float left = Math.Max(0, metres);
        for (int i = 1; i < path.Count; i++)
        {
            var (a, b) = (path[i - 1], path[i]);
            float segment = MonsterService.Distance(a.X, a.Z, b.X, b.Z);
            float face = MonsterService.Angle(a.X, a.Z, b.X, b.Z);
            if (left <= segment || i == path.Count - 1)
            {
                float t = segment <= 0 ? 1 : Math.Min(1, left / segment);
                return (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, face);
            }
            left -= segment;
        }
        return (path[0].X, path[0].Y, path[0].Z, 0);
    }

    /// <summary>The route leg at <paramref name="metres"/>: its index and its two points.</summary>
    internal static (int Leg, (float X, float Y, float Z) From, (float X, float Y, float Z) To) RideLeg(
        IReadOnlyList<(float X, float Y, float Z)> path, float metres)
    {
        float left = Math.Max(0, metres);
        for (int i = 1; i < path.Count; i++)
        {
            float segment = MonsterService.Distance(path[i - 1].X, path[i - 1].Z, path[i].X, path[i].Z);
            if (left < segment || i == path.Count - 1) return (i - 1, path[i - 1], path[i]);
            left -= segment;
        }
        return (0, path[0], path[0]);
    }

    /// <summary>
    /// Starts the step's ride when it has one and the player used an actor that can carry (a route with at least two
    /// points, a positive speed). <paramref name="arrived"/> then runs when the route is walked; false means no ride
    /// starts and the caller goes on at once.
    /// </summary>
    private bool TryStartRide(Attempt a, ReconstructedDungeons.ScriptedStep step, SceneActorUnit? carrier, Func<Task> arrived)
    {
        if (step.RideAfter == 0 || carrier == null || _catalog.GuidePath(a.Plan.SceneEntry, step.RideAfter) is not { } path) return false;
        float speed = RideSpeed(carrier), length = RideLength(path);
        if (speed <= 0 || length <= 0) return false;
        TimeSpan delay = a.Riders.Count > 0 ? Paced(RideStartDelayMs) : TimeSpan.Zero;
        var ride = new Ride(path, carrier, length, a.StepGeneration, MonsterService.NowMs + (long)delay.TotalMilliseconds,
            Paced((int)(length / speed * 1000)).TotalMilliseconds);
        _loop.ScheduleLegacy(delay, () => RideTickAsync(a, ride, arrived), "dungeon ride");
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scripted scene {Scene} ride: moving unit {Npc} failed")]
    private partial void LogRideMoveFailed(Exception e, long scene, int npc);

    /// <summary>The route's points past <paramref name="from"/> and before <paramref name="to"/> metres, in route order.</summary>
    internal static IEnumerable<(float X, float Y, float Z, float Face)> RideCorners(IReadOnlyList<(float X, float Y, float Z)> path,
        float from, float to)
    {
        float reached = 0;
        for (int i = 0; i < path.Count; i++)
        {
            if (i > 0) reached += MonsterService.Distance(path[i - 1].X, path[i - 1].Z, path[i].X, path[i].Z);
            if (reached > from && reached < to) yield return RideAt(path, reached);
        }
    }

    private async Task RideTickAsync(Attempt a, Ride ride, Func<Task> arrived)
    {
        if (a.Settled || a.Room.Closed || a.StepGeneration != ride.Generation) return;
        double done = Math.Min(1.0, (MonsterService.NowMs - ride.StartMs) / Math.Max(1.0, ride.DurationMs));
        float walked = (float)(done * ride.Length);
        // The position follows the wall clock, so a late tick (a loaded loop) jumps ahead: the riders still pass every
        // point of the route walked since the last tick instead of being moved straight across its corners.
        foreach (var corner in RideCorners(ride.Path, ride.Walked, walked)) await MoveRidersAsync(a, ride, corner);
        ride.Walked = walked;
        await MoveRidersAsync(a, ride, RideAt(ride.Path, walked));
        await WalkLegAsync(a, ride, walked);
        if (done >= 1)
        {
            // The step's end removes the carrier: not while a rider's client still plays the seat's exit animation, which reads
            // the vehicle (LC_PassengerComponent._detachFromVehicle), or its next scene change stops at the loading screen.
            bool seated = a.Riders.Count > 0;
            await PutDownRidersAsync(a);
            if (seated) _loop.ScheduleLegacy(Paced(RideExitDelayMs), arrived, "dungeon ride end");
            else await arrived();
        }
        else _loop.ScheduleLegacy(Paced(RideTickMs), () => RideTickAsync(a, ride, arrived), "dungeon ride");
    }

    private async Task MoveRidersAsync(Attempt a, Ride ride, (float X, float Y, float Z, float Face) at)
    {
        ride.Carrier.X = at.X; ride.Carrier.Y = at.Y; ride.Carrier.Z = at.Z; ride.Carrier.Face = at.Face;
        // A move that fails for one unit must not strand the step: the ride goes on and arrives.
        foreach (var member in Present(a).Where(m => a.Loaded.Contains(m) && !m.SceneLoading))
        {
            try
            {
                // A seated rider's client moves it with the seat, so a forced position would only shake it.
                if (a.Riders.ContainsKey(member) && _vehicles.IsSeatedOnSceneActor(member))
                {
                    member.PreviousPosition = member.Position;
                    member.Position = new Vec3 { X = at.X, Y = at.Y, Z = at.Z };
                }
                else await _scenes.TeleportInMapAsync(member, new MapPoint(at.X, at.Y, at.Z, at.Face));
            }
            catch (Exception e) { LogRideMoveFailed(e, a.Plan.SceneEntry, 0); }
        }
    }

    /// <summary>Tells the viewers to walk the leg the carrier has reached, as a monster's walk is sent (MonsterService).</summary>
    private async Task WalkLegAsync(Attempt a, Ride ride, float walked)
    {
        var (leg, from, to) = RideLeg(ride.Path, walked);
        if (leg == ride.Leg) return;
        ride.Leg = leg;
        float metres = MonsterService.Distance(from.X, from.Z, to.X, to.Z);
        float speed = (float)(ride.Length / Math.Max(1.0, ride.DurationMs) * 1000);
        float face = MonsterService.Angle(from.X, from.Z, to.X, to.Z);
        try
        {
            // A driver's client controls its vehicle and ignores a walk for it; transType 3 is the order it obeys:
            // SetMoveTargetPos to the given position at the unit's own speed (LC_Scene_Sync._syncUnitMoveState).
            await _world.MoveUnitAsync(ride.Carrier, a.Riders.Keys.Any(_vehicles.IsSeatedOnSceneActor)
                ? new MovementChangeNtf
                {
                    Uid = ride.Carrier.Uid, TransType = MovementTransitionType.MoveTransServerControl,
                    MovementInfo = new MovementInfo { Pos = new Vec3 { X = to.X, Y = to.Y, Z = to.Z }, Face = face, ServerTime = MonsterService.NowMs },
                }
                : new MovementChangeNtf
                {
                    Uid = ride.Carrier.Uid, TransType = MovementTransitionType.MoveTransNormal,
                    MovementInfo = new MovementInfo
                    {
                        Pos = new Vec3 { X = from.X, Y = from.Y, Z = from.Z }, ServerTime = MonsterService.NowMs,
                        MoveDuration = (int)Math.Max(1, metres / speed * 1000), Face = face, MoveType = MovementType.MoveTypeWalk,
                        MovementWalk = new MovementWalk { Dir = face, Speed = speed, Acc = 0, EndHeight = to.Y },
                    },
                });
        }
        catch (Exception e) { LogRideMoveFailed(e, a.Plan.SceneEntry, ride.Carrier.NpcId); }
    }
}
