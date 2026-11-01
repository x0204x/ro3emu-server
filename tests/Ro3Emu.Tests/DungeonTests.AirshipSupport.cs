using Phonest.Proto;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    [ClientDataFact]
    public async Task AirshipSupportMovesCastsRetargetsAndLeavesBeforeTheConversationActors()
    {
        await using var alice = await InWorld("Alice");
        Assert.True(await alice.CompleteGateAuthAsync());
        long instance = await AirshipAtBoarders(alice);
        var first = await WaveIn(instance, 4);
        var units = await AirshipSupportUnits(alice);
        Assert.Equal(new[] { 5300086, 5300087, 5300088 }, units.Select(unit => unit.NpcId).Order());
        var births = alice.Inbox.Snapshot().Where(packet => packet.IsGame(GameAoiSink.SyncAoiView))
            .SelectMany(packet => AoIViewChangeData.Parser.ParseFrom(packet.Body).Bases)
            .Where(entity => units.Any(unit => (ulong)unit.Uid == entity.Uuid)).ToArray();
        Assert.Equal(3, births.Length);
        Assert.All(births, entity =>
        {
            Assert.Equal((long)entity.Uuid, entity.Owner);
            Assert.Equal(new Vec3 { X = 223.5916f, Y = 228.65002f, Z = 432.4861f }, entity.MovementInfo.Pos);
        });
        Assert.All(units, unit =>
        {
            Assert.True(unit.Spec.StoryPresentationOnly);
            Assert.Equal(10001, unit.Template.MaxHp);
            Assert.Equal(25, unit.Entity().AnimationInfo[1]);
        });
        foreach (var unit in units)
        {
            await Next(alice, GameAoiSink.MovementChange, MovementChangeNtf.Parser,
                move => move.Uid == unit.Uid && move.MovementInfo.MoveType == MovementType.MoveTypeWalk);
            var cast = await Next(alice, CombatService.UseInSkillHigh, UseInSkillInfo.Parser,
                message => message.SrcID == unit.Uid && first.Any(enemy => enemy.Uid == message.TargetId));
            Assert.True(cast.PlayAct);
            Assert.Equal(cast.SkillMs, cast.SkillInterruptMs);
        }
        await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser,
            move => units.Any(unit => (ulong)unit.Uid == move.EntityID) && move.SkillConfigId == 5353901
                && move.EffectId == 5213506 && move.MoveTimeMs == 250);
        var leap = await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser,
            move => units.Any(unit => (ulong)unit.Uid == move.EntityID) && move.SkillConfigId == 5354001
                && move.EffectId == 4320902 && move.MoveTimeMs == 250);
        Assert.Equal(6f, MonsterService.Distance(leap.StartPos.X, leap.StartPos.Z, leap.EndPos.X, leap.EndPos.Z), 3);
        Assert.All(first, enemy => Assert.Equal(enemy.Template.MaxHp, enemy.Hp)); // 未假造友軍傷害。
        Assert.DoesNotContain(alice.Inbox.Snapshot(), packet => packet.IsGame(CombatService.SkillHit)
            && units.Any(unit => unit.Uid == SkillHitInfo.Parser.ParseFrom(packet.Body).SrcID));
        Assert.DoesNotContain(alice.Inbox.Snapshot(), packet => packet.IsGame(CombatService.UseInSkill)
            && units.Any(unit => unit.Uid == UseInSkillInfo.Parser.ParseFrom(packet.Body).SrcID));

        var session = Session(alice);
        await alice.DropGameAsync(BotDrop.Graceful);
        var deadline = DateTime.UtcNow + Wait;
        while (!await OnLoopValue(() => session.TransportLost))
        {
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(20);
        }
        Assert.Equal(ConnectServerResp.Types.RespType.Reconnected, (await alice.ReconnectGameAsync()).Result);
        Assert.Equal(units.Select(unit => unit.Uid), (await AirshipSupportUnits(alice)).Select(unit => unit.Uid));
        foreach (var enemy in first) await Kill(alice, enemy);
        await Stage(alice, 4, quest: 3);
        var second = await WaveIn(instance, 5);
        await Next(alice, CombatService.UseInSkillHigh, UseInSkillInfo.Parser,
            cast => units.Any(unit => unit.Uid == cast.SrcID) && second.Any(enemy => enemy.Uid == cast.TargetId));
        foreach (var enemy in second) await Kill(alice, enemy);
        await Stage(alice, 5, quest: 4);
        var boss = await BossIn(instance, 5300018);
        await Next(alice, CombatService.UseInSkillHigh, UseInSkillInfo.Parser,
            cast => units.Any(unit => unit.Uid == cast.SrcID) && cast.TargetId == boss.Uid);
        await Kill(alice, boss);
        await Stage(alice, 7, quest: 6);
        Assert.All(units, unit => Assert.True(unit.Removed));
        Assert.DoesNotContain(alice.Inbox.Snapshot(), packet => packet.IsGame(DungeonService.ResultNotify));
    }

    [ClientDataFact]
    public async Task LeavingBeforeAirshipSupportAppearsCancelsAllThreeBirths()
    {
        await using var alice = await InWorld("Alice");
        long instance = await AirshipAtBoarders(alice);
        await alice.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        int mark = alice.Inbox.Snapshot().Count;
        await Task.Delay(1100); // 超過本測試10%節奏的三個出生時間。
        Assert.Null(Service<InstanceService>().Find(instance));
        Assert.DoesNotContain(alice.Inbox.Snapshot().Skip(mark), packet => packet.IsGame(GameAoiSink.SyncAoiView)
            && AoIViewChangeData.Parser.ParseFrom(packet.Body).Bases.Any(entity => entity.EntityTemplateID is 5300086 or 5300087 or 5300088));
        Assert.DoesNotContain(alice.Inbox.Snapshot().Skip(mark), packet => packet.IsGame(CombatService.UseInSkillHigh)
            && UseInSkillInfo.Parser.ParseFrom(packet.Body).SkillID is 5353601 or 5353701 or 5353801);
    }

    private async Task<long> AirshipAtBoarders(BotClient alice)
    {
        AirshipStoryTestPace();
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10103210");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = LifecycleAirship }));
        long instance = await Load(alice, LifecycleAirship);
        await Stage(alice, 2, quest: 2);
        foreach (var ship in await WaveIn(instance, 3)) await Kill(alice, ship);
        await Stage(alice, 3, quest: 3);
        return instance;
    }

    private async Task<CompanionUnit[]> AirshipSupportUnits(BotClient alice)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            var units = await OnLoopValue(() => Service<World>().Map(Session(alice).WorldScene)
                .Units.OfType<CompanionUnit>().OrderBy(unit => unit.Uid).ToArray());
            if (units.Length == 3) return units;
            await Task.Delay(20);
        }
        throw new TimeoutException("airship support did not appear");
    }
}
