using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>AIConfig battle call: a calling monster entering a fight draws in the answering monsters of its camp nearby.</summary>
public sealed partial class MapTests
{
    [ClientDataFact]
    public async Task AMonsterEnteringAFightCallsItsAnsweringKindWithinItsRange()
    {
        await using var alice = await InWorld("Alice");
        var services = _app.Services;
        await services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
        {
            var monsters = services.GetRequiredService<MonsterService>();
            var world = services.GetRequiredService<World>();
            var player = services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
            // AIConfig 23001001 (dungeon packs, e.g. NPC 4101001): _iBattleCallType 1, distance 800 cm, response 1.
            var caller = monsters.Template(4101001)!;
            Assert.Equal(8f, caller.BattleCallRange);
            Assert.True(caller.AnswersBattleCall);
            var passive = monsters.Template(5000001)!; // AIConfig 20000001: neither calls nor answers
            Assert.Equal(0f, passive.BattleCallRange);
            Assert.False(passive.AnswersBattleCall);

            var source = world.AllUnits().OfType<MonsterUnit>().First();
            long uid = 1L << 49;
            MonsterUnit At(MonsterTemplate t, float dx) => new()
            {
                Uid = ++uid, SceneEntry = player.SceneEntry, InstanceId = player.InstanceId,
                X = player.Position.X + dx, Y = player.Position.Y, Z = player.Position.Z, Hp = 1000,
                Template = t, Group = source.Group, Home = source.Home,
            };
            var first = At(caller, 1); // the one the player pulls
            var near = At(caller, 5); // answers: 4 m from the caller
            var chained = At(caller, 12); // 4 m from "near" but 11 m from the caller: a called one does not call on
            var otherCamp = At(caller with { Camp = caller.Camp + 1 }, 3);
            var deaf = At(passive, 3);
            foreach (var unit in new[] { first, near, chained, otherCamp, deaf }) await world.AddUnitAsync(unit);

            monsters.Engage(first, player);
            Assert.Same(player, first.Target);
            Assert.Same(player, near.Target);
            Assert.Null(chained.Target);
            Assert.Null(otherCamp.Target);
            Assert.Null(deaf.Target);
        });
    }
}
