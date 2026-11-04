using Ro3Emu.Server.Game;
using Microsoft.Extensions.DependencyInjection;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>Petrify (902101) has PDEF% -1500 and the frost after a Freeze (902601) MSPD -5000. Both now count on a monster,
    /// and both end with the buff.</summary>
    [ClientDataFact]
    public async Task ADebuffChangesAMonstersStatsUntilItEnds()
    {
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        var (before, during, speedBefore, speedDuring, after) = await Loop.InvokeAsync(async () =>
        {
            target.Template = target.Template with { Def = 1000 };
            long def = combat.MonsterStats(target)[AttributeId.Def];
            int speed = target.SpeedCm(MonsterService.NowMs);
            await buffs.AddSupportAsync(target, 902101, 60_000, 7);
            long lowered = combat.MonsterStats(target)[AttributeId.Def];
            await buffs.AddSupportAsync(target, 902601, 60_000, 7);
            int slowed = target.SpeedCm(MonsterService.NowMs);
            await buffs.ClearUnitAsync(target);
            return (def, lowered, speed, slowed, combat.MonsterStats(target)[AttributeId.Def]);
        });
        Assert.Equal(1000, before);
        Assert.Equal(850, during);
        Assert.Equal(speedBefore / 2, speedDuring);
        Assert.Equal(1000, after);
    }
}
