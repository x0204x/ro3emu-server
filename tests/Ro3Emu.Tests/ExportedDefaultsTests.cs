using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Tests;

/// <summary>Grouped tables keep the defaults of their inner rows through the export (LuaConfigExporter).</summary>
public sealed class ExportedDefaultsTests
{
    private static GameData Data() => new(Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") }),
        NullLogger<GameData>.Instance);

    [ClientDataTheory]
    [InlineData(601)]
    [InlineData(602)]
    [InlineData(603)]
    public void EveryRowOfASurvivorChestGroupHasAWeight(int group)
    {
        var rows = Data().Row("EatChickenDropGroupConfig", group)!.Value;
        Assert.All(rows.EnumerateArray(), row => Assert.True(GameData.Int(row, "_iWeights") > 0));
    }

    [ClientDataFact]
    public void ANpcDropRowHasItsDefaultPool()
    {
        var rows = Data().Row("NPCDropConfig", 10020)!.Value;
        Assert.Equal(20, GameData.Int(rows.EnumerateObject().First().Value, "_iDropPool1"));
    }
}
