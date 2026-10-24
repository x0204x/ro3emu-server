using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Arrival-stop rules use invented fixture data, so CI protects the walk-to-attack regression too.</summary>
public sealed class ApproachAttackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArrivalAnchorsTheAttackWithOrWithoutAnActiveCast(bool casting)
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var bot = await host.InWorldAsync("Alice");
        await host.Service<GameLoop>().InvokeAsync(async () =>
        {
            var session = host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == (long)bot.Self!.Uuid);
            var combat = host.Service<CombatService>();
            var attack = await PrepareAsync(host, session, casting);
            session.Position.X += 2;
            combat.PlayerMoved(session);
            Assert.Same(attack, session.Attack);
            Assert.Equal(session.Position.X, attack.StartX);
            Assert.Equal(session.Position.Z, attack.StartZ);
            Assert.False(attack.StartedWalking);
            if (casting)
            {
                Assert.Equal(session.Position, session.SkillCast!.Origin);
                Assert.False(session.SkillCast.StartedWalking);
            }
            combat.PlayerMoved(session); // a duplicate stop must leave the same attack running
            Assert.Same(attack, session.Attack);
            session.Position.X += 0.2f;
            session.Walking = true;
            combat.PlayerMoved(session);
            Assert.Null(session.Attack); // deliberate movement still cancels it
        });
    }

    [Theory]
    [InlineData(1000, false, 2, true)]
    [InlineData(0, true, 2, true)]
    [InlineData(0, false, 100, true)]
    [InlineData(0, false, 2, false)]
    public async Task OrdinaryMovementCannotReuseTheArrivalException(int ageMs, bool walking, float distance, bool startedWalking)
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var bot = await host.InWorldAsync("Alice");
        await host.Service<GameLoop>().InvokeAsync(async () =>
        {
            var session = host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == (long)bot.Self!.Uuid);
            await PrepareAsync(host, session, casting: false, ageMs, startedWalking);
            session.Position.X += distance;
            session.Walking = walking;
            host.Service<CombatService>().PlayerMoved(session);
            Assert.Null(session.Attack);
        });
    }

    private static async Task<AttackLoop> PrepareAsync(SyntheticHost host, GameSession session, bool casting,
        int ageMs = 0, bool startedWalking = true)
    {
        var origin = session.Position.Clone();
        var target = await host.Service<MonsterService>().SpawnSingleAsync(session.SceneEntry, 16320001,
            new MapPoint(origin.X, origin.Y, origin.Z, 0), session.InstanceId);
        Assert.NotNull(target);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var walkedFrom = DateTime.UtcNow.AddSeconds(-1);
        session.Attack = new AttackLoop
        {
            Target = target, SkillId = 910001, NextSwingMs = now + 60000,
            StartX = origin.X, StartZ = origin.Z, StartedWalking = startedWalking,
            StartMs = now - ageMs, ArrivalSpeed = 7, WalkFromUtc = walkedFrom,
        };
        if (casting)
            session.SkillCast = new ActiveSkillCast
            {
                Id = 1, OwnerId = session.Role!.Id, WorldScene = session.WorldScene, Origin = origin,
                Request = new CastSkillReq { SkillId = 910001, TargetId = target.Uid },
                Row = JsonSerializer.SerializeToElement(new { _bMovement = 0 }),
                StartedWalking = true, WalkFromUtc = walkedFrom,
                StartMs = now, ChantMs = 0, ActionMs = 60000,
            };
        session.Walking = false;
        return session.Attack;
    }
}
