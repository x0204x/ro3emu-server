using System.Text.Json;
using Ro3Emu.DataExport;

namespace Ro3Emu.Setup.Tests;

/// <summary>
/// An export is complete or says it is not, a damaged data folder is exported again, and a swap cut anywhere leaves
/// the previous export (or no stamp at all), never a mix that counts as up to date.
/// </summary>
public sealed class ExportCommitTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ro3emu-export-").FullName;
    private string Data => Path.Combine(_dir, "data");
    private string Client => Path.Combine(_dir, "client");

    public ExportCommitTests()
    {
        Directory.CreateDirectory(Path.Combine(GameDataExport.LuaPayloadDir(Client), "Config"));
        File.WriteAllText(Path.Combine(GameDataExport.LuaPayloadDir(Client), "Config", "a.lua.bytes"), "x");
        Directory.CreateDirectory(GameDataExport.VfsDir(Client));
        File.WriteAllText(Path.Combine(GameDataExport.VfsDir(Client), "assetmeta.data"), "y");
    }

    /// <summary>A data or staging folder: every exported folder with <paramref name="files"/> files, and both top files.</summary>
    private static void Fill(string root, string tag, int files = 2)
    {
        foreach (var folder in GameDataExport.Folders)
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
            for (int i = 0; i < files; i++) File.WriteAllText(Path.Combine(root, folder, $"{i}.json"), tag);
        }
        File.WriteAllText(Path.Combine(root, "config", "MapResource.json"), tag);
        foreach (var file in GameDataExport.TopFiles) File.WriteAllText(Path.Combine(root, file), tag);
    }

    private void Stamp(string[]? failed = null, int format = GameDataExport.FormatVersion, Dictionary<string, int>? files = null)
    {
        files ??= GameDataExport.Folders.ToDictionary(f => f, f => Directory.GetFiles(Path.Combine(Data, f), "*", SearchOption.AllDirectories).Length);
        var stamp = new GameDataExport.Stamp(format, GameDataExport.Fingerprint(Client), Client, DateTime.UtcNow, 100, 1, 1,
            GameDataExport.QuickFingerprint(Client), files, failed ?? []);
        File.WriteAllText(Path.Combine(Data, "export.json"), JsonSerializer.Serialize(stamp));
    }

    private static string Tag(string root) => File.ReadAllText(Path.Combine(root, "config", "MapResource.json"));

    [Fact]
    public void ACompleteStampedExportIsUpToDate()
    {
        Fill(Data, "old");
        Stamp();
        Assert.True(GameDataExport.UpToDate(Data, Client));
    }

    [Fact]
    public void FailedTablesAnOlderFormatOrAMissingStampMeanExportAgain()
    {
        Fill(Data, "old");
        Stamp(failed: ["SomeTable"]);
        Assert.False(GameDataExport.UpToDate(Data, Client));
        Stamp(format: 1);
        Assert.False(GameDataExport.UpToDate(Data, Client));
        File.Delete(Path.Combine(Data, "export.json"));
        Assert.False(GameDataExport.UpToDate(Data, Client));
    }

    [Theory]
    [InlineData("skills")]
    [InlineData("nav")]
    public void AFolderThatLostOrGainedFilesIsExportedAgain(string folder)
    {
        Fill(Data, "old");
        Stamp();
        File.Delete(Path.Combine(Data, folder, "0.json"));
        Assert.False(GameDataExport.UpToDate(Data, Client));
        File.WriteAllText(Path.Combine(Data, folder, "0.json"), "old");
        File.WriteAllText(Path.Combine(Data, folder, "extra.json"), "old");
        Assert.False(GameDataExport.UpToDate(Data, Client));
    }

    [Fact]
    public void AMissingTopFileIsExportedAgain()
    {
        Fill(Data, "old");
        Stamp();
        File.Delete(Path.Combine(Data, "loc_en.tsv"));
        Assert.False(GameDataExport.UpToDate(Data, Client));
    }

    [Fact]
    public void TheSwapReplacesEveryEntryAndRemovesTheStampFirst()
    {
        Fill(Data, "old");
        Stamp();
        var staged = Path.Combine(_dir, "staged");
        Fill(staged, "new", files: 3);
        GameDataExport.SwapIn(Data, staged, Path.Combine(_dir, "old"));
        Assert.Equal("new", Tag(Data));
        Assert.All(GameDataExport.TopFiles, f => Assert.Equal("new", File.ReadAllText(Path.Combine(Data, f))));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(Data, "skills")).Length);
        Assert.False(File.Exists(Path.Combine(Data, "export.json"))); // the caller writes the new stamp last
    }

    [Theory]
    [InlineData("aside:config")]
    [InlineData("in:config")]
    [InlineData("in:sceneeditor")]
    [InlineData("aside:nav")]
    [InlineData("in:loc_en.tsv")]
    public void ASwapFailingAtAnyStepPutsThePreviousExportBack(string failAt)
    {
        Fill(Data, "old");
        var staged = Path.Combine(_dir, "staged");
        Fill(staged, "new", files: 3);
        Assert.Throws<IOException>(() => GameDataExport.SwapIn(Data, staged, Path.Combine(_dir, "old"),
            step => { if (step == failAt) throw new IOException("disk full"); }));
        Assert.Equal("old", Tag(Data));
        foreach (var folder in GameDataExport.Folders) // the old export had 2 files each (config also MapResource.json)
            Assert.Equal(folder == "config" ? 3 : 2, Directory.GetFiles(Path.Combine(Data, folder)).Length);
        Assert.All(GameDataExport.TopFiles, f => Assert.Equal("old", File.ReadAllText(Path.Combine(Data, f))));
        Assert.Equal("new", Tag(staged)); // the new export is still staged, nothing lost either way
        Assert.False(GameDataExport.UpToDate(Data, Client)); // no stamp: the next start exports again
    }

    [Theory]
    [InlineData(@"Config\DataConfig\ItemConfig.lua.bytes: bad chunk", "ItemConfig")]
    [InlineData("Config/ComplexConfig/DropPoolConfig.lua.bytes: truncated", "DropPoolConfig")]
    [InlineData(@"Config\SceneEditor\Sc_x.lua.bytes: bad", "Sc_x")]
    [InlineData(@"Config\Skill\Skill_1.lua.bytes: bad", "Skill_1")]
    [InlineData("Localization_en.lua.bytes: bad", "Localization_en")]
    [InlineData(@"Logic\Asset\LC_DAL_Bag.lua.bytes: bad", null)]
    [InlineData(@"UI\Main\Something.lua.bytes: bad", null)]
    public void AChunkThatDoesNotDecodeIsAMissingTableWhenTheExportReadsIt(string error, string? table) =>
        Assert.Equal(table, GameDataExport.TableOfDecodeError(error));

    private string Journal => Path.Combine(Data, ".export", "swapping");

    [Fact]
    public void TheSwapJournalIsWrittenBeforeAnythingMovesAndRemovedAfterARollback()
    {
        Fill(Data, "old");
        Stamp();
        var staged = Path.Combine(_dir, "staged");
        Fill(staged, "new", files: 3);
        var old = Path.Combine(Data, ".export", "old");
        Assert.Throws<IOException>(() => GameDataExport.SwapIn(Data, staged, old, step =>
        {
            if (step == "aside:export.json") Assert.True(File.Exists(Journal));
            if (step == "in:skills") throw new IOException("disk full");
        }));
        Assert.False(File.Exists(Journal));
        Assert.True(GameDataExport.UpToDate(Data, Client)); // the stamp came back with everything else
    }

    [Theory]
    [InlineData("in:config")]
    [InlineData("in:skills")]
    [InlineData("in:loc_en.tsv")]
    public void AnExportThatDiedMidSwapIsPutBackBeforeTheNextOneClearsItsWork(string diesAt)
    {
        Fill(Data, "old");
        Stamp();
        var staged = Path.Combine(_dir, "staged");
        Fill(staged, "new", files: 3);
        var old = Path.Combine(Data, ".export", "old");
        // A process that dies runs no catch: model it by copying the folder as it stood at that step, then restoring it.
        var snapshot = Path.Combine(_dir, "snapshot");
        Assert.Throws<IOException>(() => GameDataExport.SwapIn(Data, staged, old, step =>
        {
            if (step != diesAt) return;
            CopyTree(Data, snapshot);
            throw new IOException("process died");
        }));
        Directory.Delete(Data, true);
        Directory.Move(snapshot, Data);
        Assert.True(File.Exists(Journal));
        Assert.False(GameDataExport.UpToDate(Data, Client)); // never counted as up to date mid-swap

        GameDataExport.RecoverInterruptedSwap(Data);
        Assert.False(File.Exists(Journal));
        Assert.Equal("old", Tag(Data));
        Assert.All(GameDataExport.TopFiles, f => Assert.Equal("old", File.ReadAllText(Path.Combine(Data, f))));
        foreach (var folder in GameDataExport.Folders)
            Assert.Equal(folder == "config" ? 3 : 2, Directory.GetFiles(Path.Combine(Data, folder)).Length);
        Assert.True(GameDataExport.UpToDate(Data, Client)); // the previous generation, stamp and all
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

/// <summary>The whole export from the installed client (about a minute): complete, and up to date afterwards.</summary>
public sealed class RealClientExportTests
{
    private static readonly string Client = Environment.GetEnvironmentVariable("RO3_CLIENT_DIR") is { Length: > 0 } dir ? dir : @"D:\RO3\ROO_PC";

    public sealed class ClientFactAttribute : FactAttribute
    {
        public ClientFactAttribute()
        {
            if (GameDataExport.Missing(Client).Count > 0) Skip = "the RO3 client is not installed (set RO3_CLIENT_DIR)";
        }
    }

    [ClientFact, Trait("Category", "Slow")]
    public void TheInstalledClientExportsCompletelyAndIsThenUpToDate()
    {
        var data = Directory.CreateTempSubdirectory("ro3emu-realexport-").FullName;
        try
        {
            var result = GameDataExport.Run(Client, data);
            Assert.Empty(result.FailedTables); // Lua_DBManager is code, the one module that never exports
            Assert.True(GameDataExport.UpToDate(data, Client));
            Assert.Empty(GameDataExport.ReadStamp(data)!.FailedTables!);
            Assert.False(Directory.Exists(Path.Combine(data, ".export")));
        }
        finally { Directory.Delete(data, true); }
    }
}
