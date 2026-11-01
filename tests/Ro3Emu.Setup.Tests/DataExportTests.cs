using Ro3Emu.ClientTools;
using Ro3Emu.DataExport;

namespace Ro3Emu.Setup.Tests;

public class DataExportTests
{
    // Reference values from tools/WorldData/vfsget.js (lowerHash), covering every tail length class.
    [Theory]
    [InlineData("", 0UL)]
    [InlineData("a", 9607679276477937801UL)]
    [InlineData("abcdefgh", 14738604482492337154UL)]
    [InlineData("abcdefghi", 380484692874131812UL)]
    [InlineData("ABCDEFGHIJKLMNO", 9997468155419308027UL)]
    [InlineData("abcdefghijklmnop", 14180213048082216739UL)]
    [InlineData("abcdefghijklmnopq", 8459014091212432983UL)]
    [InlineData("sc_prt_fild08_001_dynamicmapdata.bytes", 7007743005348290919UL)]
    [InlineData("Sc_Prontera_001data.bytes", 4666445406827049485UL)]
    public void VfsHashMatchesTheJavaScriptExport(string name, ulong expected) => Assert.Equal(expected, Vfs.LowerHash(name));

    [Theory]
    [InlineData("sc_prt_fild08_001data.bytes", "sc_prt_fild08_001_dynamicmapdata.bytes")]
    [InlineData("sc_x_dynamicmapdata.bytes", "sc_x_dynamicmapdata.bytes")]
    [InlineData("a data.bytes data.bytes", "a _dynamicmapdata.bytes data.bytes")]
    public void NavFileNameReplacesTheFirstSuffix(string navName, string file) => Assert.Equal(file, NavExporter.FileName(navName));

