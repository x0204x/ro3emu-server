using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Tests;

/// <summary>CampFireBonus, the Bonfire Party prize of a transformation by prize level.</summary>
public sealed class CampfireBonusTests
{
    private static readonly Lazy<GameData> Exported = new(() => new GameData(
        Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") }), NullLogger<GameData>.Instance));

    [ClientDataFact]
    public void APrizeLevelPicksItsAwardGroupAndShownValue()
    {
        // 7020101 Transform: Poring at size 1: First 170301, Second 170201, Third 170101, each shown as 1000.
        var poring = CampFireBonus.Get(Exported.Value, 7020101)!.Value;
        Assert.Equal((170301L, 1000L), poring.Prize(1));
        Assert.Equal((170201L, 1000L), poring.Prize(2));
        Assert.Equal((170101L, 1000L), poring.Prize(3));
        Assert.Equal((0L, 0L), poring.Prize(4));
        Assert.Equal((170303L, 3000L), CampFireBonus.Get(Exported.Value, 7020503)!.Value.Prize(1));
    }
}
