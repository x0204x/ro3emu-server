using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    [ClientDataFact]
    public async Task AirshipBossRollSpawnsMovingBarrelsAndClearsThemWithoutQuestKills()
    {
        await using var bot = await InWorld("BarrelMotion");
        var boss = await AirshipBarrelBoss(bot);
        var a = Session(bot).DungeonAttempt!;
        int kills = a.Killed.Count;
        await OnLoop(() =>
        {
            Service<MonsterService>().GmCastBossSkill(boss, Session(bot), 5352801);
            return Task.CompletedTask;
        });
        var first = await Next(bot, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            view => view.Bases.Any(unit => unit.EntityTemplateID == 5300020));
        var born = first.Bases.First(unit => unit.EntityTemplateID == 5300020);
        var move = await Next(bot, GameAoiSink.MovementChange, MovementChangeNtf.Parser,
            entry => entry.Uid == (long)born.Uuid && entry.MovementInfo.MoveType == MovementType.MoveTypeWalk);
        Assert.Equal(0, move.MovementInfo.MovementWalk.Dir);
        Assert.True(move.MovementInfo.MovementWalk.Speed > 0);
        Assert.True(move.MovementInfo.Pos.Z > born.MovementInfo.Pos.Z);
        await OnLoop(() =>
        {
            var actor = Assert.Single(Service<World>().AllUnits().OfType<DungeonService.SceneActorUnit>(), u => u.Uid == (long)born.Uuid);
            Assert.Equal(MovementType.MoveTypeWalk, actor.Entity().MovementInfo.MoveType); // 晚進 AOI 也收到尚在滾動的快照。
            Assert.Equal(new ulong[] { 0, 1024, 0 }, actor.Entity().ObjState.Data.Values);
            Assert.Equal(kills, a.Killed.Count);
            return Task.CompletedTask;
        });
        await Task.Delay(2100); // LAB 10%：原約16秒的一批出生、移動、移除完整跑完。
        await bot.HeartbeatAsync();
        var barrels = bot.Inbox.Snapshot().Where(p => p.IsGame(GameAoiSink.SyncAoiView))
            .SelectMany(p => AoIViewChangeData.Parser.ParseFrom(p.Body).Bases).Where(u => u.EntityTemplateID == 5300020)
            .Select(u => u.Uuid).Distinct().ToArray();
        Assert.Equal(11, barrels.Length);
        await OnLoop(() =>
        {
            Assert.DoesNotContain(Service<World>().AllUnits(), u => u.InstanceId == a.Room.Id
                && u is DungeonService.SceneActorUnit { NpcId: 5300020 });
            Assert.Equal(kills, a.Killed.Count);
            Assert.Equal(4, a.Wave);
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task AirshipBossDeathCancelsUnstartedBarrels()
    {
        await using var bot = await InWorld("BarrelCancel");
        var boss = await AirshipBarrelBoss(bot);
        await OnLoop(() =>
        {
            Service<MonsterService>().GmCastBossSkill(boss, Session(bot), 5352801);
            return Task.CompletedTask;
        });
        await Kill(bot, boss);
        await Stage(bot, 6, quest: 5);
        await bot.HeartbeatAsync();
        // 施放到正常擊殺之間可能已有酒桶出生；驗證死亡後取消剩餘排程，而不是抹除合法歷史。
        int bornBeforeWait = bot.Inbox.Snapshot().Where(p => p.IsGame(GameAoiSink.SyncAoiView))
            .SelectMany(p => AoIViewChangeData.Parser.ParseFrom(p.Body).Bases).Count(u => u.EntityTemplateID == 5300020);
        await Task.Delay(1800);
        await bot.HeartbeatAsync();
        Assert.Equal(bornBeforeWait, bot.Inbox.Snapshot().Where(p => p.IsGame(GameAoiSink.SyncAoiView))
            .SelectMany(p => AoIViewChangeData.Parser.ParseFrom(p.Body).Bases).Count(u => u.EntityTemplateID == 5300020));
        await OnLoop(() =>
        {
            Assert.DoesNotContain(Service<World>().AllUnits(), u => u.InstanceId == boss.InstanceId
                && u is DungeonService.SceneActorUnit { NpcId: 5300020 });
            return Task.CompletedTask;
        });
    }

    private async Task<MonsterUnit> AirshipBarrelBoss(BotClient bot)
    {
        AirshipStoryTestPace();
        await Gm(bot, "!lvl 99");
        await Gm(bot, "!quest accept 10103210");
        Assert.Equal(0, await Enter(bot, new EnterDungeonReq { SceneEntry = LifecycleAirship }));
        long instance = await Load(bot, LifecycleAirship);
        await Stage(bot, 2, quest: 2);
        foreach (var ship in await WaveIn(instance, 3)) await Kill(bot, ship);
        foreach (var (stage, count) in new[] { (3, 4), (4, 5) })
        {
            await Stage(bot, stage, quest: 3);
            foreach (var enemy in await WaveIn(instance, count)) await Kill(bot, enemy);
        }
        await Stage(bot, 5, quest: 4);
        var boss = await BossIn(instance, 5300018);
        await Beside(bot, boss.X, boss.Y, boss.Z);
        await OnLoop(() => { boss.StaysOutOfBattle = true; return Task.CompletedTask; });
        return boss;
    }
}
