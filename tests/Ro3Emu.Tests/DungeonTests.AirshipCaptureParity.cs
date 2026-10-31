using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    /// <summary>只讀真實客戶端資料；不複製原件為合成 fixture，不把計時完成當作玩家動畫驗收。</summary>
    [ClientDataFact]
    public async Task AirshipOpeningNeedsNoFourBubbleGateAndCannonWaitsForItsOwnOpening()
    {
        await using var alice = await InWorld("Alice");
        AirshipStoryTestPace();
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10103210");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = LifecycleAirship }));
        long instance = await Load(alice, LifecycleAirship);
        await Stage(alice, 1, quest: 1);
        var bobo = Assert.Single(await ActorsIn(alice, 1, actor => actor.NpcId == 5300031));
        await Beside(alice, bobo.X, bobo.Y, bobo.Z);
        Assert.Equal(0, await Interact(alice, bobo, 5300031));
        Assert.Equal(0, await Interact(alice, bobo, 5300031));
        await PlotEnd(alice, 5300541); // 偽帶內部計時 ID 的 plot，不能推進。
        await PlotEnd(alice, 130162);
        await alice.HeartbeatAsync();
        Assert.Equal(0, Session(alice).DungeonAttempt!.Wave);

        await Stage(alice, 2, quest: 1); // 三艦先出現，開砲前仍顯示前一項。
        var cannon = (await ActorsIn(alice, 3, actor => actor.NpcId == 5300022))[0];
        await Beside(alice, cannon.X, cannon.Y, cannon.Z);
        Assert.Equal(ErrorCodes.NpcParameter, await Interact(alice, cannon, 5300006));
        var ships = await WaveIn(instance, 3);
        Assert.Equal(3, ships.Select(s => (s.Home.X, s.Home.Y, s.Home.Z)).Distinct().Count());
        Assert.All(ships, s => Assert.Equal(207.65002f, s.Home.Y, 2));
        await Stage(alice, 2, quest: 2);
        Assert.Equal(0, await Interact(alice, cannon, 5300006));
        await alice.HeartbeatAsync();
        var calls = AirshipClientCalls(alice);
        Assert.Contains(calls, call => call.CliFuncname == "ActiveCameraTimeline"
            && call.Params[0].StrParams.Contains("mainstoryfeikongtingcamera1.prefab"));
        Assert.Contains(calls, call => call.CliFuncname == "ActiveCameraTimeline"
            && call.Params[0].StrParams.Contains("mainstoryfeikongtingcamera2.prefab"));
        Assert.Contains(calls, call => call.CliFuncname == "Shouting" && call.Params[0].IntParams.Contains(5300554));
    }

    [ClientDataFact]
    public async Task AirshipCaptainRepeatedOptionOpensOneQteAndEarlyEndingCannotSkipTheDialogue()
    {
        await using var alice = await InWorld("Alice");
        await AirshipLifecycleAtCaptain(alice);
        var captain = Assert.Single(await ActorsIn(alice, 1, actor => actor.NpcId == LifecycleCaptain));
        Assert.Equal(0, await Interact(alice, captain, LifecycleCaptainTalk));
        Assert.Equal(0, await Interact(alice, captain, LifecycleCaptainOption));
        Assert.Equal(0, await Interact(alice, captain, LifecycleCaptainOption));
        await PlotEnd(alice, LifecycleSuccess); // 12秒的收尾尚未完成，片尾不能提前兌換成功。
        await AirshipLifecycleNotSettled(alice, outro: false);
        await Story(alice, LifecycleOpening);
        await PlotEnd(alice, LifecycleOpening);
        await alice.SendGameAsync(DungeonService.PlotSceneEvent, new PlotActingTriggerReq { PlotId = LifecycleSuccess });
        await AirshipLifecycleNotSettled(alice, outro: true);
        await PlotEnd(alice, LifecycleSuccess);
        await AirshipStorySuccess(alice);
        await PlotEnd(alice, LifecycleSuccess);
        await alice.HeartbeatAsync();
        Assert.Single(AirshipClientCalls(alice), call => call.CliFuncname == "ClearAllShouts");
        Assert.Single(alice.Inbox.Snapshot(), packet => packet.IsGame(DungeonService.ResultNotify));
        Assert.Equal(1, AirshipQuestSourceCount((long)alice.Self!.Uuid, LifecycleClearTask));
    }

    [ClientDataFact]
    public async Task AirshipLeavingDuringOpeningCancelsItsLaterCameraAndCannonSchedule()
    {
        await using var alice = await InWorld("Alice");
        AirshipStoryTestPace();
        long outside = Session(alice).SceneEntry;
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10103210");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = LifecycleAirship }));
        long instance = await Load(alice, LifecycleAirship);
        await Stage(alice, 1, quest: 1);
        await alice.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        await AirshipInterruptedLoadOutside(alice, outside);
        await Task.Delay(4200); // 超過縮短後的39.5秒時點。
        await alice.HeartbeatAsync();
        Assert.Null(Service<InstanceService>().Find(instance));
        Assert.DoesNotContain(AirshipClientCalls(alice), call => call.CliFuncname == "ActiveCameraTimeline"
            && call.Params[0].StrParams.Contains("mainstoryfeikongtingcamera2.prefab"));
        Assert.DoesNotContain(alice.Inbox.Snapshot(), packet => packet.IsGame(DungeonService.ResultNotify));
    }

    /// <summary>原服 TPS 用 targetsId 單一目標；空瞄成功但不製造命中，多筆／矛盾欄位拒絕。</summary>
    [ClientDataFact]
    public async Task AirshipCannonTpsRejectsAmbiguousTargetsAndAcceptsAnEmptyShotWithoutDamage()
    {
        await using var alice = await InWorld("Alice");
        AirshipStoryTestPace();
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10103210");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = LifecycleAirship }));
        long instance = await Load(alice, LifecycleAirship);
        await Stage(alice, 2, quest: 2);
        var cannon = (await ActorsIn(alice, 3, actor => actor.NpcId == 5300022))[0];
        var ships = await WaveIn(instance, 3);
        await Beside(alice, cannon.X, cannon.Y, cannon.Z);
        Assert.Equal(0, await Interact(alice, cannon, 5300006));
        foreach (var request in new[]
        {
            new CastSkillReq { SkillId = 5353001, OpUnitId = cannon.Uid,
                TargetsId = { ships[0].Uid, ships[1].Uid }, TargetPos = ships[0].Pos.Clone() },
            new CastSkillReq { SkillId = 5353001, OpUnitId = cannon.Uid, TargetId = ships[0].Uid,
                TargetsId = { ships[1].Uid }, TargetPos = ships[0].Pos.Clone() },
            new CastSkillReq { SkillId = 5353001, OpUnitId = cannon.Uid,
                TargetsId = { Session(alice).Role!.Id }, TargetPos = ships[0].Pos.Clone() },
            new CastSkillReq { SkillId = 5353001, OpUnitId = cannon.Uid,
                TargetPos = new Vec3 { X = ships[0].X, Y = 1e30f, Z = ships[0].Z } },
        })
        {
            await alice.SendGameAsync(CombatService.UseSkill, request);
            Assert.NotEqual(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        }
        long[] hp = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(ships.Select(ship => ship.Hp).ToArray()));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        {
            SkillId = 5353001, OpUnitId = cannon.Uid,
            // 空瞄甚至指在敵船位置，也不能把未提供的目標重新選成一次命中。
            TargetPos = ships[0].Pos.Clone(),
        });
        var empty = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, empty.Result);
        var timeline = await Next(alice, CombatService.UseInSkillHigh, UseInSkillInfo.Parser,
            use => use.SkillID == 5353001 && use.SessionId == empty.SessionId);
        Assert.Equal(Session(alice).Role!.Id, timeline.SrcID);
        Assert.Equal(0, timeline.TargetId);
        var projectile = await Next(alice, CombatService.SkillBulletHigh, SkillFixTimeBulletNotify.Parser,
            bullet => bullet.SkillConfigId == 5353001 && bullet.EffectId == 6120105);
        Assert.Equal(Session(alice).Role!.Id, projectile.SourceId);
        Assert.Equal(0, projectile.TargetId);
        Assert.True(projectile.FixedTime > 0); // 空瞄也有展示彈道，不能只播人物施放。
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 5353001, OpUnitId = cannon.Uid, TargetPos = ships[0].Pos.Clone() });
        Assert.NotEqual(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result); // 空瞄也有真實 CD。
        await Task.Delay(projectile.FixedTime + 300); // 等彈道確實飛完，才可證明空瞄沒有延遲誤傷。
        await alice.HeartbeatAsync();
        await OnLoop(() =>
        {
            Assert.Equal(hp, ships.Select(ship => ship.Hp));
            Assert.Equal(1, Session(alice).DungeonAttempt!.Wave);
            return Task.CompletedTask;
        });
        Assert.DoesNotContain(alice.Inbox.Snapshot(), packet => packet.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(packet.Body).SessionId == empty.SessionId);
    }

    /// <summary>合法連發不能取消已離砲、仍在同場景／角色世代內飛行的前發。</summary>
    [ClientDataFact]
    public async Task AirshipCannonNextLegalShotDoesNotCancelItsPreviousFlyingProjectile()
    {
        await using var alice = await InWorld("Alice");
        AirshipStoryTestPace();
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10103210");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = LifecycleAirship }));
        long instance = await Load(alice, LifecycleAirship);
        await Stage(alice, 2, quest: 2);
        var cannon = (await ActorsIn(alice, 3, actor => actor.NpcId == 5300022))[0];
        var ships = await WaveIn(instance, 3);
        var visible = (await ClientAirships(alice, expected: 3)).Take(2).ToArray();
        var targets = visible.Select(view => ships.Single(ship => ship.Uid == (long)view.Uuid)).ToArray();
        await OnLoop(() =>
        {
            foreach (var ship in targets)
            {
                ship.Target = null; ship.PetTarget = null; ship.Returning = false; ship.Moving = false;
                ship.Hp = 1; // 縮短LAB驗證，傷害／死亡仍走正式管線。
            }
            return Task.CompletedTask;
        });
        await Beside(alice, cannon.X, cannon.Y, cannon.Z);
        Assert.Equal(0, await Interact(alice, cannon, 5300006));
        var first = await AirshipTpsShotWithProjectile(alice, cannon, visible[0]);
        Assert.True(first.Bullet.FixedTime > 650);
        await Task.Delay(600); // 已過500ms動作／CD，但前發彈道仍未抵達。
        var second = await AirshipTpsShotWithProjectile(alice, cannon, visible[1]);
        await OnLoop(() =>
        {
            Assert.All(targets, ship => Assert.Equal(1, ship.Hp)); // 證明第二發已接受時前發還在飛。
            return Task.CompletedTask;
        });
        await AirshipTpsLethalHit(alice, first.Accepted.SessionId, targets[0].Uid);
        await AirshipTpsLethalHit(alice, second.Accepted.SessionId, targets[1].Uid);
        await OnLoop(() =>
        {
            Assert.All(targets, ship => Assert.True(ship.Dead));
            Assert.Equal(1, Session(alice).DungeonAttempt!.Wave); // 第三船仍在，不能虛構整波完成。
            return Task.CompletedTask;
        });
    }

    private static async Task<(CastSkillRsp Accepted, SkillFixTimeBulletNotify Bullet)> AirshipTpsShotWithProjectile(
        Ro3Emu.Bot.BotClient bot, DungeonService.SceneActorUnit cannon, EntityBase ship)
    {
        await bot.SendGameAsync(CombatService.UseSkill, CannonShot(cannon, ship));
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var bullet = await Next(bot, CombatService.SkillBulletHigh, SkillFixTimeBulletNotify.Parser,
            message => message.SkillConfigId == 5353001 && message.EffectId == 6120105);
        Assert.Equal(0, bullet.TargetId);
        Assert.True(bullet.FixedTime > 0);
        return (accepted, bullet);
    }

    private static Task<SkillHitInfo> AirshipTpsLethalHit(Ro3Emu.Bot.BotClient bot, long cast, long target) =>
        Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            hit => hit.SkillId == 5353001 && hit.SessionId == cast
                && hit.Dsts.Any(dst => dst.DstID == target && dst.IsDead));

    private static DungeonEventNotify[] AirshipClientCalls(Ro3Emu.Bot.BotClient bot) =>
        bot.Inbox.Snapshot().Where(packet => packet.IsGame(DungeonService.Event))
            .Select(packet => DungeonEventNotify.Parser.ParseFrom(packet.Body))
            .Where(call => call.DungeonEntry == LifecycleAirship && (int)call.EventType == 6).ToArray();
}
