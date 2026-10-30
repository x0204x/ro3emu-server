using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A cannon volley: the bullets the client aimed (bulletExtraParam: gravity in cm/s^2, as LC_GVGFireCannon multiplies
/// _fGravity by 100, speed in cm/s, the direction), flown as bullet units and exploded where they land.
/// </summary>
public sealed partial class TerritoryWarsService
{
    /// <summary>(inferred) The longest flight followed; a shot that never comes down within it explodes there.</summary>
    private const double MaxFlightSeconds = 12;
    /// <summary>(inferred) CameraFollowBullet: the camera distance (cm) and the bullet's screen offsets.</summary>
    private const long FollowDistanceCm = 1000;

    /// <summary>
    /// The skill must be the cannon's (_iSkillID), the volley 1.._iSingleShotNum bullets. A volley costs one of the cannon's
    /// _iBulletNumLimit (SkillConfig _kCost [6, 4900014, 1]) and starts the skill's _iCD (inferred: _iReloadTime is not
    /// enforced apart from it; it is never longer).
    /// </summary>
    private async Task<int> FireCannonAsync(GameSession s, CannonUnit unit, GVGCannonConfig cfg, CannonUseSkill? use)
    {
        if (use?.UseSkillParam is not { } cast || cast.SkillId != cfg.SkillId || use.BulletExtraParam.Count == 0
            || use.BulletExtraParam.Count > Math.Max(1, cfg.SingleShotNum)) return ErrorCodes.NpcParameter;
        if (SkillConfig.Get(_data, cfg.SkillId) is not { } skill || unit.Removed || s.Dead) return ErrorCodes.NpcParameter;
        long now = MonsterService.NowMs;
        if (now < unit.ReadyMs) return ErrorCodes.SkillCooldown;
        if (unit.Bullets <= 0) return ErrorCodes.SkillCostShort;
        unit.Bullets--;
        unit.CdStartMs = now;
        unit.ReadyMs = now + Math.Max(skill.Cd, cfg.ReloadTime);
        await CannonBulletsAsync(s, unit);
        await CannonCooldownAsync(s, unit, cfg);
        bool follow = true;
        foreach (var param in use.BulletExtraParam)
        {
            if (await LaunchBulletAsync(s, unit, cfg, skill, param) is not { } bullet) continue;
            if (follow) await FollowBulletAsync(s, bullet);
            follow = false;
        }
        return 0;
    }

    /// <summary>
    /// A bullet unit of SkillConfig _kLogicData[2] (an NPCConfig _iNPCType 10 row) from the muzzle (GVGCannonConfig
    /// _kFireOffset cm: side, up, forward along the aim), flying v t + g t^2 / 2 until it meets the ground.
    /// </summary>
    private async Task<CannonBullet?> LaunchBulletAsync(GameSession s, CannonUnit unit, GVGCannonConfig cfg, SkillConfig skill,
        BulletExtraParam param)
    {
        if (skill.LogicData is not [_, _, var npc, ..] || NPCConfig.Get(_data, npc) is not { } row || s.Role == null
            || !_cannons.TryGetValue(unit.Uid, out var standing)) return null;
        var dir = param.FaceVector3D ?? new Vec3 { Z = 1 };
        double length = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y + dir.Z * dir.Z);
        if (length < 1e-6 || param.Speed <= 0) return null;
        var start = Muzzle(unit, cfg, dir);
        double speed = param.Speed / 100.0, gravity = Math.Max(0, param.Gravity / 100.0);
        var velocity = new Vec3 { X = (float)(dir.X / length * speed), Y = (float)(dir.Y / length * speed), Z = (float)(dir.Z / length * speed) };
        double flight = FlightSeconds(unit, start, velocity, gravity);
        var bullet = new CannonBullet
        {
            Uid = CannonUidBase | BulletUidFlag | Interlocked.Increment(ref _bulletSerial), SceneEntry = unit.SceneEntry,
            InstanceId = unit.InstanceId, X = start.X, Y = start.Y, Z = start.Z, Face = unit.Face, NpcId = (int)npc,
            Shooter = s.Role.Id, Camp = unit.Camp, Velocity = velocity, Gravity = (float)gravity, LaunchMs = MonsterService.NowMs,
            FlightMs = (int)(flight * 1000), EntityType = (int)row.NPCType,
        };
        if (!await _instances.AddPropAsync(standing.Room, bullet)) return null;
        var landing = bullet.At(flight);
        _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(bullet.FlightMs), () => ExplodeAsync(s, standing.Room, bullet, skill, landing));
        return bullet;
    }

    private static Vec3 Muzzle(CannonUnit unit, GVGCannonConfig cfg, Vec3 dir)
    {
        long[] offset = cfg.FireOffset;
        double side = offset.Length > 0 ? offset[0] / 100.0 : 0, up = offset.Length > 1 ? offset[1] / 100.0 : 0,
            forward = offset.Length > 2 ? offset[2] / 100.0 : 0;
        double flat = Math.Sqrt(dir.X * dir.X + dir.Z * dir.Z);
        double fx = flat > 1e-6 ? dir.X / flat : 0, fz = flat > 1e-6 ? dir.Z / flat : 1;
        return new Vec3 { X = (float)(unit.X + fx * forward + fz * side), Y = (float)(unit.Y + up), Z = (float)(unit.Z + fz * forward - fx * side) };
    }

    /// <summary>Seconds until the bullet comes down to the ground under it (the nav mesh, else the cannon's height).</summary>
    private double FlightSeconds(CannonUnit unit, Vec3 start, Vec3 velocity, double gravity)
    {
        var nav = _maps.Map(unit.SceneEntry)?.Nav;
        const double Step = 0.02;
        for (double t = Step; t < MaxFlightSeconds; t += Step)
        {
            double x = start.X + velocity.X * t, z = start.Z + velocity.Z * t, y = start.Y + velocity.Y * t - gravity * t * t / 2;
            double ground = nav?.WalkHeight((float)x, (float)z, (float)y + 50) ?? unit.Y;
            if (y <= ground && velocity.Y - gravity * t <= 0) return t;
        }
        return MaxFlightSeconds;
    }

    /// <summary>31018 CameraFollowBullet_ListLong on the shooter: the camera of LC_GVGFireCannon follows the first bullet.</summary>
    private Task FollowBulletAsync(GameSession s, CannonBullet bullet)
    {
        var data = new CustomData
        {
            Id = CameraFollowBullet,
            CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.ListLong, ListLongParam = { bullet.Uid, FollowDistanceCm, 0, 0 } },
        };
        return Send(s, WorldCoreService.CustomChange, new CustomDataChange { EntityId = s.Role!.Id, CustomDatas = { data } });
    }

    /// <summary>
    /// The bullet lands: it leaves the room and the cannon skill hits around the point (inferred: within SkillConfig
    /// _kLogicData[4] cm, as the original's GVG_S0_CannonFire script is server side) the monsters and the hostile players
    /// of the battle room (the other camp in a siege, other guilds in a territory battle), through the ordinary extra-cast
    /// paths. Nothing is hit once the shooter's battle is over.
    /// </summary>
    private async Task ExplodeAsync(GameSession s, InstanceService.Room room, CannonBullet bullet, SkillConfig skill, Vec3 landing)
    {
        await _instances.RemovePropAsync(room, bullet);
        if (room.Closed || s.Role == null || WarRoomOf(s)?.Room != room) return;
        float radius = (skill.LogicData is [_, _, _, _, var cm, ..] && cm > 0 ? cm : 500) / 100f;
        var monsters = _combat.MonstersNear(s, landing, radius);
        if (monsters.Count > 0) await _combat.CastExtraAsync(s, skill.Row, monsters);
        foreach (var player in _world.PlayersIn(s.WorldScene).ToList())
            if (player != s && !player.Dead && player.Role != null && _combat.Hostile(s, player)
                && MonsterService.Distance(player.Position.X, player.Position.Z, landing.X, landing.Z) <= radius)
                await _combat.CastExtraOnPlayerAsync(s, skill, player);
    }

    /// <summary>A cannon bullet in flight (NPCConfig _iNPCType 10): its entity carries the whole ballistic move.</summary>
    internal sealed class CannonBullet : WorldUnit
    {
        public required int NpcId { get; init; }
        public required long Shooter { get; init; }
        public required long Camp { get; init; }
        public required Vec3 Velocity { get; init; }
        public required float Gravity { get; init; }
        public required long LaunchMs { get; init; }
        public required int FlightMs { get; init; }
        public required int EntityType { get; init; }

        public Vec3 At(double t) => new()
        {
            X = (float)(X + Velocity.X * t), Y = (float)(Y + Velocity.Y * t - Gravity * t * t / 2), Z = (float)(Z + Velocity.Z * t),
        };

        public override EntityBase Entity() => new()
        {
            Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = EntityType, Owner = Shooter, EntityCamp = Camp,
            Face = Face, State = EntityState.Alive, NotifyType = EntityBase.Types.NotifyType.All,
            MovementInfo = new MovementInfo
            {
                Pos = new Vec3 { X = X, Y = Y, Z = Z }, Face = Face, ServerTime = LaunchMs, MoveDuration = FlightMs,
                MoveType = MovementType.MoveTypeMove,
                MovementMove = new MovementMove { StartVelocity = Velocity.Clone(), AccVelocity = new Vec3 { Y = -Gravity } },
            },
        };
    }
}
