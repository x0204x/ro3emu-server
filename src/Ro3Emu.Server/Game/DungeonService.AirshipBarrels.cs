using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    private const int AirshipBarrelNpc = 5300020;
    private const ulong AirshipBarrelStateMask = 1024, AirshipBarrelForbidMask = 1281;
    private sealed record AirshipBarrelRoute(int Delay, float X, float Z, float MoveX, float MoveZ,
        int MoveDelay, int TravelMs, int LifeMs, float Direction = 0);

    // 原服 seq53455–53728：5352801 傾斜後的 11 個酒桶；每筆 31081／leave_aoi 決定路徑與生命週期。
    // (inferred) 將這次觀察到的排列用於每次有效傾斜，不宣稱原服隨機選道規則已還原。
    private static readonly AirshipBarrelRoute[] AirshipBarrelRoutes =
    [
        new(106, 238.84f, 448.181f, 238.84f, 448.76102f, 133, 6117, 6389),
        new(1120, 228.66f, 440.351f, 228.66f, 440.941f, 173, 7155, 6281),
        new(2133, 235.44f, 445.481f, 235.44f, 446.066f, 133, 6003, 6162),
        new(3147, 227.12f, 448.181f, 227.12f, 448.76102f, 174, 6103, 6148),
        new(4161, 228.66f, 440.351f, 228.66f, 440.92603f, 146, 7158, 6241),
        new(5188, 238.84f, 448.181f, 238.84f, 448.751f, 145, 6119, 6108),
        new(6201, 232.36f, 447.76f, 232.35701f, 448.33002f, 173, 6128, 6202, 359.77972f),
        new(7215, 228.66f, 440.351f, 228.66f, 440.92603f, 134, 7158, 6189),
        new(8229, 238.84f, 448.181f, 238.84f, 448.756f, 146, 6118, 6069),
        new(9254, 238.84f, 448.181f, 238.84f, 448.76102f, 108, 6117, 6044),
        new(9254, 232.36f, 447.76f, 232.35696f, 448.34f, 121, 6126, 6044, 359.77972f),
    ];

    private void ScheduleAirshipBarrels(Attempt a, AirshipStoryState state, MonsterUnit boss, long serial)
    {
        if (state.BarrelSerial == serial) return;
        state.BarrelSerial = serial;
        foreach (var route in AirshipBarrelRoutes)
            AirshipLater(a, state, route.Delay, () => SpawnAirshipBarrelAsync(a, state, route),
                () => a.Wave == 4 && !boss.Dead && boss.Hp > 0 && !boss.Removed && state.BarrelSerial == serial);
    }

    private async Task SpawnAirshipBarrelAsync(Attempt a, AirshipStoryState state, AirshipBarrelRoute route)
    {
        if (NPCConfig.Get(_data, AirshipBarrelNpc) is not { } config || _monsters.Template(AirshipBarrelNpc) is not { } template) return;
        var actor = new SceneActorUnit
        {
            Uid = SceneActorUidBase | Interlocked.Increment(ref _sceneActorSerial), SceneEntry = a.Plan.SceneEntry,
            InstanceId = a.Room.Id, NpcId = AirshipBarrelNpc, Point = 0, EntityType = (int)config.NPCType,
            Camp = config.Camp, Name = "", BornMs = MonsterService.NowMs, Template = template,
            X = route.X, Y = 228.65002f, Z = route.Z,
        };
        actor.StoryEntityExtras = entity =>
        {
            entity.ObjState = new AoiObjStateInfo { Data = new CommonBigMask { Values = { 0, AirshipBarrelStateMask, 0 } } };
            entity.ObjForbid = new AoiObjForbidInfo { Data = new CommonBigMask { Values = { AirshipBarrelForbidMask } } };
        };
        if (!await _instances.AddPropAsync(a.Room, actor)) return;
        state.Barrels.Add(actor);
        AirshipLater(a, state, route.MoveDelay, () => StartAirshipBarrelMotionAsync(a, state, actor, route), () => a.Wave == 4 && !actor.Removed);
        AirshipLater(a, state, route.LifeMs, () => RemoveAirshipBarrelAsync(a, state, actor));
    }

    private async Task StartAirshipBarrelMotionAsync(Attempt a, AirshipStoryState state, SceneActorUnit actor, AirshipBarrelRoute route)
    {
        if (actor.Template is not { SpeedCm: > 0 } template) return;
        long started = MonsterService.NowMs;
        int duration = Math.Max(1, (int)Paced(route.TravelMs).TotalMilliseconds);
        float speed = template.SpeedCm / 100f * route.TravelMs / duration;
        actor.X = route.MoveX; actor.Z = route.MoveZ; actor.Face = route.Direction;
        var extras = actor.StoryEntityExtras;
        actor.StoryEntityExtras = entity =>
        {
            extras?.Invoke(entity);
            entity.MovementInfo = AirshipBarrelMovement(actor, started, duration, speed, route.Direction);
        };
        await _world.MoveUnitAsync(actor, new MovementChangeNtf
        { Uid = actor.Uid, MovementInfo = AirshipBarrelMovement(actor, started, duration, speed, route.Direction) });
        await TickAirshipBarrelAsync(a, state, actor, route, started, duration, speed);
    }

    private async Task TickAirshipBarrelAsync(Attempt a, AirshipStoryState state, SceneActorUnit actor,
        AirshipBarrelRoute route, long started, int duration, float speed)
    {
        if (actor.Removed) return;
        if (a.Wave != 4 || a.Settled || a.Room.Closed) { await RemoveAirshipBarrelAsync(a, state, actor); return; }
        float seconds = Math.Clamp(MonsterService.NowMs - started, 0, duration) / 1000f;
        double radians = route.Direction * Math.PI / 180;
        actor.X = route.MoveX + (float)Math.Sin(radians) * speed * seconds;
        actor.Z = route.MoveZ + (float)Math.Cos(radians) * speed * seconds;
        _world.Map(actor.WorldScene).Moved(actor);
        if (MonsterService.NowMs - started < duration)
            AirshipLater(a, state, 100, () => TickAirshipBarrelAsync(a, state, actor, route, started, duration, speed));
        else await _world.MoveUnitAsync(actor, new MovementChangeNtf
        { Uid = actor.Uid, MovementInfo = new MovementInfo { Pos = actor.Pos, ServerTime = MonsterService.NowMs } });
    }

    private static MovementInfo AirshipBarrelMovement(SceneActorUnit actor, long started, int duration, float speed, float direction)
    {
        long now = MonsterService.NowMs;
        var info = new MovementInfo { Pos = actor.Pos, Face = actor.Face, ServerTime = now };
        if (started + duration <= now) return info;
        info.MoveType = MovementType.MoveTypeWalk; info.FaceMove = true; info.MoveDuration = (int)(started + duration - now);
        info.MovementWalk = new MovementWalk { Dir = direction, Speed = speed, EndHeight = actor.Y };
        return info;
    }

    private async Task RemoveAirshipBarrelAsync(Attempt a, AirshipStoryState state, SceneActorUnit actor)
    {
        if (state.Barrels.Remove(actor)) await _instances.RemovePropAsync(a.Room, actor);
    }

    private async Task ClearAirshipBarrelsAsync(Attempt a, AirshipStoryState state)
    {
        state.BarrelSerial = 0;
        foreach (var actor in state.Barrels.ToArray()) await RemoveAirshipBarrelAsync(a, state, actor);
    }
}
