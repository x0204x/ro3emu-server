using System.Security.Cryptography;
using System.Text.Json;
using Ro3Emu.ClientTools;
using Ro3Emu.DataExport;

namespace Ro3Emu.Setup.Tests;

/// <summary>只使用自造 bytes 與純 .NET 檔案雜湊；不啟動 Setup、遊戲或原生 Lua。</summary>
public sealed class PreparedGameDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-prepared-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "data");
    private string Client => Path.Combine(_root, "client");
    private string Marker => Path.Combine(Data, PreparedGameData.MarkerName);
    private const string Lua = "ro3_Data/StreamingAssets/Recovery/LuaPayload/Config/Test.lua.bytes";
    private const string Asset = "ro3_Data/StreamingAssets/VFS/assetmeta.data";
    private const string Proto = "ro3_Data/StreamingAssets/LocalProto/proto.pb.bytes";

    public PreparedGameDataTests()
    {
        foreach (string folder in GameDataExport.Folders)
            Write(Data, folder + "/" + (folder == "config" ? "MapResource.json" : "fixture.bin"), "自造資料");
        foreach (string top in GameDataExport.TopFiles) Write(Data, top, "自造文字");
        Write(Data, "export.json", "{\"Format\":0,\"StaticRecovery\":{\"status\":\"synthetic\"}}");
        // 故意不是合法 Lua bytecode；正確匯入路徑若啟動 Lua，這份 fixture 就不能通過。
        foreach (string rel in new[] { Lua, Asset, Proto }) Write(Client, rel, "not executable: " + rel);
        VerifiedBaseline.Write(Client, "synthetic-release", 17, "synthetic-build", new string('a', 64),
            new[] { Lua, Asset, Proto }.Select(rel => new VerifiedBaseline.Entry(new FileInfo(Path.Combine(Client, rel)).Length, Hash(Path.Combine(Client, rel)), rel)));
    }

    private PreparedGameData.Manifest Prepare()
    {
        var manifest = PreparedGameData.CreateManifest(Data, Client);
        Save(manifest); return manifest;
    }
    private void Save(PreparedGameData.Manifest manifest) => File.WriteAllText(Marker, JsonSerializer.Serialize(manifest));
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Write(string root, string rel, string value)
    {
        string path = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value);
    }
    private Dictionary<string, string> Snapshot() => Directory.EnumerateFiles(Data, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(Data, path), Hash);

    [Fact]
    public void CorrectImportUsesTheProductGateWithoutAnyNativeExporter()
    {
        var manifest = Prepare();
        Assert.Equal(1, manifest.Format); Assert.Equal(7, manifest.Files.Count);
        Assert.Equal(17, manifest.Client.Sequence);
        int exports = 0; var before = Snapshot();
        Flows.EnsureGameData(Client, false, Data, (_, _) => { exports++; throw new InvalidOperationException("不應執行匯出"); });
        Assert.Equal(0, exports); Assert.Equal(before.OrderBy(p => p.Key), Snapshot().OrderBy(p => p.Key));
        // Server 產生的非資料檔不屬於 curated 靜態集合。
        Write(Data, "logs/runtime.log", "自造記錄"); Write(Data, "realm.db", "自造DB");
        Assert.True(PreparedGameData.UseIfPresent(Data, Client, false));
    }

    [Fact]
    public void OneManifestIsPortableToAnotherClientRootWithFreshBaselineWrite()
    {
        Prepare();
        string second = Path.Combine(_root, "another-install-root");
        foreach (string rel in new[] { Lua, Asset, Proto })
        {
            string target = Path.Combine(second, rel); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(Client, rel), target); File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddDays(-3));
        }
        // 另一安裝目錄重新由同一API產baseline，不複製baseline檔，也不使用來源機器路徑或mtime。
        VerifiedBaseline.Write(second, "synthetic-release", 17, "synthetic-build", new string('a', 64),
            new[] { Lua, Asset, Proto }.Select(rel => new VerifiedBaseline.Entry(new FileInfo(Path.Combine(second, rel)).Length, Hash(Path.Combine(second, rel)), rel)));
        Assert.Equal(Hash(Path.Combine(Client, VerifiedBaseline.RelativePath)), Hash(Path.Combine(second, VerifiedBaseline.RelativePath)));
        int exports = 0; Flows.EnsureGameData(second, false, Data, (_, _) => exports++);
        Assert.Equal(0, exports);
    }

    [Fact]
    public void PublicPackagingEntryWritesOnlyThePortableSidecar()
    {
        var before = Snapshot(); var manifest = PreparedGameData.WriteManifest(Data, Client);
        Assert.Equal(1, manifest.Format); Assert.True(File.Exists(Marker));
        foreach (var (rel, hash) in before) Assert.Equal(hash, Hash(Path.Combine(Data, rel)));
        Assert.True(PreparedGameData.UseIfPresent(Data, Client, false));
    }

    [Theory]
    [InlineData("data-content")]
    [InlineData("data-missing")]
    [InlineData("data-added")]
    [InlineData("source-content")]
    [InlineData("source-added")]
    [InlineData("baseline-version")]
    [InlineData("manifest-version")]
    [InlineData("manifest-source")]
    [InlineData("manifest-hash")]
    [InlineData("manifest-path")]
    [InlineData("manifest-case-duplicate")]
    [InlineData("malformed")]
    [InlineData("missing-baseline")]
    public void AnyMismatchFailsClosedWithoutExportOrOverwrite(string fault)
    {
        var manifest = Prepare();
        switch (fault)
        {
            case "data-content": Write(Data, "config/MapResource.json", "changed"); break;
            case "data-missing": File.Delete(Path.Combine(Data, "config/MapResource.json")); break;
            case "data-added": Write(Data, "config/extra.json", "extra"); break;
            case "source-content": Write(Client, Lua, "changed source"); break;
            case "source-added": Write(Client, "ro3_Data/StreamingAssets/Recovery/LuaPayload/extra.lua.bytes", "extra source"); break;
            case "baseline-version":
                string baseline = Path.Combine(Client, VerifiedBaseline.RelativePath);
                File.WriteAllText(baseline, File.ReadAllText(baseline).Replace("release\t17\t", "release\t18\t", StringComparison.Ordinal)); break;
            case "manifest-version": Save(manifest with { Format = 2 }); break;
            case "manifest-source": Save(manifest with { Client = manifest.Client with { Sequence = 18 } }); break;
            case "manifest-hash": manifest.Files["config/MapResource.json"] = new(1, new string('b', 64)); Save(manifest); break;
            case "manifest-path": manifest.Files["../outside"] = new(1, new string('b', 64)); Save(manifest); break;
            case "manifest-case-duplicate": manifest.Files["CONFIG/MapResource.json"] = manifest.Files["config/MapResource.json"]; Save(manifest); break;
            case "malformed": File.WriteAllText(Marker, "{not-json"); break;
            case "missing-baseline": File.Delete(Path.Combine(Client, VerifiedBaseline.RelativePath)); break;
        }
        var before = Snapshot(); int exports = 0;
        Assert.Throws<SetupException>(() => Flows.EnsureGameData(Client, false, Data, (_, _) => exports++));
        Assert.Equal(0, exports); Assert.Equal(before.OrderBy(p => p.Key), Snapshot().OrderBy(p => p.Key));
    }

    [Fact]
    public void ReexportCannotSilentlyReplaceAnImportedGeneration()
    {
        Prepare(); int exports = 0; var before = Snapshot();
        Assert.Throws<SetupException>(() => Flows.EnsureGameData(Client, true, Data, (_, _) => exports++));
        Assert.Equal(0, exports); Assert.Equal(before.OrderBy(p => p.Key), Snapshot().OrderBy(p => p.Key));
    }

    [Fact]
    public void NoMarkerKeepsTheOriginalMissingOrOldStampExportPath()
    {
        int exports = 0;
        Flows.EnsureGameData(Client, false, Data, (source, target) =>
        { Assert.Equal(Client, source); Assert.Equal(Data, target); exports++; });
        Assert.Equal(1, exports);
    }

    [Fact]
    public void NoMarkerKeepsTheOriginalCompleteNativeStampPath()
    {
        var counts = GameDataExport.Folders.ToDictionary(folder => folder,
            folder => Directory.EnumerateFiles(Path.Combine(Data, folder), "*", SearchOption.AllDirectories).Count());
        var stamp = new GameDataExport.Stamp(GameDataExport.FormatVersion, GameDataExport.Fingerprint(Client), Client,
            DateTime.UtcNow, 100, 1, 1, GameDataExport.QuickFingerprint(Client), counts, []);
        File.WriteAllText(Path.Combine(Data, "export.json"), JsonSerializer.Serialize(stamp));
        int exports = 0;
        Flows.EnsureGameData(Client, false, Data, (_, _) => exports++);
        Assert.Equal(0, exports);
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
