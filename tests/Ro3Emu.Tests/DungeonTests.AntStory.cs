using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    // 這些測試使用私人真實匯出，驗證伺服器／TCP契約；不等同原生客戶端碰撞、鏡頭或操作驗收。
    private static readonly (int Npc, int Interaction, int Buff)[] AntInteractionOrder =
    [
        (5300041, 5300028, 5301401), (5300005, 5300008, 5365101),
        (5300055, 5300010, 5365201), (5300005, 5300009, 5365301), (5300005, 5300005, 5365401),
    ];

    [ClientDataTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task TheAntSlideRestoresItsCustomDataOnAbandonOrStaleGeneration(bool staleGeneration, bool failedNotification)
    {
        await using var alice = await InWorld("Alice");
        var session = Session(alice);
        var previous = new CustomData { Id = 5400005, CustomParam = new CustomParam { Tp = (CustomParam.Types.ParamType)3, LongParam = 18 } };
        await OnLoop(() => Service<WorldCoreService>().SetCustomAsync(session, previous));
        await EnterAntSlideForTestAsync(alice);
        Assert.Equal(30d, Service<DungeonService>().AntStoryMovementSpeed(session));
        Assert.Equal([600L, 3000L], session.WorldState.CustomData[5400003].CustomParam.ListLongParam);
        var customWriter = Service<DungeonService>().SetAntCustomAsync;
        if (failedNotification) Service<DungeonService>().SetAntCustomAsync = (_, _) => Task.FromException(new IOException("LAB: custom cleanup notification fails"));
        try
        {
            await OnLoop(async () =>
            {
                if (staleGeneration)
                {
                    // 舊 callback／狀態不可替新 generation 放寬走路或保留滑行。
                    session.DungeonAttempt!.StepGeneration++;
                    Assert.Null(Service<DungeonService>().AntStoryMovementSpeed(session));
                    await Service<DungeonService>().PlayerMovedAsync(session);
                }
                else await Service<DungeonService>().AbandonAsync(session);
            });
        }
        finally { Service<DungeonService>().SetAntCustomAsync = customWriter; }
        Assert.Null(Service<DungeonService>().AntStoryMovementSpeed(session));
        Assert.Equal(previous, session.WorldState.CustomData[5400005]);
        Assert.DoesNotContain(DungeonService.AntSlideState, session.WorldState.CustomData.Keys);
        Assert.DoesNotContain(5400003L, session.WorldState.CustomData.Keys);
        if (!failedNotification)
            await Next(alice, WorldCoreService.CustomChange, CustomDataChange.Parser,
                p => p.CustomDatas.Any(c => c.Id == DungeonService.AntSlideState && c.CustomParam.LongParam == 0));
    }

    [ClientDataFact]
    public async Task TheAntSlideDoesNotRestoreItsOldValuesIntoAReplacementWorldSnapshot()
    {
        await using var alice = await InWorld("Alice");
        await EnterAntSlideForTestAsync(alice);
        var session = Session(alice);
        var old = session.WorldState;
        var replacement = new WorldSessionState();
        var marker = new CustomData { Id = 5400005, CustomParam = new CustomParam { Tp = (CustomParam.Types.ParamType)3, LongParam = 97 } };
        replacement.CustomData[marker.Id] = marker.Clone();
        await OnLoop(async () =>
        {
            // 重連快照置換的fixture；不宣稱實際連線已完成重新登入。
            session.WorldState = replacement;
            Assert.Null(Service<DungeonService>().AntStoryMovementSpeed(session));
            await Service<DungeonService>().AbandonAsync(session);
        });
        Assert.Same(replacement, session.WorldState);
        Assert.Equal(marker, replacement.CustomData[marker.Id]);
        Assert.DoesNotContain(DungeonService.AntSlideState, old.CustomData.Keys);
        Assert.DoesNotContain(DungeonService.AntSlideState, replacement.CustomData.Keys);
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheDeniroFailedRidePrecedesThePersistentVehicleAndOnlyItsRiderCanFinish(bool exitByButton)
    {
        await using var alice = await InWorld("Alice");
        var deniro = await PrepareAntDeniroAsync(alice, 4);
        var session = Session(alice);
        var attempt = session.DungeonAttempt!;
        var end = Service<WorldData>().Map(1000301)!.Regions[31];
        await WalkInto(alice, end);
        Assert.False(attempt.Settled); // 未騎乘，不得徒步踩尾端就發獎。
        await Beside(alice, deniro.X, deniro.Y, deniro.Z);
        Assert.Equal(0, await Interact(alice, deniro, 5300005));
        var carried = await Next(alice, GameAoiSink.MovementChange, MovementChangeNtf.Parser,
            m => m.Uid == session.Role!.Id && m.TransType == MovementTransitionType.MoveTransBeCarriedMove);
        Assert.Equal(deniro.Y + 2.8f, carried.MovementInfo.Pos.Y, 3); // 真實 VehicleSeatConfig._kOffset。
        await Next(alice, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == session.Role!.Id && n.DataInfo.InteractConfigId == 5300005);
        Assert.Equal(12d, Service<DungeonService>().AntStoryMovementSpeed(session));
        Assert.False(attempt.Settled); // 上車不是完成條件，也不啟動舊 guide36 自動推進。
        if (exitByButton)
        {
            await alice.SendGameAsync(WorldInteractionService.Action, new UseInteractActionReq { ButtonId = 26 });
            Assert.Equal(0, (await Next(alice, WorldInteractionService.Action, UseInteractActionRes.Parser)).Result);
            Assert.Null(Service<DungeonService>().AntStoryMovementSpeed(session));
            Assert.Equal(InteractInfo.Types.EState.Idle, deniro.Entity().InteractInfo.EType);
            await Next(alice, WorldInteractionService.Change, InteractChangeNtf.Parser,
                n => n.Uid == deniro.Uid && n.DataInfo.EType == InteractInfo.Types.EState.Idle);
            Assert.False(attempt.Settled);
            await Beside(alice, deniro.X, deniro.Y, deniro.Z);
            Assert.Equal(0, await Interact(alice, deniro, 5300005));
            await Until(() => Service<WorldInteractionService>().IsSeatedOnSceneActor(session), "the rider boards again without a walk");
        }
        await OnLoop(() => Service<WorldInteractionService>().PutDownFromSceneActorAsync(session, -1));
        Assert.True(Service<WorldInteractionService>().IsSeatedOnSceneActor(session)); // 舊 token 不可拆掉新座位。
        await alice.SendGameAsync(MsgId.Walk, new WalkReq
        {
            Pos = new Vec3 { X = session.Position.X + 0.5f, Y = session.Position.Y, Z = session.Position.Z },
            Face = 90, Dir = 90, SpeedRate = 1,
        });
        var moving = await Next(alice, GameAoiSink.MovementChange, MovementChangeNtf.Parser,
            m => m.Uid == deniro.Uid && m.MovementInfo.MoveType == MovementType.MoveTypeMove);
        Assert.Equal(MovementTransitionType.MoveTransNormal, moving.TransType);
        Assert.Equal(12f, moving.MovementInfo.MovementMove.StartVelocity.X, 3);
        await WalkInto(alice, end); // 測試輔助短程TCP walk，並非原生客戶端實走整段路。
        var result = await Next(alice, DungeonService.ResultNotify, DungeonResultNotify.Parser);
        Assert.Equal(1, result.ResultInfo.WinCamp);
        await StoryMovesOn(alice, 10104300, 10104310);
        Assert.False(Service<WorldInteractionService>().IsSeatedOnSceneActor(session));
        Assert.Null(Service<DungeonService>().AntStoryMovementSpeed(session));
    }

    [ClientDataFact]
    public async Task TheDeniroPositionReportIsBoundToTheCurrentActorAndItsOfferedTarget()
    {
        await using var alice = await InWorld("Alice");
        var deniro = await PrepareAntDeniroAsync(alice, 2);
        var session = Session(alice);
        var before = deniro.Pos.Clone();
        await OnLoop(async () =>
        {
            Assert.True(await Service<DungeonService>().TrySyncAntActorPositionAsync(session, new ClientTeleportObject
            { TargetUid = deniro.Uid, TargetPos = new Vec3 { X = 600, Y = 251, Z = 600 } }));
            Assert.True(await Service<DungeonService>().TrySyncAntActorPositionAsync(session, new ClientTeleportObject
            { TargetUid = deniro.Uid + 1, TargetPos = new Vec3 { X = 69, Y = 251, Z = 292 } }));
        });
        Assert.Equal(before, deniro.Pos);
        await SyncAntDeniroAsync(alice, deniro);
        Assert.Equal((69f, 251f, 292f), (deniro.X, deniro.Y, deniro.Z));
        await OnLoop(async () =>
        {
            session.DungeonAttempt!.StepGeneration++;
            Assert.True(await Service<DungeonService>().TrySyncAntActorPositionAsync(session, new ClientTeleportObject
            { TargetUid = deniro.Uid, TargetPos = new Vec3 { X = 70, Y = 251, Z = 292 } }));
        });
        Assert.Equal(69f, deniro.X); // 舊 generation 的合法目標也不得再移動 NPC。
    }

    [ClientDataFact]
    public async Task TheDeniroCameraTargetCannotRunAfterTheStepHasChanged()
    {
        Service<IOptions<ServerOptions>>().Value.ScriptedScenePacePercent = 10;
        await using var alice = await InWorld("Alice");
        await PrepareAntDeniroAsync(alice, 2);
        var session = Session(alice);
        await Task.Delay(600); // 當前本機 game-loop timer；不啟動任何客戶端。
        Assert.Equal(2, session.DungeonAttempt!.Wave);
        var targets = alice.Inbox.Snapshot().Where(e => e.IsGame(DungeonService.Event))
            .Select(e => DungeonEventNotify.Parser.ParseFrom(e.Body)).SelectMany(e => e.Vars)
            .Where(v => v.VarId == 1100 && v.Vi64.Count == 4).ToArray();
        Assert.Equal([69L, 251L, 292L], targets[^1].Vi64.Skip(1));
        Assert.Single(alice.Inbox.Snapshot().Where(e => e.IsGame(DungeonService.Event))
            .Select(e => DungeonEventNotify.Parser.ParseFrom(e.Body)), e => e.CliFuncname == "ActiveCameraTimeline");
    }

    private async Task<DungeonService.SceneActorUnit> PrepareAntDeniroAsync(BotClient alice, int completedSteps)
    {
        await Gm(alice, "!quest accept 10104300");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = 1000301 }));
        await Load(alice, 1000301);
        for (int step = 0; step < completedSteps; step++)
        {
            await Stage(alice, step + 1, quest: step + 1);
            var expected = AntInteractionOrder[step];
            var actors = await ActorsWhen(alice, all => all.Count(a => a.Buffs.Count > 0) == 1
                && all.Any(a => a.NpcId == expected.Npc && a.Buffs.Select(b => b.ConfigId).SequenceEqual([expected.Buff])));
            if (step == 2) await SyncAntDeniroAsync(alice, actors.Single(a => a.NpcId == 5300005));
            var actor = actors.Single(a => a.NpcId == expected.Npc);
            await Beside(alice, actor.X, actor.Y, actor.Z);
            Assert.Equal(0, await Interact(alice, actor, expected.Interaction));
            Assert.False(Service<WorldInteractionService>().IsSeatedOnSceneActor(Session(alice)));
        }
        await Stage(alice, completedSteps + 1, quest: completedSteps + 1);
        return (await ActorsWhen(alice, all => all.Any(a => a.NpcId == 5300005))).Single(a => a.NpcId == 5300005);
    }

    private async Task SyncAntDeniroAsync(BotClient alice, DungeonService.SceneActorUnit deniro)
    {
        await alice.SendGameAsync(WorldCoreService.TeleportObject, new ClientTeleportObject
        { TargetUid = deniro.Uid, TargetPos = new Vec3 { X = 69, Y = 251, Z = 292 } });
        await Next(alice, GameAoiSink.MovementChange, MovementChangeNtf.Parser,
            m => m.Uid == deniro.Uid && m.MovementInfo?.Pos is { X: 69, Z: 292 });
    }

    private async Task EnterAntSlideForTestAsync(BotClient alice)
    {
        await Gm(alice, "!quest accept 10104290");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = 1000201 }));
        await Load(alice, 1000201);
        await Stage(alice, 1, quest: 1);
        await WalkInto(alice, Service<WorldData>().Map(1000201)!.Regions[25]);
        await Stage(alice, 2, quest: 2);
        var vortex = Assert.Single(await ActorsIn(alice, 1, a => a.NpcId == 5300030));
        await Beside(alice, vortex.X, vortex.Y, vortex.Z);
        Assert.Equal(0, await Interact(alice, vortex, 5300030));
        await Stage(alice, 3, quest: 3);
        await Next(alice, WorldCoreService.CustomChange, CustomDataChange.Parser,
            p => p.CustomDatas.Any(c => c.Id == DungeonService.AntSlideState && c.CustomParam.LongParam == 1));
    }

    private async Task BoardAntDeniroForRegionAsync(BotClient alice)
    {
        var deniro = Assert.Single(await ActorsWhen(alice, all => all.Any(a => a.NpcId == 5300005
            && a.Buffs.Any(b => b.ConfigId == 5365401))), a => a.NpcId == 5300005);
        var view = await ClientSees(alice, deniro.Uid, [5365401]);
        int offered = Reachable(deniro.NpcId, 5300005);
        Assert.True(ConditionHolds(offered, view.BuffGroups));
        await Gm(alice, $"!actors {deniro.NpcId}");
        await Until(() => ClientPlayerAt(alice, Session(alice).Role!.Id, onSurface: true) is { } current
            && MonsterService.Distance(current.X, current.Z, deniro.X, deniro.Z) < 1.5f, "the modeled client receives the position beside Deniro");
        var at = ClientPlayerAt(alice, Session(alice).Role!.Id, onSurface: true);
        Assert.NotNull(at);
        Assert.Equal(deniro.Uid, Possessed(alice, at.X, at.Y, at.Z));
        Assert.Equal(0, await Interact(alice, deniro, 5300005));
        await Next(alice, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == Session(alice).Role!.Id && n.DataInfo.EType == InteractInfo.Types.EState.Vehicle);
        Assert.Equal(InteractInfo.Types.EState.Vehicle, deniro.Entity().InteractInfo.EType);
    }
}