    [Fact]
    public void NavNamesAreDistinctInTableOrder()
    {
        var dir = Directory.CreateTempSubdirectory("ro3emu-nav-");
        try
        {
            var json = Path.Combine(dir.FullName, "MapResource.json");
            File.WriteAllText(json, """{"1":{"_kNavDataName":"b data.bytes"},"2":{"_kNavDataName":"a data.bytes"},"3":{"_kNavDataName":"b data.bytes"},"4":{"_kNavDataName":""},"5":{}}""");
            Assert.Equal(["b data.bytes", "a data.bytes"], NavExporter.NavNames(json));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void ClientScenesListsTheMapsWhoseUnitySceneTheVfsLacks()
    {
        var dir = Directory.CreateTempSubdirectory("ro3emu-scenes-");
        try
        {
            // An asset index with one bundle entry (27 bytes: asset hash, load type, dyn id, file hash, VFS hash) and no bytes entries.
            var meta = new byte[4 + 27 + 4];
            BitConverter.TryWriteBytes(meta.AsSpan(0), 1u);
            BitConverter.TryWriteBytes(meta.AsSpan(4), Vfs.LowerHash("SC_Town.unity"));
            File.WriteAllBytes(Path.Combine(dir.FullName, "assetmeta.data"), meta);
            var json = Path.Combine(dir.FullName, "MapResource.json");
            File.WriteAllText(json, """{"1":{"_kResource":"sc_town"},"2":{"_kResource":"sc_field"},"3":{"_kResource":"sc_field"},"4":{"_kResource":""},"5":{}}""");
            using var vfs = new Vfs(dir.FullName);
            var file = Path.Combine(dir.FullName, ClientScenes.FileName);

            Assert.Equal(["sc_field"], ClientScenes.Write(json, vfs, file));
            using var written = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            Assert.Equal(["sc_field"], written.RootElement.GetProperty("Missing").EnumerateArray().Select(e => e.GetString()));
        }
        finally { dir.Delete(true); }
    }

    /// <summary>The installed client against the exported MapResource table: Glast Heim and the five eastern towns have
    /// walk maps but no Unity scene (issue: a GM warp to Glast Heim left the character on "Failed to load the area").</summary>
    [ClientWithDataFact]
    public void TheInstalledClientHasNoSceneForGlastHeim()
    {
        using var vfs = new Vfs(GameDataExport.VfsDir(RealClientExportTests.Client));

        var missing = ClientScenes.Missing(ClientWithDataFactAttribute.MapResource!, vfs);

        Assert.Contains("sc_glast_01_001", missing); // MapConfig 10700 Glast Heim the Ancient City
        Assert.Contains("sc_gl_prison_001", missing); // 10791, one of its nine floors
        Assert.Contains("sc_amatsu_001", missing); // 10601
        Assert.DoesNotContain("sc_prontera_001", missing);
        Assert.DoesNotContain("sc_gef_dun02_001", missing); // Geffen Tower 3, a floor that loads
        Assert.True(vfs.Has("sc_glast_01_001_dynamicmapdata.bytes")); // its walk map is there, so the server ran the map
    }

    [Fact]
    public void LocMapTakesTheClientsMostFrequentEnglishThenOurTranslations()
    {
        var dir = Directory.CreateTempSubdirectory("ro3emu-loc-");
        try
        {
            var zh = Path.Combine(dir.FullName, "zh.json");
            var en = Path.Combine(dir.FullName, "en.json");
            // Keys out of numeric order: the first-seen tie break follows Object.entries (numeric keys ascending).
            File.WriteAllText(zh, """{"10":{"_kDes":"确定"},"2":{"_kDes":"确定"},"3":{"_kDes":"确定"},"4":{"_kDes":"取消"},"5":{"_kDes":"英文"},"6":{"_kDes":"无"}}""");
            File.WriteAllText(en, """{"10":{"_kDes":"OK"},"2":{"_kDes":"Confirm"},"3":{"_kDes":"OK"},"4":{"_kDes":"Cancel"},"5":{"_kDes":"中文"},"6":{"_kDes":"None"}}""");
            var map = new LocMapBuilder();
            map.AddClientTables(zh, en);
            map.AddTranslations(["# comment\n取消\tBack\n新\\t行\tNew\\nline\n坏\t坏\n"]);
            var outFile = Path.Combine(dir.FullName, "loc_en.tsv");
            map.Write(outFile);
            var lines = File.ReadAllLines(outFile);
            Assert.StartsWith(ClientPatcher.GeneratedLocHeader, lines[0]);
            Assert.Equal(["确定\tOK", "取消\tBack", "新\\t行\tNew\\nline"], lines[1..]);
            Assert.Equal((2, 2), (map.FromTables, map.Ours));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void ReferenceCheckFindsWhatAnAssemblyNeedsAndLacks()
    {
        var own = typeof(Vfs).Assembly.Location;
        var alone = Directory.CreateTempSubdirectory("ro3emu-refs-");
        try
        {
            // Without KeraLua.dll next to it (or in the folder searched), every KeraLua type and member it uses is reported.
            var copy = Path.Combine(alone.FullName, Path.GetFileName(own));
            File.Copy(own, copy);
            var missing = ClientPatcher.UnresolvedReferences(copy, alone.FullName);
            Assert.Contains(missing, r => r.Contains("KeraLua.Lua") && r.Contains("assembly KeraLua"));
            Assert.Empty(ClientPatcher.UnresolvedReferences(own, Path.GetDirectoryName(typeof(object).Assembly.Location)!));
        }
        finally { alone.Delete(true); }
    }
}

/// <summary>Runs where the RO3 client is installed and this checkout has data/config exported from it.</summary>
public sealed class ClientWithDataFactAttribute : FactAttribute
{
    public static string? MapResource { get; } = FindMapResource();

    public ClientWithDataFactAttribute()
    {
        if (GameDataExport.Missing(RealClientExportTests.Client).Count > 0) Skip = "the RO3 client is not installed (set RO3_CLIENT_DIR)";
        else if (MapResource == null) Skip = "needs data/config/MapResource.json exported from the client";
    }

    private static string? FindMapResource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx")))
                return File.Exists(Path.Combine(dir.FullName, "data", "config", "MapResource.json"))
                    ? Path.Combine(dir.FullName, "data", "config", "MapResource.json") : null;
        return null;
    }
}
