using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>A base level up refills HP and SP (classic RO, from akreao's fork): the first quest's Porings level a Novice mid-fight.</summary>
public sealed partial class DungeonTests
{
    [ClientDataFact]
    public async Task BaseLevelUpRefillsHpAndSp()
    {
        var bot = await InWorld("Healer");
        var character = Service<CharacterService>();
        var s = Session(bot);
        await OnLoop(async () =>
        {
            await character.SetHpSpAsync(s, 10, 5);
            await character.AddExpAsync(s, 1, 0); // lv1 needs 5: no level up, no heal
        });
        Assert.Equal(1, s.Progress!.Level);
        Assert.True(s.CurrentHp < 30, $"HP {s.CurrentHp}"); // still hurt (natural regen may add a little)

        await OnLoop(() => character.AddExpAsync(s, 20, 0)); // past lv2 (5) and lv3 (8)
        var stats = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(character.Stats(s)));
        Assert.True(s.Progress.Level >= 3);
        Assert.Equal(stats[101], stats[2000]); // HP full at the new maximum
        Assert.Equal(stats[102], stats[2001]);
    }
}
