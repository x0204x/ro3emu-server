using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Item score field contract and the bounded inferred formula.</summary>
public sealed class EquipmentRatingTests
{
    private static readonly GameData Data = new(
        Options.Create(new ServerOptions { DataDir = Path.Combine(RepoRoot(), "data") }), NullLogger<GameData>.Instance);

    [ClientDataFact]
    public void AppraisedCutterUsesItsActualRolledValuesAffixAndSpecial()
    {
        // ItemConfig/EquipConfig 2010250: Cutter has two base attributes, PATK=103 and MATK=104.
        // AttributeConfig weights: 103=5, 104=5, STR(1)=50; SpecialEffectConfig[1]._iPower=100.
        var item = new InvItem
        {
            Entry = 2010250, Attrs = new()
            {
                [3] = 1, [30] = 103, [60] = 38, [31] = 104, [61] = 41,
                [100] = 1, [140] = 2, [80] = 1,
            }
        };

        Assert.Equal(595, EquipmentRating.Calculate(item, Data));
        item.Attrs[60] = 51;
        Assert.Equal(660, EquipmentRating.Calculate(item, Data));
    }

    [ClientDataFact]
    public void FractionalConfigWeightRoundsOnceAndStopsAtFirstIncompletePair()
    {
        // AttributeConfig[101]._iPower=0.25; slot 1 is missing its value, so slot 2 is not parsed by the client.
        var item = new InvItem
        {
            Entry = 2010250, Attrs = new()
            {
                [3] = 1, [30] = 101, [60] = 3, [31] = 103, [32] = 103, [62] = 100,
                [100] = 1, [140] = 1,
            }
        };

        Assert.Equal(51, EquipmentRating.Calculate(item, Data)); // 3*0.25 + 1*50 = 50.75
    }

    [ClientDataFact]
    public void UnappraisedAndNonEquipmentInstancesHaveNoCalculatedScore()
    {
        var cutter = new InvItem { Entry = 2010250, Attrs = new() { [3] = 0, [30] = 103, [60] = 50, [2] = 777 } };
        Assert.Equal(0, EquipmentRating.Calculate(cutter, Data));

        cutter.Attrs[3] = 1;
        Assert.Equal(250, EquipmentRating.Calculate(cutter, Data)); // stored 2 is an output, not an input to the proxy
        cutter.Entry = 2; // currency ItemConfig entry
        Assert.Equal(0, EquipmentRating.Calculate(cutter, Data));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
