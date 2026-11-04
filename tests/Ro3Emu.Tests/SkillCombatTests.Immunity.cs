using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private const int DebuffImmunity = 903301, EndureRow = 903101, Stun = 901601;

    [ClientDataFact] // Dragon Howling, Shield Block: "Immune to all debuffs", whoever gave either buff
    public async Task AnOrdinaryImmunityRefusesAnOrdinaryDebuff()
    {
        await using var alice = await InWorld("Alice");
        var a = Session("alice");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(async () =>
        {
            await buffs.AddAsync(a, DebuffImmunity, 60000, a.Role!.Id);
            await buffs.AddAsync(a, Stun, 5000, 1);
            Assert.DoesNotContain(a.Buffs, b => b.ConfigId == Stun);
        });
    }

    [ClientDataFact] // without it the same debuff lands
    public async Task TheSameDebuffLandsWithoutImmunity()
    {
        await using var alice = await InWorld("Alice");
        var a = Session("alice");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(async () =>
        {
            await buffs.AddAsync(a, Stun, 5000, 1);
            Assert.Contains(a.Buffs, b => b.ConfigId == Stun);
        });
    }

    [ClientDataFact] // Endure: "Immune to knockback, knock-up"
    public async Task EndureHoldsAPlayerInPlace()
    {
        await using var alice = await InWorld("Alice");
        var a = Session("alice");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        Assert.False(buffs.ResistsDisplacement(a));
        await Loop.InvokeAsync(() => buffs.AddAsync(a, EndureRow, 60000, a.Role!.Id));
        Assert.True(buffs.ResistsDisplacement(a));
    }

    [ClientDataFact] // Praefatio: "Immunity to up to 3 ... effects"; level 1 is ["20","7","3"]
    public async Task PraefatioSpendsItsCharges()
    {
        await using var alice = await InWorld("Alice");
        var a = Session("alice");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(async () =>
        {
            await buffs.AddAsync(a, 1430201, 60000, a.Role!.Id);
            for (int i = 0; i < 3; i++)
            {
                await buffs.AddAsync(a, Stun, 5000, 1);
                Assert.DoesNotContain(a.Buffs, b => b.ConfigId == Stun);
            }
            Assert.DoesNotContain(a.Buffs, b => b.ConfigId == 1430201);
            await buffs.AddAsync(a, Stun, 5000, 1);
            Assert.Contains(a.Buffs, b => b.ConfigId == Stun);
        });
    }

    [ClientDataFact] // Berserk: "Gain Slow Immunity"
    public async Task BerserkRefusesASlow()
    {
        await using var alice = await InWorld("Alice");
        var a = Session("alice");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(async () =>
        {
            await buffs.AddAsync(a, 1121401, 60000, a.Role!.Id);
            await buffs.AddAsync(a, 902601, 5000, 1);
            Assert.DoesNotContain(a.Buffs, b => b.ConfigId == 902601);
        });
    }
}
