using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    private const long AirshipCombatScene = 1000501;

    private static CastSkillReq CannonShot(DungeonService.SceneActorUnit cannon, EntityBase ship) => new()
    {
        SkillId = 5353001, OpUnitId = cannon.Uid, TargetsId = { (long)ship.Uuid },
        TargetPos = ship.MovementInfo.Pos.Clone(),
    };

    /// <summary>登砲只改狀態；同一座砲可持續射擊，冷卻與退出均不會偽造敵艦擊殺。</summary>
    [ClientDataFact]
    public async Task AirshipCannonMountDoesNotFireAndRepeatedShotsNeedRealHits()
    {
        await using var alice = await InWorld("Alice");
        AirshipStoryTestPace();
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10103210"); // LAB 準備入場任務；不代表已走完前置主線。
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = AirshipCombatScene }));
        long instance = await Load(alice, AirshipCombatScene);
        await Stage(alice, 1, quest: 1);
        await Stage(alice, 2, quest: 2);
        var cannon = (await ActorsIn(alice, 3, actor => actor.NpcId == 5300022))[0];
        var ships = (await WaveIn(instance, 3)).OrderBy(ship => ship.Template.NpcId).ToArray();
        var clientShips = await ClientAirships(alice, expected: 3);
        Assert.Equal(ships.Select(ship => (ulong)ship.Uid), clientShips.Select(ship => ship.Uuid));
        var hpBefore = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(ships.Select(ship => ship.Hp).ToArray()));

        await Beside(alice, cannon.X, cannon.Y, cannon.Z);
        Assert.Equal(0, await Interact(alice, cannon, 5300006));
        await Next(alice, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == Session(alice).Role!.Id && n.DataInfo.EType == InteractInfo.Types.EState.UpperCannon);
        await Next(alice, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == cannon.Uid && n.DataInfo.EType == InteractInfo.Types.EState.UpperCannon
                && n.DataInfo.InteractConfigId == 0 && n.DataInfo.UpperCannonInfo.CannonGuId == cannon.Uid);
        Assert.Equal(ErrorCodes.NpcParameter, await Interact(alice, cannon, 5300006));
        await alice.SendGameAsync(WorldInteractionService.Action, new UseInteractActionReq { ButtonId = 27 });
        Assert.Equal(ErrorCodes.NpcParameter,
            (await Next(alice, WorldInteractionService.Action, UseInteractActionRes.Parser)).Result);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        {
            SkillId = 5353001, OpUnitId = cannon.Uid, TargetId = (long)alice.Self!.Uuid,
        });
        Assert.NotEqual(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await OnLoop(() =>
        {
            var attempt = Assert.IsType<DungeonService.Attempt>(Session(alice).DungeonAttempt);
            Assert.Equal(hpBefore, ships.Select(ship => ship.Hp));
            Assert.Equal(1, attempt.Wave);
            Assert.Single(attempt.CannonUsers);
            return Task.CompletedTask;
        });

        Task PrepareShip(MonsterUnit ship) => OnLoop(() =>
        {
            Assert.False(ship.Template.Aggressive);
            ship.Target = null;
            ship.PetTarget = null;
            ship.Returning = false;
            ship.Moving = false;
            ship.Hp = 1; // 只縮短 LAB 的擊殺時間；命中與死亡仍走真實戰鬥管線。
            return Task.CompletedTask;
        });
        await PrepareShip(ships[0]);
        await alice.SendGameAsync(CombatService.UseSkill, CannonShot(cannon, clientShips[0]));
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var cannonTimeline = await Next(alice, CombatService.UseInSkillHigh, UseInSkillInfo.Parser,
            use => use.SkillID == 5353001 && use.TargetId == ships[0].Uid);
        Assert.Equal(Session(alice).Role!.Id, cannonTimeline.SrcID);
        var cannonProjectile = await Next(alice, CombatService.SkillBulletHigh, SkillFixTimeBulletNotify.Parser,
            bullet => bullet.SkillConfigId == 5353001 && bullet.EffectId == 6120105);
        Assert.Equal(Session(alice).Role!.Id, cannonProjectile.SourceId);
        Assert.Equal(0, cannonProjectile.TargetId); // wire展示彈道不鎖單位，實際傷害仍繫結授權目標。
        Assert.True(cannonProjectile.FixedTime > 0);
        await alice.SendGameAsync(CombatService.UseSkill, CannonShot(cannon, clientShips[1]));
        Assert.NotEqual(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.Contains((await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            hit => hit.SkillId == 5353001)).Dsts, dst => dst.DstID == ships[0].Uid && dst.IsDead);
        await OnLoop(() =>
        {
            var attempt = Assert.IsType<DungeonService.Attempt>(Session(alice).DungeonAttempt);
            Assert.Equal(hpBefore[1], ships[1].Hp);
            Assert.Equal(1, attempt.Wave);
            Assert.Single(attempt.CannonUsers);
            return Task.CompletedTask;
        });

        await Task.Delay(600); // G 5353001 的動作與技能 CD 均為 500 ms。
        await PrepareShip(ships[1]);
        await alice.SendGameAsync(CombatService.UseSkill, CannonShot(cannon, clientShips[1]));
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.Contains((await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            hit => hit.SkillId == 5353001)).Dsts, dst => dst.DstID == ships[1].Uid && dst.IsDead);
        await OnLoop(() =>
        {
            Assert.Equal(1, Session(alice).DungeonAttempt!.Wave); // 兩艘死亡不能當作三艘完成。
            return Task.CompletedTask;
        });
        await Task.Delay(600);
        await PrepareShip(ships[2]);
        await alice.SendGameAsync(CombatService.UseSkill, CannonShot(cannon, clientShips[2]));
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.Contains((await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            hit => hit.SkillId == 5353001)).Dsts, dst => dst.DstID == ships[2].Uid && dst.IsDead);
        await Next(alice, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == cannon.Uid && n.DataInfo.EType == InteractInfo.Types.EState.Idle);
        await Next(alice, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == Session(alice).Role!.Id && n.DataInfo.EType == InteractInfo.Types.EState.Idle);
        await Stage(alice, 3, quest: 3);
        await OnLoop(() =>
        {
            var attempt = Assert.IsType<DungeonService.Attempt>(Session(alice).DungeonAttempt);
            Assert.Empty(attempt.CannonUsers);
            Assert.Empty(attempt.CannonOccupants);
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, CannonShot(cannon, clientShips[1]));
        Assert.NotEqual(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    /// <summary>原配置僅允許單人；斷線清除炮位，新副本拒絕舊炮位 ID。</summary>
    [ClientDataFact]
    public async Task AirshipCannonOccupancyIsReleasedWhenItsUserDisconnects()
    {
        AirshipStoryTestPace();
        long role, previousInstance;
        DungeonService.Attempt previous;
        DungeonService.SceneActorUnit oldCannon;
        await using (var alice = await InWorld("Alice"))
        {
            role = (long)alice.Self!.Uuid;
            await Gm(alice, "!lvl 99");
            await Gm(alice, "!quest accept 10103210"); // LAB 準備入場任務；重登沿用 Running 狀態。
            Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = AirshipCombatScene }));
            previousInstance = await Load(alice, AirshipCombatScene);
            await Stage(alice, 1, quest: 1);
            await Stage(alice, 2, quest: 2);
            oldCannon = (await ActorsIn(alice, 3, actor => actor.NpcId == 5300022))[0];
            previous = Assert.IsType<DungeonService.Attempt>(Session(alice).DungeonAttempt);
            await Beside(alice, oldCannon.X, oldCannon.Y, oldCannon.Z);
            Assert.Equal(0, await Interact(alice, oldCannon, 5300006));
            Assert.Equal(ErrorCodes.NpcParameter, await Interact(alice, oldCannon, 5300006));
            Assert.Single(previous.CannonOccupants);
        }
        for (int i = 0; i < 150 && Service<SessionRegistry>().All().Any(s => s.Role?.Id == role); i++)
            await Task.Delay(20);
        await OnLoop(() =>
        {
            Assert.Empty(previous.CannonUsers);
            Assert.Empty(previous.CannonOccupants);
            return Task.CompletedTask;
        });
        Assert.Null(Service<InstanceService>().Find(previousInstance));

        await using var relog = await InWorld("Alice", create: false);
        Assert.Equal(0, Session(relog).InstanceId);
        Assert.Equal(0, await Enter(relog, new EnterDungeonReq { SceneEntry = AirshipCombatScene }));
        long currentInstance = await Load(relog, AirshipCombatScene);
        Assert.NotEqual(previousInstance, currentInstance);
        await Stage(relog, 1, quest: 1);
        await Stage(relog, 2, quest: 2);
        var newCannon = (await ActorsIn(relog, 3, actor => actor.NpcId == 5300022))[0];
        await Beside(relog, newCannon.X, newCannon.Y, newCannon.Z);
        Assert.NotEqual(0, await Interact(relog, oldCannon, 5300006));
        Assert.Equal(0, await Interact(relog, newCannon, 5300006));
        await OnLoop(() =>
        {
            var current = Assert.IsType<DungeonService.Attempt>(Session(relog).DungeonAttempt);
            Assert.Single(current.CannonUsers);
            Assert.Single(current.CannonOccupants);
            Assert.DoesNotContain(oldCannon.Uid, current.CannonOccupants.Keys);
            return Task.CompletedTask;
        });
    }

    /// <summary>四戰士與五弓箭手分批；原服巡視不必完成五次泡泡，QTE 仍不能跨階段。</summary>
    [ClientDataFact]
    public async Task AirshipDeckWavesBossAndInvestigationRejectCrossStageEvents()
    {
        await using var alice = await InWorld("Alice");
        AirshipStoryTestPace();
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10103210"); // LAB 準備入場任務；戰鬥仍由正式協定推進。
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = AirshipCombatScene }));
        long instance = await Load(alice, AirshipCombatScene);
        await Stage(alice, 1, quest: 1);
        await Stage(alice, 2, quest: 2);
        foreach (var ship in await WaveIn(instance, 3)) await Kill(alice, ship);

        foreach (var (stage, count) in new[] { (3, 4), (4, 5) })
        {
            await Stage(alice, stage, quest: 3);
            var wave = await WaveIn(instance, count);
            Assert.Equal(count, wave.Count);
            await PlotEnd(alice, 130162); // 船舵結局只屬最後階段。
            await Task.Delay(100);
            Assert.Equal(stage - 1, Assert.IsType<DungeonService.Attempt>(Session(alice).DungeonAttempt).Wave);
            foreach (var boarder in wave.Take(count - 1))
            {
                await Kill(alice, boarder);
                Assert.Equal(stage - 1, Assert.IsType<DungeonService.Attempt>(Session(alice).DungeonAttempt).Wave);
            }
            await Kill(alice, wave[count - 1]);
        }
        await Stage(alice, 5, quest: 4);
        await PlotEnd(alice, 130163);
        await Task.Delay(100);
        Assert.Equal(4, Assert.IsType<DungeonService.Attempt>(Session(alice).DungeonAttempt).Wave);
        await Kill(alice, await BossIn(instance, 5300018));

        await Stage(alice, 6, quest: 5);
        await PlotEnd(alice, 130162);
        await Task.Delay(100);
        Assert.Equal(5, Assert.IsType<DungeonService.Attempt>(Session(alice).DungeonAttempt).Wave);
        // 不發出任何泡泡互動，計時仍必須到達船長階段。
        await Stage(alice, 7, quest: 6);
        Assert.False(Assert.IsType<DungeonService.Attempt>(Session(alice).DungeonAttempt).Settled); // 還需船長對話與 QTE 結局。
    }
}
