using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class WorldCoreService
{
    // FlyBuffTag and MapConfig._iFly are consumed by the client's flight availability checks.
    private bool CanFly(GameSession s) => Ready(s) && !s.Dead
        && _data.Row("MapConfig", s.SceneEntry) is { } map && GameData.Int(map, "_iFly") == 1
        && s.Buffs.Any(b => b.ExpireMs > Now && _data.Row("BuffConfig", b.ConfigId) is { } row
            && row.TryGetProperty("_kBuffCategory", out var tags) && tags.ValueKind == JsonValueKind.Array
            && tags.EnumerateArray().Any(t => GameData.Number(t) == 3100000));

    /// <summary>
    /// Wing use effects (ActionConfig 101 JumpSameMap / 102 JumpOtherMap, _iEXECHost 1; ItemConfig 1002001 Fly Wing and
    /// 1002000 Butterfly Wing). The Fly Wing moves the player to a random spot of the current map (inferred: one of the
    /// map's authored points or monster spawn points standing on walkable ground, so it never lands outside the play
    /// area); the Butterfly Wing goes to the picked map's Kafra arrival, else its born point (inferred).
    /// </summary>
    private async Task<bool> WingJumpAsync(GameSession s, long scene)
    {
        if (_worldData.Map(scene) is not { } map) return false;
        if (scene != s.SceneEntry) return await this._scene.EnterSceneAsync(s, scene, map.KafraArrival);
        var spots = map.Points.Values.Concat(map.SpawnGroups.SelectMany(g => g.Points).Select(p => new MapPoint(p.X, p.Y, p.Z, p.Face)))
            .Select(map.Grounded).Where(p => map.IsWalkable(p.X, p.Y, p.Z)).ToList();
        if (spots.Count == 0) return false;
        await this._scene.TeleportInMapAsync(s, spots[Random.Shared.Next(spots.Count)]);
        return true;
    }

    /// <summary>31083 toggles authoritative flight only with the configured buff and a valid map.</summary>
    public async Task FlySwitchAsync(GameSession s, FlySwitchReq req)
    {
        if (!Ready(s) || s.Dead || !float.IsFinite(req.Height) || req.Height < 0 || req.Height > 50
            || req.IsFly && (!CanFly(s) || s.WorldState.Flying))
        { await ErrorAsync(s, FlySwitch, ErrorCodes.Parameter); return; }
        if (!req.IsFly)
        {
            if (s.WorldState.Flying) await LandAsync(s, s.Position, s.Face);
            return;
        }
        // (inferred) Height is a desired offset; cap each takeoff to the same one-second flight budget.
        var position = s.Position.Clone();
        position.Y += Math.Min(req.Height, FlightSpeed(s));
        var map = _worldData.Map(s.SceneEntry);
        if (map == null || position.Y > map.Grounded(new MapPoint(position.X, position.Y, position.Z, s.Face)).Y + 50)
        { await ErrorAsync(s, FlySwitch, ErrorCodes.Parameter); return; }
        s.WorldState.Flying = true;
        s.WorldState.LastFlyMs = Now;
        await MoveFlightAsync(s, position, s.Face, new Vec3(), true, MovementTransitionType.MoveTransFlyUp);
    }

    private float FlightSpeed(GameSession s) => _data.Table("PublicParamConfig").TryGetValue("MountFlyMaxSpeed", out var value)
        ? Math.Max(1, (float)(GameData.Number(value) ?? s.Speed) / 100) : Math.Max(1, s.Speed / 100f);

    /// <summary>31084: AStarAgentNew sends position plus a three-dimensional direction, not a ground yaw.</summary>
    public async Task FlyMoveAsync(GameSession s, FlyMoveReq req)
    {
        double seconds = Math.Clamp((Now - s.WorldState.LastFlyMs) / 1000.0, 0.05, 2);
        float speed = FlightSpeed(s);
        var map = _worldData.Map(s.SceneEntry);
        var grounded = Finite(req.Pos) && map != null ? map.Grounded(new MapPoint(req.Pos.X, req.Pos.Y, req.Pos.Z, req.Face)) : default;
        if (!s.WorldState.Flying || !CanFly(s) || !Finite(req.Pos) || !Finite(req.Dir) || !float.IsFinite(req.Face)
            || Distance(new Vec3(), req.Dir) > 2
            || map == null || !map.IsWalkable(grounded.X, grounded.Y, grounded.Z)
            || req.Pos.Y < grounded.Y - 1 || req.Pos.Y > grounded.Y + 50
            || Distance(s.Position, req.Pos) > speed * seconds * 1.5 + 1.5)
        { await ErrorAsync(s, FlyMove, ErrorCodes.Parameter); await CorrectFlightAsync(s); return; }
        s.WorldState.LastFlyMs = Now;
        await MoveFlightAsync(s, req.Pos, req.Face, req.Dir, req.BStop, MovementTransitionType.MoveTransNormal);
    }

    public async Task FreeFallAsync(GameSession s, FreeFallJumpReq req)
    {
        if (!Ready(s) || s.Dead || !s.WorldState.Flying || !Finite(req.ToPos) || !float.IsFinite(req.Face)
            || Distance(s.Position, req.ToPos) > 50 || req.ToPos.Y > s.Position.Y + 1
            || MonsterService.Distance(s.Position.X, s.Position.Z, req.ToPos.X, req.ToPos.Z) > 3)
        { await ErrorAsync(s, FreeFall, ErrorCodes.Parameter); return; }
        await LandAsync(s, req.ToPos, req.Face);
    }

    private async Task LandAsync(GameSession s, Vec3 requested, float face)
    {
        var map = _worldData.Map(s.SceneEntry);
        if (map == null) return;
        var at = map.Grounded(new MapPoint(requested.X, requested.Y, requested.Z, face));
        if (!map.IsWalkable(at.X, at.Y, at.Z)) { await ErrorAsync(s, FreeFall, ErrorCodes.Parameter); return; }
        s.WorldState.Flying = false;
        await MoveFlightAsync(s, new Vec3 { X = at.X, Y = at.Y, Z = at.Z }, face, new Vec3(), true, MovementTransitionType.MoveTransLanding);
        // FixLandNtf instructs the client to end the special landing state; immediate landing has zero remaining time.
        await ToSelfAndViewersAsync(s, FixLand, new FixLandNtf { Uuid = s.Role!.Id, FixLandTime = 0 });
    }

    private async Task CorrectFlightAsync(GameSession s)
    {
        if (s.Role == null) return;
        await s.SendGameAsync(GameAoiSink.MovementChange, new MovementChangeNtf
        { Uid = s.Role.Id, TransType = MovementTransitionType.MoveTransForceTeleport, MovementInfo = new MovementInfo { Pos = s.Position.Clone(), Face = s.Face, ServerTime = Now } });
    }

    private async Task MoveFlightAsync(GameSession s, Vec3 pos, float face, Vec3 direction, bool stop, MovementTransitionType transition)
    {
        var move = new MovementInfo { Pos = pos.Clone(), Face = face, ServerTime = Now, MoveType = MovementType.MoveTypeNone };
        float length = Distance(new Vec3(), direction);
        if (!stop && length > 0.0001f)
        {
            float speed = FlightSpeed(s);
            move.MoveType = MovementType.MoveTypeMove; move.MoveDuration = 1000;
            move.MovementMove = new MovementMove { StartVelocity = new Vec3 { X = direction.X / length * speed, Y = direction.Y / length * speed, Z = direction.Z / length * speed }, AccVelocity = new Vec3() };
        }
        s.PreviousPosition = s.Position; s.Position = pos.Clone(); s.Face = face; s.LastWalkUtc = DateTime.UtcNow; s.Walking = false;
        _combat.PlayerMoved(s);
        var notify = new MovementChangeNtf { Uid = s.Role!.Id, TransType = transition, MovementInfo = move };
        await s.SendGameAsync(GameAoiSink.MovementChange, notify);
        await _world.MoveAsync(s, new WalkReq { Pos = pos.Clone(), Face = face, BStop = true, SpeedRate = 1 }, movement: notify);
    }
}
