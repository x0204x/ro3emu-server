using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed class MonsterDeathMovementTests
{
    [Fact]
    public async Task DeathStopsThePublishedWalkBeforeDeadStateAndRemovesTheCorpseOnce()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var bot = await host.InWorldAsync("CorpseWalk");
        var loop = host.Service<GameLoop>();
        var world = host.Service<World>();
        var monsters = host.Service<MonsterService>();
        MonsterUnit monster = await loop.InvokeAsync(async () =>
        {
            var player = host.Service<SessionRegistry>().All().Single(s => s.Role != null);
            var template = monsters.Template(16320002)! with { BodyRemainMs = 300, RoamDistance = 0 };
            return (await monsters.SpawnSingleAsync(player.RuntimeMap, template.NpcId,
                new MapPoint(player.Position.X + 10, player.Position.Y, player.Position.Z, 270), customTemplate: template))!;
        });
        Vec3 position = await loop.InvokeAsync(async () =>
        {
            monster.Moving = true;
            monster.StartMs = MonsterService.NowMs;
            monster.EndMs = monster.StartMs + 4000;
            monster.FromX = monster.X; monster.FromY = monster.Y; monster.FromZ = monster.Z;
            monster.ToX = monster.X - 10; monster.ToY = monster.Y; monster.ToZ = monster.Z;
            monster.SegmentSpeed = 2.5f;
            await world.MoveUnitAsync(monster, new MovementChangeNtf
            {
                Uid = monster.Uid, MovementInfo = monster.CurrentMovement(monster.StartMs),
            });
            Vec3 at = monster.Pos;
            monster.Hp = 0;
            await monsters.KillAsync(monster);
            await monsters.KillAsync(monster);
            Assert.False(monster.Moving);
            Assert.Equal(at, monster.Pos);
            return at;
        });
        await bot.Inbox.WaitAsync(e => e.IsGame(GameAoiSink.SyncAoiView)
            && AoIViewChangeData.Parser.ParseFrom(e.Body).LeaveAoi.Contains((ulong)monster.Uid), TimeSpan.FromSeconds(5));
        var events = bot.Inbox.Snapshot().ToArray();
        var movements = events.Where(e => e.IsGame(GameAoiSink.MovementChange))
            .Select(e => MovementChangeNtf.Parser.ParseFrom(e.Body)).Where(m => m.Uid == monster.Uid).ToArray();
        Assert.Collection(movements,
            walk => Assert.Equal(MovementType.MoveTypeWalk, walk.MovementInfo.MoveType),
            stop =>
            {
                Assert.Equal(MovementType.MoveTypeNone, stop.MovementInfo.MoveType);
                Assert.Equal(0, stop.MovementInfo.MoveDuration);
                Assert.Equal(position, stop.MovementInfo.Pos);
            });
        int stopped = Array.FindIndex(events, e => e.IsGame(GameAoiSink.MovementChange)
            && MovementChangeNtf.Parser.ParseFrom(e.Body) is { Uid: var uid, MovementInfo.MoveType: MovementType.MoveTypeNone } && uid == monster.Uid);
        int died = Array.FindIndex(events, e => e.IsGame(CombatService.EntityBaseChange)
            && EntityBase.Parser.ParseFrom(e.Body) is { Uuid: var uid, State: EntityState.Dead } && uid == (ulong)monster.Uid);
        Assert.True(stopped >= 0 && died > stopped);
        Assert.Single(events, e => e.IsGame(GameAoiSink.SyncAoiView)
            && AoIViewChangeData.Parser.ParseFrom(e.Body).LeaveAoi.Contains((ulong)monster.Uid));
        Assert.True(await loop.InvokeAsync(() => Task.FromResult(!world.AllUnits().Contains(monster))));
    }
}
