using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    private MonsterTemplate? AirshipWaveTemplate(Attempt a, int npc)
    {
        if (a.Plan.SceneEntry != AirshipScene || _monsters.Template(npc) is not { } template) return null;
        // G 三艦距離砲台超出一般60公尺AOI；只在本任務房間保持可見。
        if (a.Wave == 1 && npc is 5300015 or 5300053) return template with { VisibleAcrossScene = true };
        // G 52488/31037 首領8142HP；表值805×101135/10000=8141.3675。
        // 只對已觀察的本場景採上取整，不修改所有NPC的通用四捨五入。
        if (a.Wave == 4 && npc == 5300018 && NPCConfig.Get(_data, npc) is { } row)
            return template with
            {
                MaxHp = (long)Math.Ceiling(MonsterService.TemplateAttributes(_data, row.Row,
                (int)row.Level).GetValueOrDefault(AttributeId.MaxHp))
            };
        return null;
    }

    private static MapPoint AirshipStoryActorPoint(Attempt a, ReconstructedDungeons.SceneActor actor, MapPoint at) =>
        a.Plan.SceneEntry == AirshipScene && actor.NpcId == 5300022 && actor.Point == 42
            ? new MapPoint(241.56f, 228.65002f, 463.74f, 90) : at; // G 50882/31037，中間那一座。

    // G 黃QQ 1000501：31037/50761 出生，31081/50764–50820 接近及停船。
    // 座標與 dir 是實包觀察值；不是場景編輯器的地面導航點，也不是追擊玩家的 AI。
    private sealed record AirshipShipRoute(MapPoint Born, MapPoint From, MapPoint Stop,
        float Direction, int StartDelayMs, int DurationMs, int PauseDelayMs = 0, MapPoint? Pause = null);

    private sealed class AirshipShipMotion(AirshipShipRoute route)
    {
        public AirshipShipRoute Route { get; } = route;
        public long StartedMs;
        public int DurationMs;
        public float Speed;
        public bool Moving;
    }

    private static AirshipShipRoute? AirshipRoute(Attempt a, int npc, int index)
    {
        if (a.Plan.SceneEntry != AirshipScene || a.Wave != 1) return null;
        return (npc, index) switch
        {
            (5300015, 0) => new(new(346.8f, 207.65002f, 451.9f, 0),
                new(345.82486f, 207.65002f, 451.96817f, 0), new(317.00397f, 207.65002f, 453.98346f, 0),
                274.03094f, 232, 3399, 117, new(346.8f, 207.65002f, 451.9f, 0)),
            (5300053, 1) => new(new(332.9f, 207.65002f, 530.8f, 0),
                new(332.78604f, 207.65002f, 530.28534f, 0), new(285.02432f, 207.65002f, 499.9749f, 0),
                237.63416f, 466, 6655, 238, new(333.59702f, 207.65002f, 530.8f, 0)),
            (5300053, 2) => new(new(333.6f, 207.65002f, 358.8f, 0),
                new(332.89944f, 207.65002f, 359.46945f, 0), new(289.68f, 207.65002f, 400.7707f, 0),
                313.7329f, 243, 7033),
            _ => null,
        };
    }

    private bool StartAirshipShip(Attempt a, MonsterUnit ship, int index)
    {
        if (AirshipRoute(a, ship.Template.NpcId, index) is not { } route) return false;
        ship.StaysOutOfBattle = true;
        ship.KeepsDamage = true;
        var motion = new AirshipShipMotion(route);
        var prior = ship.EntityExtras;
        ship.EntityExtras = entity =>
        {
            prior?.Invoke(entity);
            entity.Face = 0; // G 出生、停止封包未帶 face；移動朝向由 faceMove + movementWalk.dir 決定。
            entity.MovementInfo = AirshipShipMovement(ship, motion, MonsterService.NowMs);
        };
        var state = AirshipState(a);
        bool Current() => a.Wave == 1 && !ship.Dead && ship.Hp > 0 && !ship.Removed;
        if (route.Pause is { } pause)
            AirshipLater(a, state, route.PauseDelayMs, () => SetAirshipShipPointAsync(ship, pause), Current);
        AirshipLater(a, state, route.StartDelayMs, () => BeginAirshipShipMotionAsync(a, ship, motion), Current);
        return true;
    }

    private async Task BeginAirshipShipMotionAsync(Attempt a, MonsterUnit ship, AirshipShipMotion motion)
    {
        motion.StartedMs = MonsterService.NowMs;
        motion.DurationMs = Math.Max(1, (int)Paced(motion.Route.DurationMs).TotalMilliseconds);
        // 正式節奏使用 NPCConfig._iSpeed=850；LAB 縮短劇情時同步縮短封包時間、提高速度。
        motion.Speed = ship.Template.SpeedCm / 100f * motion.Route.DurationMs / motion.DurationMs;
        motion.Moving = true;
        AirshipShipPosition(ship, motion.Route.From, motion.Route.Direction);
        await _world.MoveUnitAsync(ship, new MovementChangeNtf
        { Uid = ship.Uid, MovementInfo = AirshipShipMovement(ship, motion, motion.StartedMs) });
        await TickAirshipShipAsync(a, ship, motion);
    }

    private async Task TickAirshipShipAsync(Attempt a, MonsterUnit ship, AirshipShipMotion motion)
    {
        if (a.Settled || a.Room.Closed || a.Wave != 1 || ship.Removed || ship.Dead || ship.Hp <= 0)
        { motion.Moving = false; return; }
        long now = MonsterService.NowMs;
        float progress = Math.Clamp((now - motion.StartedMs) / (float)motion.DurationMs, 0, 1);
        AirshipShipPosition(ship, AirshipShipPoint(motion.Route, progress), motion.Route.Direction);
        _world.Map(ship.WorldScene).Moved(ship);
        if (progress >= 1)
        {
            motion.Moving = false;
            await SetAirshipShipPointAsync(ship, motion.Route.Stop);
        }
        else AirshipLater(a, AirshipState(a), 100, () => TickAirshipShipAsync(a, ship, motion));
    }

    private static MapPoint AirshipShipPoint(AirshipShipRoute route, float t) => new(
        route.From.X + (route.Stop.X - route.From.X) * t,
        route.From.Y + (route.Stop.Y - route.From.Y) * t,
        route.From.Z + (route.Stop.Z - route.From.Z) * t, route.Direction);

    private static void AirshipShipPosition(MonsterUnit ship, MapPoint at, float face)
    { ship.X = at.X; ship.Y = at.Y; ship.Z = at.Z; ship.Face = face; }

    private Task SetAirshipShipPointAsync(MonsterUnit ship, MapPoint at)
    {
        AirshipShipPosition(ship, at, 0);
        return _world.MoveUnitAsync(ship, new MovementChangeNtf
        { Uid = ship.Uid, MovementInfo = new MovementInfo { Pos = ship.Pos, ServerTime = MonsterService.NowMs } });
    }

    private static MovementInfo AirshipShipMovement(MonsterUnit ship, AirshipShipMotion motion, long now)
    {
        var info = new MovementInfo { Pos = ship.Pos, ServerTime = now };
        long remaining = motion.StartedMs + motion.DurationMs - now;
        if (!motion.Moving || ship.Removed || ship.Dead || ship.Hp <= 0 || remaining <= 0) return info;
        info.MoveType = MovementType.MoveTypeWalk;
        info.FaceMove = true;
        info.MoveDuration = (int)remaining;
        info.MovementWalk = new MovementWalk
        { Dir = motion.Route.Direction, Speed = motion.Speed, EndHeight = motion.Route.Stop.Y };
        return info;
    }
}
