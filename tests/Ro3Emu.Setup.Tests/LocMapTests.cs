using Ro3Emu.ClientTools;
using Ro3Emu.DataExport;

namespace Ro3Emu.Setup.Tests;

/// <summary>
/// "RO3Emu.exe join" runs no export, so it had no data/loc_en.tsv to copy next to the game and the client drew its
/// Chinese strings (skill names, "利刃重击 is on cooldown.") untranslated. The map alone is now built from the client.
/// </summary>
public sealed class LocMapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-locmap-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void AClientWithoutTheLocalizationChunksIsReportedNotMapped()
    {
        Directory.CreateDirectory(GameDataExport.LuaPayloadDir(_dir));
        var e = Assert.Throws<ExportException>(() => GameDataExport.WriteLocMap(_dir, Path.Combine(_dir, "data", "loc_en.tsv")));
        Assert.Contains("Localization_zh_CN.lua.bytes", e.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "data", "loc_en.tsv")));
    }

    [GenesisClientFact]
    public void TheEnglishMapAloneComesFromTheClientsOwnTextTables()
    {
        var client = Environment.GetEnvironmentVariable("RO3_GENESIS_CLIENT")!;
        var map = Path.Combine(_dir, "data", "loc_en.tsv");

        int entries = GameDataExport.WriteLocMap(client, map, new GameDataExport.Options { Translations = Flows.Translations() });

        Assert.True(entries > 10000, $"{entries} entries");
        var lines = File.ReadAllLines(map);
        Assert.StartsWith(ClientPatcher.GeneratedLocHeader, lines[0]); // PatchClient replaces only a map it generated
        Assert.Contains("利刃重击\tBlade Smash", lines); // SkillConfig 1509901 _iName 10110201064
        foreach (string name in new[] { TraditionalChineseMapBuilder.TextFile, TraditionalChineseMapBuilder.SidFile })
            Assert.Equal(TraditionalChineseMapBuilder.Header, File.ReadLines(Path.Combine(_dir, "data", name)).First());
        Assert.False(Directory.Exists(Path.Combine(_dir, "data", ".locmap")));
    }
}

/// <summary>Runs only where a real client is given (RO3_GENESIS_CLIENT, the folder with ro3.exe).</summary>
public sealed class GenesisClientFactAttribute : FactAttribute
{
    public GenesisClientFactAttribute()
    {
        var client = Environment.GetEnvironmentVariable("RO3_GENESIS_CLIENT");
        if (client == null || !File.Exists(Path.Combine(client, "ro3.exe"))) Skip = "Set RO3_GENESIS_CLIENT (the folder with ro3.exe).";
    }
}
