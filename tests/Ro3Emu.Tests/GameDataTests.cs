using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Tests;

/// <summary>Client config and skill JSON exports exported from the client</summary>
public sealed class GameDataTests
{
    private static readonly GameData Data = new(
        Options.Create(new ServerOptions { DataDir = Path.Combine(RepoRoot(), "data") }), NullLogger<GameData>.Instance);

    [ClientDataFact]
    public void ConfigRowsRetainAttributePairsAndLocalizedNames()
    {
        var novice = Data.Row("JobConfig", 1000);
        Assert.NotNull(novice);
        Assert.Equal(JsonValueKind.Array, novice.Value.GetProperty("_kAttri").ValueKind);
        Assert.Equal(new[] { (115L, 100d), (113L, 700d) }, GameData.Pairs(novice.Value, "_kAttri"));
        Assert.Equal(700, Data.JobAttributes(1000)[113]);
        Assert.Equal("Novice", Data.TextOf(novice.Value, "_iName"));
        Assert.Equal("Zeny", Data.ItemName(1));
        Assert.Equal("Prontera South Gate", Data.MapName(10111));
        Assert.Equal(1, Data.FindItems("Zeny").First().Id);
        // [textId, params...]: @{n} takes the n-th parameter (gathering node names).
        Assert.Equal("Lv. 1 Ore Vein", Data.TextOf(Data.Row("NPCConfig", 7010001)!.Value, "_kName"));
    }

    [Fact]
    public void ClientVisibleTextUsesTheMatchingSourceLanguageId()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ro3emu-client-text-" + Guid.NewGuid().ToString("N"));
        string config = Path.Combine(dir, "config");
        Directory.CreateDirectory(config);
        try
        {
            File.WriteAllText(Path.Combine(config, "Localization_en.json"),
                """{"10470000823":{"_iID":10470000823,"_kDes":"Map Vendor"},"2":{"_iID":2,"_kDes":"Girl"},"3":{"_iID":3,"_kDes":"Novice"},"4":{"_iID":4,"_kDes":"Vendor @{1}"}}""");
            File.WriteAllText(Path.Combine(config, "Localization_zh_CN.json"),
                """{"10470000823":{"_iID":10470000823,"_kDes":"地图商贩"},"2":{"_iID":2,"_kDes":"少女"},"4":{"_iID":4,"_kDes":"商贩 @{1}"}}""");
            File.WriteAllText(Path.Combine(config, "Localization_zh_TW.json"),
                """{"10470000823":{"_iID":10470000823,"_kDes":"地圖商人"},"4":{"_iID":4,"_kDes":"商人 @{1}"}}""");
            File.WriteAllText(Path.Combine(config, "MapConfig.json"),
                """{"10111":{"_iID":10111,"_iName":[10470000823]}}""");
            using var row = JsonDocument.Parse("""{"_kName":[10470000823]}""");
            var data = new GameData(Options.Create(new ServerOptions { GameDataDir = dir }), NullLogger<GameData>.Instance);

            Assert.Equal("Map Vendor", data.TextOf(row.RootElement, "_kName"));
            Assert.Equal("地圖商人", data.ClientTextOf(row.RootElement, "_kName"));
            Assert.Equal("Map Vendor", data.MapName(10111));
            Assert.Equal("地圖商人", data.ClientMapName(10111));
            Assert.Equal("少女", data.ClientText(2)); // no zh_TW row: use zh_CN
            Assert.Equal("Novice", data.ClientText(3)); // neither Chinese table has the id: use English
            using var templated = JsonDocument.Parse("""{"_kName":[4,"A"]}""");
            Assert.Equal("商人 A", data.ClientTextOf(templated.RootElement, "_kName"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [ClientDataFact]
    public void SkillRowsRemainValidAfterFileParseAndConcurrentAccess()
    {
        var rows = Enumerable.Range(0, 12).AsParallel().Select(_ => Data.SkillTemplate(10000)).ToArray();
        Assert.All(rows, row =>
        {
            Assert.NotNull(row);
            Assert.Equal(1, row.Value.GetProperty("m_fTotalTime").GetDouble());
            Assert.Equal(0.2, GameData.Number(row.Value.GetProperty("m_kAtkPoints")[0]));
        });
        Assert.True(Data.SkillTemplateTable("Lua_Config_EffectData").ContainsKey("1000002"));
        Assert.Null(Data.SkillTemplate(-1));
        Assert.Null(Data.Row("NotAnExport", 1));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
