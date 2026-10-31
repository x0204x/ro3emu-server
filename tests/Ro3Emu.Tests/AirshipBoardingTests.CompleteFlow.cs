using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class AirshipBoardingTests
{
    [ClientDataFact]
    public async Task OneRoleCompletesCargoCannonFlightAndCampThroughTheQuestAndSceneProtocols()
    {
        SureHitRolls.Install(_app); // LAB 固定命中；不把隨機閃避當成流程失敗。
        Service<Microsoft.Extensions.Options.IOptions<Ro3Emu.Server.ServerOptions>>().Value.ScriptedScenePacePercent = 10;
        await using var bot = await InWorld();
        long role = (long)bot.Self!.Uuid;
        await Dock(bot); // 僅以 GM 準備等級及貨物起點；往後不接受或完成任何後續任務。
        await BoardingDialogue(bot);
        await bot.SendGameAsync(TaskService.ExitCutSceneState, new ExitCutSceneStateReq());
        await PlotEnd(bot, 130041);
        await Barrier(bot);
        Assert.Equal(TaskState.Running, await Read(s => s.Tasks!.Infos[10103210].Status));
        long instance = await CompleteFlowBoard(bot);
        await CompleteFlowStage(bot, 2, 2);
        await CompleteFlowCannon(bot, instance);
        foreach (var (stage, count) in new[] { (3, 4), (4, 5) })
        {
            await CompleteFlowStage(bot, stage, 3);
            foreach (var enemy in await CompleteFlowWave(instance, count)) await CompleteFlowKill(bot, enemy);
        }
        await CompleteFlowStage(bot, 5, 4);
        var boss = Assert.Single(await CompleteFlowWave(instance, 1));
        Assert.Equal(5300018, boss.Template.NpcId);
        await CompleteFlowKill(bot, boss);
        await CompleteFlowStage(bot, 6, 5);
        await CompleteFlowStage(bot, 7, 6); // 戰後時間軸自行進行，不要求五次泡泡。
        await CompleteFlowCaptain(bot);
        await bot.SendGameAsync(TaskService.ExitCutSceneState, new ExitCutSceneStateReq());
        await PlotEnd(bot, 130162); // G 本地 QTE 成功影片的既有 601 結束包，沒有另造 QTE 請求。
        Assert.Equal(1, (await Next(bot, DungeonService.ResultNotify, DungeonResultNotify.Parser)).ResultInfo.WinCamp);
        await Barrier(bot);
        await CompleteFlowLand(bot);
        await CompleteFlowCampDialogue(bot);
        foreach (long task in new long[] { Cargo, 10103160, 10103170, 10103200, 10103201, 10103210, 10103220, 10104010 })
            Assert.Equal(1, StoredTasks(role).CompleteTaskNum[task]);
        foreach (long task in new long[] { Cargo, 10103200, 10103210, 10103220, 10104010 })
            Assert.Equal(1, AwardSources(role, task));
        Assert.Equal(TaskState.Running, await Read(s => s.Tasks!.Infos[10104020].Status));
        Assert.Equal(0, AwardSources(role, 10104020));
        Assert.Single(bot.Inbox.Snapshot(), packet => packet.IsGame(DungeonService.ResultNotify));
    }

    private async Task<long> CompleteFlowBoard(BotClient bot)
    {
        Assert.Equal(0, await EnterAirship(bot));
        var change = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, message => message.SceneEntry == 1000501);
        Assert.NotEqual(change.SceneEntry, change.SceneID);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 1000501 });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, message => message.SceneId == 1000501)).Result);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        await CompleteFlowStage(bot, 1, 1);
        Assert.Equal(0, await Read(s => s.Tasks!.Infos[10103210].CompleteNum[0]));
        return change.SceneID;
    }

    private static Task<DungeonBaseInfoNotify> CompleteFlowStage(BotClient bot, int stage, int quest) =>
        Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser,
            info => info.Stage == stage && info.Vars.Any(value => value.VarId == 10001 && value.Vi64.LastOrDefault() == quest));

    private static async Task<int> CompleteFlowInteract(BotClient bot, DungeonService.SceneActorUnit actor, int interaction)
    {
        await bot.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        { NpcUid = actor.Uid, InteractionId = interaction, SceneConfigId = actor.SceneEntry });
        return (await Next(bot, SceneService.NpcInteraction, NpcInteractionRsp.Parser,
            response => response.Req.NpcUid == actor.Uid && response.Req.InteractionId == interaction)).Result;
    }

    private Task CompleteFlowBeside(float x, float y, float z) => OnLoop(() =>
        Service<SceneService>().TeleportInMapAsync(Session, new MapPoint(x + 1, y, z, 0))); // LAB 定位，保留 AOI／移動通知。

    private async Task<MonsterUnit[]> CompleteFlowWave(long instance, int count)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            var enemies = await Read(_ => Service<World>().AllUnits().OfType<MonsterUnit>()
                .Where(enemy => enemy.InstanceId == instance && !enemy.Dead && enemy.Hp > 0).ToArray());
            if (enemies.Length == count) return enemies;
            await Task.Delay(20);
        }
        throw new TimeoutException($"飛空艇 {instance} 未產生預期的 {count} 隻敵人。");
    }

    private async Task CompleteFlowCannon(BotClient bot, long instance)
    {
        long role = Session.Role!.Id;
        var cannon = await Read(s => s.DungeonAttempt!.SceneActors.First(actor => actor.NpcId == 5300022));
        var ships = await CompleteFlowWave(instance, 3);
        var visible = await CompleteFlowVisibleShips(bot);
        Assert.Equal(new ulong[] { 5300015, 5300053, 5300053 }, visible.Select(ship => ship.EntityTemplateID));
        var hp = await Read(_ => ships.Select(ship => ship.Hp).ToArray());
        await CompleteFlowBeside(cannon.X, cannon.Y, cannon.Z);
        await CompleteFlowMount(bot, cannon);
        Assert.Equal(hp, await Read(_ => ships.Select(ship => ship.Hp).ToArray())); // 32700 登砲不能造成傷害。
        await CompleteFlowShot(bot, cannon, visible[0], ships.Single(ship => ship.Uid == (long)visible[0].Uuid));
        await bot.SendGameAsync(WorldInteractionService.Action, new UseInteractActionReq { ButtonId = 28 });
        Assert.Equal(0, (await Next(bot, WorldInteractionService.Action, UseInteractActionRes.Parser)).Result);
        await Next(bot, WorldInteractionService.Change, InteractChangeNtf.Parser,
            change => change.Uid == role && change.DataInfo.EType == InteractInfo.Types.EState.Idle);
        await Next(bot, WorldInteractionService.Change, InteractChangeNtf.Parser,
            change => change.Uid == cannon.Uid && change.DataInfo.EType == InteractInfo.Types.EState.Idle);
        Assert.Empty(await Read(s => s.DungeonAttempt!.CannonUsers.ToArray()));
        await Task.Delay(600); // G 5353001 的動作／CD 500 ms；退出後先等原施放結束。
        await CompleteFlowMount(bot, cannon);
        await CompleteFlowShot(bot, cannon, visible[1], ships.Single(ship => ship.Uid == (long)visible[1].Uuid));
        Assert.Equal(1, await Read(s => s.DungeonAttempt!.Wave)); // 尚有第三艘，不能提早換階段。
        await Task.Delay(600);
        await CompleteFlowShot(bot, cannon, visible[2], ships.Single(ship => ship.Uid == (long)visible[2].Uuid));
        await Next(bot, WorldInteractionService.Change, InteractChangeNtf.Parser,
            change => change.Uid == role && change.DataInfo.EType == InteractInfo.Types.EState.Idle && change.Interrupt);
        await Next(bot, WorldInteractionService.Change, InteractChangeNtf.Parser,
            change => change.Uid == cannon.Uid && change.DataInfo.EType == InteractInfo.Types.EState.Idle && change.Interrupt);
        Assert.Empty(await Read(s => s.DungeonAttempt!.CannonUsers.ToArray())); // 換階段也必須解除砲位。
    }

    private async Task CompleteFlowMount(BotClient bot, DungeonService.SceneActorUnit cannon)
    {
        Assert.Equal(0, await CompleteFlowInteract(bot, cannon, 5300006));
        var mounted = await Next(bot, WorldInteractionService.Change, InteractChangeNtf.Parser,
            change => change.Uid == Session.Role!.Id && change.DataInfo.EType == InteractInfo.Types.EState.UpperCannon);
        Assert.Equal(cannon.Uid, mounted.DataInfo.UpperCannonInfo.CannonGuId);
        var cannonMounted = await Next(bot, WorldInteractionService.Change, InteractChangeNtf.Parser,
            change => change.Uid == cannon.Uid && change.DataInfo.EType == InteractInfo.Types.EState.UpperCannon);
        Assert.Equal(cannon.Uid, cannonMounted.DataInfo.UpperCannonInfo.CannonGuId);
        Assert.Equal(0, cannonMounted.DataInfo.InteractConfigId);
    }

    private async Task CompleteFlowShot(BotClient bot, DungeonService.SceneActorUnit cannon, EntityBase visible, MonsterUnit ship)
    {
        await OnLoop(() =>
        {
            // LAB 僅把敵艦降為 1 HP；真實 31102／5353001 仍須命中、扣血、死亡及推進副本。
            ship.Target = null; ship.PetTarget = null; ship.Returning = false; ship.Moving = false; ship.Hp = 1;
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 5353001, OpUnitId = cannon.Uid, TargetsId = { (long)visible.Uuid }, TargetPos = visible.MovementInfo.Pos.Clone() });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var timeline = await Next(bot, CombatService.UseInSkillHigh, UseInSkillInfo.Parser,
            use => use.SkillID == 5353001 && use.SessionId == accepted.SessionId);
        Assert.Equal(Session.Role!.Id, timeline.SrcID);
        Assert.Equal(ship.Uid, timeline.TargetId);
        var projectile = await Next(bot, CombatService.SkillBulletHigh, SkillFixTimeBulletNotify.Parser,
            bullet => bullet.SkillConfigId == 5353001 && bullet.EffectId == 6120105);
        Assert.Equal(Session.Role!.Id, projectile.SourceId);
        Assert.Equal(0, projectile.TargetId);
        Assert.True(projectile.FixedTime > 0);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            hit => hit.SkillId == 5353001 && hit.Dsts.Any(target => target.DstID == ship.Uid && target.IsDead));
    }

    private static async Task<EntityBase[]> CompleteFlowVisibleShips(BotClient bot)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            var visible = new Dictionary<ulong, EntityBase>();
            foreach (var packet in bot.Inbox.Snapshot().Where(packet => packet.IsGame(GameAoiSink.SyncAoiView)))
            {
                var view = AoIViewChangeData.Parser.ParseFrom(packet.Body);
                foreach (ulong gone in view.LeaveAoi) visible.Remove(gone);
                foreach (var actor in view.Bases) visible[actor.Uuid] = actor;
            }
            var ships = visible.Values.Where(actor => actor.EntityTemplateID is 5300015 or 5300053)
                .OrderBy(actor => actor.EntityTemplateID).ToArray();
            if (ships.Length == 3) return ships;
            await Task.Delay(20);
        }
        throw new TimeoutException("客戶端未透過 31037 收到三艘敵艦。");
    }

    private async Task CompleteFlowKill(BotClient bot, MonsterUnit enemy)
    {
        await SwingWait.UntilDoneAsync(Service<GameLoop>(), Session, enemy.Uid);
        await OnLoop(async () =>
        {
            await Service<SceneService>().TeleportInMapAsync(Session, new MapPoint(enemy.X + 1, enemy.Y, enemy.Z, enemy.Face));
            Service<MonsterService>().Engage(enemy, Session);
            enemy.Hp = 1; // LAB 非砲台戰鬥速殺，仍走正式技能、傷害與死亡管線。
            CombatTestFixture.ReadyNormalAttack(Service<GameLoop>(), Session);
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = enemy.Uid });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            hit => hit.Dsts.Any(target => target.DstID == enemy.Uid && target.IsDead));
    }

    private async Task CompleteFlowCaptain(BotClient bot)
    {
        var captain = await Read(s => Assert.Single(s.DungeonAttempt!.SceneActors, actor => actor.NpcId == 5300038));
        Assert.Equal(5300038, captain.NpcId);
        await CompleteFlowBeside(captain.X, captain.Y, captain.Z);
        Assert.Equal(0, await CompleteFlowInteract(bot, captain, 5300035)); // 正式船長對話。
        Assert.Equal(0, await CompleteFlowInteract(bot, captain, 5300039)); // 對話選項的 211 動作。
        var opening = await Next(bot, DungeonService.Event, DungeonEventNotify.Parser,
            message => (int)message.EventType == 4
                && message.Vars.Any(value => value.VarId == DungeonService.StoryVar && value.Vi64.Contains(130161)));
        Assert.Equal(new long[] { 130161 }, opening.Vars.Single(value => value.VarId == DungeonService.StoryVar).Vi64);
        Assert.False(await Read(s => s.DungeonAttempt!.Settled));
    }

    private async Task CompleteFlowLand(BotClient bot)
    {
        await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, message => message.SceneEntry == 10101);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 10101 });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, message => message.SceneId == 10101)).Result);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        Assert.True(await Read(s => s.SceneEntry == 10101 && s.InstanceId == 0 && !s.SceneLoading));
        // 原服先自動回港，再使用已交付任務的30131進入墜落營地。
        var request = new ClientCustomJumpMapReq
        { SceneId = 10514, PointId = 1040401, CustomType = EClientCustomJumpMapType.TaskDelivery, TypeParam1 = 10103210 };
        await bot.SendGameAsync(SceneService.CustomJumpMap, request);
        Assert.Equal(0, (await Next(bot, SceneService.CustomJumpMap, ClientCustomJumpMapResp.Parser)).Result);
        await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, message => message.SceneEntry == 10514);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 10514 });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, message => message.SceneId == 10514)).Result);
        var self = (await Next(bot, MsgId.EntitySelf, EntitySelf.Parser)).Base;
        var map = Service<WorldData>().Map(10514)!;
        var arrival = map.Grounded(map.Points[1040401]);
        Assert.Equal(arrival.X, self.MovementInfo.Pos.X, 2);
        Assert.Equal(arrival.Z, self.MovementInfo.Pos.Z, 2);
        Assert.True(await Read(s => s.SceneEntry == 10514 && s.InstanceId == 0 && !s.SceneLoading && !s.InCutScene));
    }

    private async Task CompleteFlowCampDialogue(BotClient bot)
    {
        Assert.Equal(TaskState.Running, await Read(s => s.Tasks!.Infos[10104010].Status));
        var target = Service<GameData>().Row("TaskConfig", 10104010)!.Value.GetProperty("_kProgressTarget")[0];
        long point = target[1].GetInt64();
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session, Service<WorldData>().Map(10514)!.Points[point]));
        await bot.SendGameAsync(SceneService.ClientNpcInteract, new ClientNpcInteractReq
        { DialogID = target[3].GetInt32(), Areapointid = point });
        await Barrier(bot);
        await Next(bot, TaskService.TaskInfoSync, DBTaskInfoIncrement.Parser,
            update => update.Infos.TryGetValue(10104020, out var info) && info.All?.Status == TaskState.Running);
    }
}
