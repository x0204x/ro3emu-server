using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>From the player's sheet to a hit: BuffConfig 990801 raises 1215 and 1216 by 10%, through the same formula a fight uses.</summary>
public sealed partial class DungeonTests
{
    private const int TenPercentBothWays = 990801;

    [ClientDataFact]
    public async Task ABuffsDamageAttributesReachBothSidesOfAHit()
    {
        var template = Service<MonsterService>().Template(Boss)!;
        var player = await ReferencePlayer("Attr", template.Level);
        var combat = Service<CombatService>();
        await OnLoop(async () =>
        {
            var before = combat.Duel(player, template);
            await Service<BuffService>().AddAsync(player, TenPercentBothWays, 60000, player.Role!.Id);
            var after = combat.Duel(player, template);
            Assert.Equal(before.PlayerDps * 1.1, after.PlayerDps, 3);
            Assert.Equal(before.MonsterDps * 0.9, after.MonsterDps, 3);
        });
    }

    [ClientDataFact]
    public async Task APetTowerBuffsDamageAttributesKeepTheirOwnPath()
    {
        var template = Service<MonsterService>().Template(Boss)!;
        var player = await ReferencePlayer("AttrT", template.Level);
        var combat = Service<CombatService>();
        await OnLoop(async () =>
        {
            var before = combat.Duel(player, template);
            await Service<BuffService>().AddAsync(player, TenPercentBothWays, 60000, TowerPetUnit.UidBase);
            var after = combat.Duel(player, template);
            Assert.Equal(before.PlayerDps, after.PlayerDps, 3);
            Assert.Equal(before.MonsterDps, after.MonsterDps, 3);
        });
    }

    [ClientDataFact]
    public async Task AGuardiansDamageAttributesKeepTheirOwnPath()
    {
        var template = Service<MonsterService>().Template(Boss)!;
        var player = await ReferencePlayer("AttrG", template.Level);
        var combat = Service<CombatService>();
        await OnLoop(async () =>
        {
            var before = combat.Duel(player, template);
            await Service<BuffService>().AddGuardianAsync(player, TenPercentBothWays, 60000, player.Role!.Id);
            var after = combat.Duel(player, template);
            Assert.Equal(before.PlayerDps, after.PlayerDps, 3);
            Assert.Equal(before.MonsterDps, after.MonsterDps, 3);
        });
    }
}
