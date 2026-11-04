using Romsg;
using Ro3Emu.Server.Game;
using Microsoft.Extensions.DependencyInjection;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>Rows outside the named groups carry the state their _kBuffCategory says, on a player and on a monster.</summary>
    [ClientDataTheory]
    [InlineData(1200401, 18)] // Generic Stun, category 13
    [InlineData(1210701, 18)] // Meteor Storm's stun, category 13
    [InlineData(1200501, 21)] // Generic Immobilize, category 8
    [InlineData(1210601, 21)] // Quagmire, category 8
    [InlineData(1230401, 20)] // a skill's Silence, category 6 and _iType 0
    [InlineData(4340301, 12)] // a monster's Petrify, category 10
    public async Task AControlBuffCarriesTheStateOfItsCategory(int buff, int state)
    {
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        var a = Session("alice");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var (onPlayer, onMonster) = await Loop.InvokeAsync(async () =>
        {
            await buffs.AddAsync(a, buff, 60_000, 7);
            await buffs.AddSupportAsync(target, buff, 60_000, 7);
            return (a.Buffs.Single(b => b.ConfigId == buff).StateOrdinal, buffs.UnitStatus(target));
        });
        Assert.Equal(state, onPlayer);
        Assert.NotEqual(0UL, onMonster & 1UL << (state - 1));
    }

    /// <summary>A category outside the control kinds, or a beneficial row, carries no state: Blind and Burn stay plain debuffs.</summary>
    [ClientDataTheory]
    [InlineData(902501)] // Blind
    [InlineData(902701)] // Burn
    public async Task OtherDebuffsCarryNoState(int buff)
    {
        await using var alice = await InWorld("Alice");
        var a = Session("alice");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        int state = await Loop.InvokeAsync(async () =>
        {
            await buffs.AddAsync(a, buff, 60_000, 7);
            return a.Buffs.Single(b => b.ConfigId == buff).StateOrdinal;
        });
        Assert.Equal(0, state);
    }
}
