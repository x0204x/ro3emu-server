using System.Text.Json;
using Ro3Emu.ClientTools;
using Ro3Emu.DataExport;

namespace Ro3Emu.Setup.Tests;

/// <summary>以自造 JSON／非可執行 bytes 驗證玩家生成與配對交付；不啟動遊戲或 Lua VM。</summary>
public sealed class PlayerLocalizationFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3-player-loc-" + Guid.NewGuid().ToString("N"));
    private string Client => Path.Combine(_root, "client");
    private string Cache => Path.Combine(_root, "localization");

    [Fact]
    public void TheSameWriterKeepsEnglishCountAndNativeSidPriority()
    {
        var tables = Tables();
        int english = GameDataExport.WriteLocMapsFromTables(tables.Cn, tables.En, tables.Tw,
            Path.Combine(Cache, "loc_en.tsv"), new GameDataExport.Options
            {
                TraditionalChineseOverrides = ["sid\t1\t不得蓋過原生\nsid\t2\t補齊文字\nsid\t999\t其他世代\n"],
            });
        Assert.Equal(2, english);
        string sid = File.ReadAllText(Path.Combine(Cache, TraditionalChineseMapBuilder.SidFile));
        Assert.Contains("1\t原生名稱\n", sid);
        Assert.Contains("2\t補齊文字\n", sid);
        Assert.DoesNotContain("不得蓋過原生", sid);
        Assert.DoesNotContain("999\t", sid);
        Assert.Equal(TraditionalChineseMapBuilder.Header,
            File.ReadLines(Path.Combine(Cache, TraditionalChineseMapBuilder.TextFile)).First());
    }

    [Fact]
    public void AChangedThirdSourceOrMissingPairRebuildsWithoutTouchingCuratedServerData()
    {
        NativeSources();
        var tables = Tables();
        string serverMap = Path.Combine(_root, "data", "loc_en.tsv");
        Write(serverMap, "curated server index");
        int builds = 0;
        int Writer(string _, string map, GameDataExport.Options options)
        {
            builds++;
            Assert.Single(options.TraditionalChineseOverrides);
            return GameDataExport.WriteLocMapsFromTables(tables.Cn, tables.En, tables.Tw, map, options);
        }
        Flows.EnsureLocMap(Client, Cache, Writer);
        Flows.EnsureLocMap(Client, Cache, Writer);
        Assert.Equal(1, builds);
        string native = GameDataExport.LocSourceFiles(Client)[2];
        DateTime oldTime = File.GetLastWriteTimeUtc(native);
        File.AppendAllText(native, "new source");
        File.SetLastWriteTimeUtc(native, oldTime); // 不可只靠 mtime 誤認舊配對有效。
        Flows.EnsureLocMap(Client, Cache, Writer);
        Assert.Equal(2, builds);
        File.Delete(Path.Combine(Cache, TraditionalChineseMapBuilder.SidFile));
        Flows.EnsureLocMap(Client, Cache, Writer);
        Assert.Equal(3, builds);
        File.AppendAllText(Path.Combine(Cache, TraditionalChineseMapBuilder.TextFile), "damaged\n");
        Flows.EnsureLocMap(Client, Cache, Writer);
        Assert.Equal(4, builds);
        Assert.Equal("curated server index", File.ReadAllText(serverMap));
    }

    [Fact]
    public void AnIncompleteWriterCannotMarkThePackFresh()
    {
        NativeSources();
        int Writer(string _, string english, GameDataExport.Options options)
        {
            Write(english, ClientPatcher.GeneratedLocHeader + " synthetic\n");
            Write(Path.Combine(Cache, TraditionalChineseMapBuilder.TextFile), TraditionalChineseMapBuilder.Header + "\n");
            return 1;
        }
        Assert.Throws<SetupException>(() => Flows.EnsureLocMap(Client, Cache, Writer));
        Assert.False(File.Exists(Path.Combine(Cache, "locmap.sources.json")));
        Assert.False(Directory.Exists(Path.Combine(Client, "ro3emu")));
    }

    [Fact]
    public void CompleteCustomClientMapsArePreservedAsOnePair()
    {
        GeneratedPack(Cache, "new");
        string destination = Path.Combine(Client, "ro3emu");
        Write(Path.Combine(destination, "loc_en.tsv"), "my English map");
        Write(Path.Combine(destination, TraditionalChineseMapBuilder.TextFile), "my Traditional Chinese map");
        Write(Path.Combine(destination, TraditionalChineseMapBuilder.SidFile), "my SID map");
        Flows.CopyLocalizationMaps(Cache, Client);
        Assert.Equal("my English map", File.ReadAllText(Path.Combine(destination, "loc_en.tsv")));
        Assert.Equal("my Traditional Chinese map", File.ReadAllText(Path.Combine(destination, TraditionalChineseMapBuilder.TextFile)));
        Assert.Equal("my SID map", File.ReadAllText(Path.Combine(destination, TraditionalChineseMapBuilder.SidFile)));
    }

    [Theory]
    [InlineData(TraditionalChineseMapBuilder.TextFile)]
    [InlineData(TraditionalChineseMapBuilder.SidFile)]
    public void AHalfCustomPairFailsBeforeChangingAnyClientMap(string existing)
    {
        GeneratedPack(Cache, "new");
        string directory = Path.Combine(Client, "ro3emu");
        Write(Path.Combine(directory, "loc_en.tsv"), "keep English");
        Write(Path.Combine(directory, existing), "keep custom");
        Assert.Throws<SetupException>(() => Flows.CopyLocalizationMaps(Cache, Client));
        Assert.Equal("keep custom", File.ReadAllText(Path.Combine(directory, existing)));
        Assert.Equal("keep English", File.ReadAllText(Path.Combine(directory, "loc_en.tsv")));
        Assert.Equal(2, Directory.GetFiles(directory).Length);
    }

    [Fact]
    public void AHalfGeneratedPairCanBeSafelyReplacedByTheCompleteNewPair()
    {
        GeneratedPack(Cache, "new");
        string target = Path.Combine(Client, "ro3emu");
        Write(Path.Combine(target, TraditionalChineseMapBuilder.TextFile), TraditionalChineseMapBuilder.Header + "\nold\n");
        Flows.CopyLocalizationMaps(Cache, Client);
        foreach (string name in new[] { TraditionalChineseMapBuilder.TextFile, TraditionalChineseMapBuilder.SidFile })
            Assert.Equal(File.ReadAllBytes(Path.Combine(Cache, name)), File.ReadAllBytes(Path.Combine(target, name)));
    }

    [Fact]
    public void AFailedSecondFilePublicationRestoresThePreviousGeneratedPair()
    {
        GeneratedPack(Cache, "new");
        string target = Path.Combine(Client, "ro3emu");
        GeneratedPack(target, "old");
        var before = new[] { TraditionalChineseMapBuilder.TextFile, TraditionalChineseMapBuilder.SidFile }
            .ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(target, name)));
        Assert.Throws<IOException>(() => Flows.CopyLocalizationMaps(Cache, Client,
            count => { if (count == 1) throw new IOException("自造配對發布故障"); }));
        foreach (var (name, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(target, name)));
        Assert.Empty(Directory.GetDirectories(target, ".locpair-*"));
    }

    private void NativeSources()
    {
        foreach (string source in GameDataExport.LocSourceFiles(Client))
            Write(source, "not executable Lua: " + Path.GetFileName(source));
        Assert.Equal(3, GameDataExport.LocSourceFiles(Client).Length);
    }

    private (string Cn, string En, string Tw) Tables()
    {
        string Table(string name, Dictionary<int, string> values)
        {
            string path = Path.Combine(_root, name + ".json");
            Write(path, JsonSerializer.Serialize(values.ToDictionary(p => p.Key, p => new { _kDes = p.Value })));
            return path;
        }
        return (Table("cn", new() { [1] = "原名", [2] = "缺字" }),
            Table("en", new() { [1] = "Native name", [2] = "Missing" }),
            Table("tw", new() { [1] = "原生名稱", [2] = "2" }));
    }

    private static void GeneratedPack(string directory, string value)
    {
        Write(Path.Combine(directory, "loc_en.tsv"), ClientPatcher.GeneratedLocHeader + " synthetic\n" + value);
        foreach (string name in new[] { TraditionalChineseMapBuilder.TextFile, TraditionalChineseMapBuilder.SidFile })
            Write(Path.Combine(directory, name), TraditionalChineseMapBuilder.Header + "\n" + value);
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
