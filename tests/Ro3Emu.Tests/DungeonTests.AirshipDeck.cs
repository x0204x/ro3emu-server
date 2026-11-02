using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    [ClientDataFact]
    public async Task AirshipDeckBlastsLeaveOnePersistentBurnWithoutChangingTheShipObjective()
    {
        await using var alice = await InWorld("Alice");
        AirshipStoryTestPace();
        long outside = Session(alice).SceneEntry;
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10103210");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = LifecycleAirship }));
        long instance = await Load(alice, LifecycleAirship);
        await Stage(alice, 2, quest: 2);
        await alice.HeartbeatAsync();
        var blasts = AirshipDeckCasts(alice);
        Assert.Equal(2, blasts.Length);
        Assert.Equal(2, blasts.Select(cast => cast.SrcID).Distinct().Count());
        Assert.All(blasts, cast =>
        {
            Assert.Equal(new Vec3 { X = 236, Y = 228.65002f, Z = 467.79f }, cast.TargetPos);
            Assert.Equal(200, cast.SkillMs);
            Assert.True(cast.PlayAct);
            AssertAirshipDeckActorBornBeforeCastAndRemoved(alice, cast.SrcID);
        });
        var burn = await OnLoopValue(() => Assert.Single(AirshipDeckActors(instance)));
        Assert.Equal(5300057, burn.NpcId);
        Assert.Equal(blasts[0].TargetPos, burn.Pos);
        Assert.Equal((ulong)5300057, burn.Entity().EntityTemplateID); // 後進 AOI 仍有燃燒模型。
        Assert.Equal(830, Assert.Single(burn.Entity().Property, property => property.Idx == 101).Data);
        await OnLoop(() =>
        {
            var attempt = Session(alice).DungeonAttempt!;
            Assert.Equal(1, attempt.Wave);
            Assert.Empty(attempt.Killed);
            Assert.Equal(3, attempt.Room.Monsters.Count);
            Assert.DoesNotContain(attempt.SceneActors, actor => actor.NpcId is 5300037 or 5300057);
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        await AirshipInterruptedLoadOutside(alice, outside);
        await OnLoop(() =>
        {
            Assert.True(burn.Removed);
            Assert.Empty(AirshipDeckActors(instance));
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task AirshipCaptainRepeatsDoNotDuplicateTheFiveDeckBlastsAndExitClearsTheBurns()
    {
        await using var alice = await InWorld("Alice");
        long outside = Session(alice).SceneEntry;
        await AirshipLifecycleAtCaptain(alice);
        long instance = Session(alice).DungeonAttempt!.Room.Id;
        var captain = Assert.Single(await ActorsIn(alice, 1, actor => actor.NpcId == LifecycleCaptain));
        await alice.HeartbeatAsync();
        int beforeCaptain = AirshipDeckCasts(alice).Length;
        Assert.Equal(0, await Interact(alice, captain, LifecycleCaptainOption));
        Assert.Equal(0, await Interact(alice, captain, LifecycleCaptainOption));
        await Story(alice, LifecycleOpening); // 收尾 12 秒後開 QTE，五處砲擊應已完成。
        await alice.HeartbeatAsync();
        var casts = AirshipDeckCasts(alice);
        Assert.Equal(beforeCaptain + 5, casts.Length); // 只核對此段新增；LAB 擊殺定位可能離開開場爆炸的 AOI。
        var captainCasts = casts.Skip(beforeCaptain).ToArray();
        Assert.Equal(new[]
        {
            (228.66f, 476.721f), (239.0966f, 454.0193f), (239.1845f, 466.0333f),
            (227.12f, 448.181f), (229.9801f, 459.6027f),
        }.OrderBy(p => p.Item1), captainCasts.Select(c => (c.TargetPos.X, c.TargetPos.Z)).OrderBy(p => p.X));
        Assert.All(captainCasts, cast => AssertAirshipDeckActorBornBeforeCastAndRemoved(alice, cast.SrcID));
        var burns = await OnLoopValue(() => AirshipDeckActors(instance));
        Assert.Equal(6, burns.Length);
        Assert.All(burns, burn => Assert.Equal(5300057, burn.NpcId));
        await alice.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        await AirshipInterruptedLoadOutside(alice, outside);
        await OnLoop(() =>
        {
            Assert.All(burns, burn => Assert.True(burn.Removed));
            Assert.Empty(AirshipDeckActors(instance));
            return Task.CompletedTask;
        });
    }

    private DungeonService.SceneActorUnit[] AirshipDeckActors(long instance) => [.. Service<World>().AllUnits()
        .OfType<DungeonService.SceneActorUnit>()
        .Where(actor => actor.InstanceId == instance && actor.NpcId is 5300037 or 5300057)];

    private static UseInSkillInfo[] AirshipDeckCasts(BotClient bot) => [.. bot.Inbox.Snapshot()
        .Where(packet => packet.IsGame(CombatService.UseInSkillHigh))
        .Select(packet => UseInSkillInfo.Parser.ParseFrom(packet.Body)).Where(cast => cast.SkillID == 5353101)];

    private static void AssertAirshipDeckActorBornBeforeCastAndRemoved(BotClient bot, long uid)
    {
        var packets = bot.Inbox.Snapshot().ToArray();
        int born = Array.FindIndex(packets, packet => packet.IsGame(GameAoiSink.SyncAoiView)
            && AoIViewChangeData.Parser.ParseFrom(packet.Body).Bases.Any(actor => actor.Uuid == (ulong)uid));
        int cast = Array.FindIndex(packets, packet => packet.IsGame(CombatService.UseInSkillHigh)
            && UseInSkillInfo.Parser.ParseFrom(packet.Body).SrcID == uid);
        int gone = Array.FindIndex(packets, packet => packet.IsGame(GameAoiSink.SyncAoiView)
            && AoIViewChangeData.Parser.ParseFrom(packet.Body).LeaveAoi.Contains((ulong)uid));
        Assert.True(born >= 0 && born < cast && cast < gone, $"甲板演出順序不符：born={born}, cast={cast}, leave={gone}");
    }
}
