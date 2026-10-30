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
        SkillId = 5353001, OpUnitId = cannon.Uid, TargetId = (long)ship.Uuid,
        TargetPos = ship.MovementInfo.Pos.Clone(),
    };

    /// <summary>登砲只改狀態；同一座砲可持續射擊，冷卻與退出均不會偽造敵艦擊殺。</summary>
    [ClientDataFact]
    public async Task AirshipCannonMountDoesNotFireAndRepeatedShotsNeedRealHits()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10103210"); // LAB 準備入場任務；不代表已走完前置主線。
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = AirshipCombatScene }));
        long instance = await Load(alice, AirshipCombatScene);
        await Stage(alice, 1, quest: 1);
        await Gm(alice, "!actors use"); // 開場四段泡泡的既有測試另行逐一驗證。
        await Stage(alice, 2, quest: 2);
        var cannon = (await ActorsIn(alice, 3, actor => actor.NpcId == 5300022))[0];
        var ships = (await WaveIn(instance, 2)).OrderBy(ship => ship.Template.NpcId).ToArray();
        var clientShips = await ClientAirships(alice);
        Assert.Equal(ships.Select(ship => (ulong)ship.Uid), clientShips.Select(ship => ship.Uuid));
        var hpBefore = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(ships.Select(ship => ship.Hp).ToArray()));

        await Beside(alice, cannon.X, cannon.Y, cannon.Z);
        Assert.Equal(0, await Interact(alice, cannon, 5300006));
        await Next(alice, WorldInteractionService.Change, InteractChangeNtf.Parser,
            n => n.Uid == Session(alice).Role!.Id && n.DataInfo.EType == InteractInfo.Types.EState.UpperCannon);
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
            await Gm(alice, "!actors use");
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
        await Gm(relog, "!actors use");
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

    /// <summary>三波登船、首領與五處戰後調查只由各自目前階段的真實事件推進。</summary>
    [ClientDataFact]
    public async Task AirshipDeckWavesBossAndInvestigationRejectCrossStageEvents()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10103210"); // LAB 準備入場任務；戰鬥仍由正式協定推進。
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = AirshipCombatScene }));
        long instance = await Load(alice, AirshipCombatScene);
        await Stage(alice, 1, quest: 1);
        await Gm(alice, "!actors use");
        await Stage(alice, 2, quest: 2);
        foreach (var ship in await WaveIn(instance, 2)) await Kill(alice, ship);

        for (int stage = 3; stage <= 5; stage++)
        {
            await Stage(alice, stage, quest: 3);
            var wave = await WaveIn(instance, 3);
            Assert.Equal(3, wave.Count);
            await PlotEnd(alice, 130162); // 船舵結局只屬最後階段。
            await Task.Delay(100);
            Assert.Equal(stage - 1, Assert.IsType<DungeonService.Attempt>(Session(alice).DungeonAttempt).Wave);
            foreach (var boarder in wave.Take(2))
            {
                await Kill(alice, boarder);
                Assert.Equal(stage - 1, Assert.IsType<DungeonService.Attempt>(Session(alice).DungeonAttempt).Wave);
            }
            await Kill(alice, wave[2]);
        }
        await Stage(alice, 6, quest: 4);
        await PlotEnd(alice, 130163);
        await Task.Delay(100);
        Assert.Equal(5, Assert.IsType<DungeonService.Attempt>(Session(alice).DungeonAttempt).Wave);
        await Kill(alice, await BossIn(instance, 5300018));

        await Stage(alice, 7, quest: 5);
        var actors = await ActorsIn(alice, 5);
        var catalog = Service<DungeonCatalog>();
        await PlotEnd(alice, 130162);
        await Task.Delay(100);
        Assert.Equal(6, Assert.IsType<DungeonService.Attempt>(Session(alice).DungeonAttempt).Wave);
        var interactions = new[] { 5300048, 5300049, 5300050, 5300051, 5300052 };
        for (int i = 0; i < interactions.Length; i++)
        {
            int interaction = interactions[i];
            var actor = actors.Single(candidate => catalog.Interactions(candidate.NpcId).Contains(interaction));
            await Beside(alice, actor.X, actor.Y, actor.Z);
            Assert.Equal(0, await Interact(alice, actor, interaction));
            if (i == 0)
            {
                Assert.Equal(0, await Interact(alice, actor, interaction)); // 同一泡泡重送不能湊成第二處。
                Assert.Single(Assert.IsType<DungeonService.Attempt>(Session(alice).DungeonAttempt).StepSeen);
            }
            if (i < interactions.Length - 1)
            {
                var attempt = Assert.IsType<DungeonService.Attempt>(Session(alice).DungeonAttempt);
                Assert.Equal(6, attempt.Wave);
                Assert.Equal(i + 1, attempt.StepSeen.Count);
            }
        }
        await Stage(alice, 8, quest: 6);
        Assert.False(Assert.IsType<DungeonService.Attempt>(Session(alice).DungeonAttempt).Settled); // 還需船長對話與 QTE 結局。
    }
}
