using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A scripted step's cannons (ScriptedStep.Cannon, the airship's stage 2). The client holds the whole gameplay: NPCInteractConfig
/// 5300006 names GameplayStatusConfig 12 (type 10, enum_InteractState.CommonCannon), and GamePlayStatusExtraConfig[12] gives it
/// camera 19, the crosshair and the buttons 27 (fire, skill 5353001) and 28 (leave, action 5004). The server's 32702 with
/// upperCannonInfo.cannonGuId starts it (LC_GamePlayStatusMgr.CommonCannon): LC_CannonComponent seats the player on the cannon and
/// hands it the control, LC_TPSComponent aims by a ray from the camera's centre and sends what it hits as targetsId in the fire
/// skill's 31101 (LC_Actor_TPSInput._onSkillRelease). Damage and defeat are the ordinary combat's. What flies past is
/// a step's fight with a route (<see cref="FlyAsync"/>).
/// </summary>
public sealed partial class DungeonService
{
    /// <summary>The step's cannon interaction seats its user. Only the step that shows the cannons counts.</summary>
    private async Task BoardCannonAsync(Attempt a, GameSession s, SceneActorUnit actor, long interaction)
    {
        if (Step(a) is not { Cannon: { } cannon } || cannon.Interaction != interaction || a.Settled) return;
        long token = await _vehicles.SeatOnCannonAsync(s, actor.Uid, actor.NpcId, (int)interaction);
        if (token != 0) a.Riders[s] = token;
    }

    /// <summary>A gunner on a cannon of the running step fires that step's cannon skill.</summary>
    private bool FiresCannon(GameSession s, int skill) => _vehicles.IsOnCannon(s)
        && Active(s) is { } a && Step(a) is { Cannon: { } cannon } && cannon.Fire == skill;

    /// <summary>
    /// A unit of a step's fight on a scene-editor patrol: it stays out of the battle, keeps its damage, every player of the
    /// room shows it however far, and it flies the patrol once at its own speed and stays where the patrol ends (inferred:
    /// the client ships the patrols' points and no rule for them, and the original's server script is absent).
    /// </summary>
    private async Task FlyAsync(Attempt a, MonsterUnit unit, IReadOnlyList<(float X, float Y, float Z)> route)
    {
        unit.StaysOutOfBattle = true;
        unit.KeepsDamage = true;
        await _world.ShowFromAfarAsync(unit);
        await FlyLegAsync(a, unit, route, 1);
    }

    private async Task FlyLegAsync(Attempt a, MonsterUnit unit, IReadOnlyList<(float X, float Y, float Z)> route, int leg)
    {
        float speed = unit.Template.SpeedCm / 100f;
        if (a.Settled || a.Room.Closed || unit.Dead || leg >= route.Count || speed <= 0) return;
        var (from, to) = (route[leg - 1], route[leg]);
        float metres = MonsterService.Distance(from.X, from.Z, to.X, to.Z);
        float face = MonsterService.Angle(from.X, from.Z, to.X, to.Z);
        int duration = (int)Math.Max(1, metres / speed * 1000);
        (unit.X, unit.Y, unit.Z, unit.Face) = (to.X, to.Y, to.Z, face);
        await _world.MoveUnitAsync(unit, new MovementChangeNtf
        {
            Uid = unit.Uid, TransType = MovementTransitionType.MoveTransNormal,
            MovementInfo = new MovementInfo
            {
                Pos = new Vec3 { X = from.X, Y = from.Y, Z = from.Z }, ServerTime = MonsterService.NowMs,
                MoveDuration = duration, Face = face, MoveType = MovementType.MoveTypeWalk,
                MovementWalk = new MovementWalk { Dir = face, Speed = speed, Acc = 0, EndHeight = to.Y },
            },
        });
        _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(duration), () => FlyLegAsync(a, unit, route, leg + 1), "dungeon scripted flight");
    }
}
