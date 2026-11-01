using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    [ClientDataFact]
    public async Task AirshipShipsApproachInTheCapturedDirectionsAndStopAboveTheGround()
    {
        await using var alice = await InWorld("Alice");
        Service<IOptions<ServerOptions>>().Value.ScriptedScenePacePercent = 25;
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10103210");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = LifecycleAirship }));
        long instance = await Load(alice, LifecycleAirship);
        await Stage(alice, 2, quest: 1);
        var ships = (await WaveIn(instance, 3)).OrderBy(s => s.Uid).ToArray();
        var born = (await ClientAirships(alice, expected: 3)).OrderBy(s => s.Uuid).ToArray();
        var expected = new[]
        {
            (X: 317.00397f, Z: 453.98346f, Direction: 274.03094f),
            (X: 285.02432f, Z: 499.9749f, Direction: 237.63416f),
            (X: 289.68f, Z: 400.7707f, Direction: 313.7329f),
        };
        for (int i = 0; i < ships.Length; i++)
        {
            var ship = ships[i];
            Assert.Equal(0, born[i].Face);
            Assert.Equal(0, born[i].MovementInfo.Face);
            Assert.Equal(207.65002f, born[i].MovementInfo.Pos.Y, 3);
            var approach = await Next(alice, GameAoiSink.MovementChange, MovementChangeNtf.Parser,
                n => n.Uid == ship.Uid && n.MovementInfo.MoveType == MovementType.MoveTypeWalk);
            Assert.True(approach.MovementInfo.FaceMove);
            Assert.Equal(0, approach.MovementInfo.Face);
            Assert.Equal(expected[i].Direction, approach.MovementInfo.MovementWalk.Dir, 3);
            Assert.InRange(approach.MovementInfo.MovementWalk.Speed, 33.9f, 34.1f); // LAB 25% 節奏。
            Assert.Equal(207.65002f, approach.MovementInfo.MovementWalk.EndHeight, 3);
        }
        // 中途取得的 AOI 也必須有剩餘路段及朝向；不能只修改已在線者收到的封包。
        var movingSnapshot = await OnLoopValue(() => ships[1].Entity().MovementInfo);
        Assert.Equal(MovementType.MoveTypeWalk, movingSnapshot.MoveType);
        Assert.True(movingSnapshot.FaceMove);
        Assert.Equal(expected[1].Direction, movingSnapshot.MovementWalk.Dir, 3);
        Assert.InRange(movingSnapshot.MoveDuration, 1, 1663);
        for (int i = 0; i < ships.Length; i++)
        {
            var ship = ships[i];
            var end = expected[i];
            await Next(alice, GameAoiSink.MovementChange, MovementChangeNtf.Parser,
                n => n.Uid == ship.Uid && n.MovementInfo.MoveType == MovementType.MoveTypeNone
                    && Math.Abs(n.MovementInfo.Pos.X - end.X) < .001f);
            await OnLoop(() =>
            {
                Assert.Equal(end.X, ship.X, 3);
                Assert.Equal(end.Z, ship.Z, 3);
                Assert.Equal(207.65002f, ship.Y, 3);
                Assert.Equal(ship.Template.MaxHp, ship.Hp); // 不把 HP 改成 1 掩蓋原始模型。
                Assert.True(ship.StaysOutOfBattle);
                Assert.True(ship.KeepsDamage);
                Assert.Null(ship.Target);
                Assert.False(ship.Moving);
                Assert.Equal(MovementType.MoveTypeNone, ship.Entity().MovementInfo.MoveType);
                return Task.CompletedTask;
            });
        }
    }

    [ClientDataFact]
    public async Task AirshipShipsCannotBePulledIntoGroundCombatByPlayersPetsOrCompanions()
    {
        await using var alice = await InWorld("Alice");
        AirshipStoryTestPace();
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10103210");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = LifecycleAirship }));
        long instance = await Load(alice, LifecycleAirship);
        await Stage(alice, 2, quest: 2); // 砲台開放前，三艦已停在空中。
        var ship = (await WaveIn(instance, 3))[0];
        await OnLoop(() =>
        {
            var s = Session(alice);
            var monsters = Service<MonsterService>();
            var world = Service<World>();
            var combat = Service<CombatService>();
            var pet = TowerPetUnit.Create(Service<GameData>(),
                new DBPetInfo { Inited = true, ListPet = { [13001] = new PetData { PetId = 13001, Level = 12, Star = 1 } } },
                s, world, monsters, combat, TowerPetUnit.UidBase + (1L << 40), 13001, instance,
                ship.X + 5, ship.Y, ship.Z);
            Assert.NotNull(pet);
            var companion = new CompanionUnit(new CompanionUnit.Kit(5300007, ship.Template, [], 300, 100, 100, 20, 100), world, combat)
            {
                Uid = CompanionUnit.UidBase + 123, SceneEntry = ship.SceneEntry, InstanceId = instance,
                X = ship.X + 5, Y = ship.Y, Z = ship.Z, Point = 0, HomeX = ship.X, HomeZ = ship.Z,
                Credit = () => s, Fell = _ => Task.CompletedTask,
            };
            var before = ship.Pos;
            monsters.Engage(ship, s);
            monsters.Engage(ship, pet);
            monsters.Engage(ship, companion);
            monsters.Tick(ship, world.Map(ship.WorldScene));
            Assert.Null(ship.Target);
            Assert.Null(ship.PetTarget);
            Assert.False(ship.Moving);
            Assert.False(ship.Returning);
            Assert.Equal(before, ship.Pos);
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task LeavingDuringAirshipApproachCancelsTheOldShipsMovement()
    {
        await using var alice = await InWorld("Alice");
        AirshipStoryTestPace();
        long outside = Session(alice).SceneEntry;
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10103210");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = LifecycleAirship }));
        long instance = await Load(alice, LifecycleAirship);
        await Stage(alice, 2, quest: 1);
        var ships = await WaveIn(instance, 3);
        await Next(alice, GameAoiSink.MovementChange, MovementChangeNtf.Parser,
            n => ships.Any(s => s.Uid == n.Uid) && n.MovementInfo.MoveType == MovementType.MoveTypeWalk);
        await alice.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        await AirshipInterruptedLoadOutside(alice, outside);
        var stopped = await OnLoopValue(() => ships.Select(s => s.Pos).ToArray());
        int mark = alice.Inbox.Snapshot().Count;
        await Task.Delay(850); // 超過本次10%節奏的最長路段；舊閉房排程不得再移動。
        await alice.HeartbeatAsync();
        Assert.Equal(stopped, await OnLoopValue(() => ships.Select(s => s.Pos).ToArray()));
        Assert.DoesNotContain(alice.Inbox.Snapshot().Skip(mark), p => p.IsGame(GameAoiSink.MovementChange)
            && ships.Any(s => s.Uid == MovementChangeNtf.Parser.ParseFrom(p.Body).Uid));
        Assert.Null(Service<InstanceService>().Find(instance));
    }
}
