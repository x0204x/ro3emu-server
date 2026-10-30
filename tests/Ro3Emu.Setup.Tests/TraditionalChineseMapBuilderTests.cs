using System.Text.Json;
using Ro3Emu.DataExport;
using Ro3Emu.ClientTools;

namespace Ro3Emu.Setup.Tests;

public sealed class TraditionalChineseMapBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3-zh-tw-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SameEnglishKeepsDistinctSidTranslationsAndReportsAmbiguity()
    {
        string simplified = Table("cn", new() { [1] = "次級", [2] = "下一頁", [3] = "倉庫" });
        string english = Table("en", new() { [1] = "Next", [2] = "Next", [3] = "Storage" });
        string traditional = Table("tw", new() { [1] = "下一階", [2] = "下一頁" });
        var result = TraditionalChineseMapBuilder.Build(simplified, english, traditional, Path.Combine(_root, "pack"));
        string sids = File.ReadAllText(Path.Combine(_root, "pack", TraditionalChineseMapBuilder.SidFile));
        Assert.Contains("1\t下一階\n", sids);
        Assert.Contains("2\t下一頁\n", sids);
        Assert.Equal(["3"], result.MissingTraditionalIds);
        Assert.Equal(1, result.AmbiguousTexts);
    }

    [Fact]
    public void RejectsUnsafeFormatPairAndPreservesEscapedData()
    {
        string simplified = Table("cn", new() { [1] = "\u57ce\u5e02 ${1}", [2] = "\t說明\n第\\二行" });
        string english = Table("en", new() { [1] = "City ${1}", [2] = "\tNotice\nLine\\two" });
        string traditional = Table("tw", new() { [1] = "城市 ${2}", [2] = "\t說明\n第\\二行" });
        string pack = Path.Combine(_root, "pack");
        var result = TraditionalChineseMapBuilder.Build(simplified, english, traditional, pack);
        Assert.Contains("1", result.PlaceholderMismatches);
        string text = File.ReadAllText(Path.Combine(pack, TraditionalChineseMapBuilder.TextFile));
        Assert.DoesNotContain("City ${1}\t", text);
        Assert.Contains("\\tNotice\\nLine\\\\two\t\\t說明\\n第\\\\二行", text);
    }

    [Fact]
    public void MissingNativeTextCanUseExplicitSameSidFallbackWithoutReplacingKnownText()
    {
        string simplified = Table("cn", new() { [1] = "姓名", [2] = "倉庫" });
        string english = Table("en", new() { [1] = "Name", [2] = "Storage" });
        string traditional = Table("tw", new() { [1] = "角色名稱", [2] = "2" });
        string pack = Path.Combine(_root, "pack");
        var result = TraditionalChineseMapBuilder.Build(simplified, english, traditional, pack,
            ["sid\t2\t倉庫\nfallback-text\t角色名稱\tIncorrect\nfallback-text\tStorage\t倉庫\n"]);
        Assert.Equal(2, result.TranslatedIds);
        Assert.Empty(result.UnresolvedIds);
        string text = File.ReadAllText(Path.Combine(pack, TraditionalChineseMapBuilder.TextFile));
        Assert.Contains("角色名稱\t角色名稱", text);
        Assert.Contains("Storage\t倉庫", text);
        Assert.Throws<InvalidDataException>(() => TraditionalChineseMapBuilder.Build(simplified, english, traditional,
            pack, ["sid\t999999\t未知\n"]));
    }

    [Fact]
    public void WholeProfileIsRequiredForReleaseAndOverridesAreScoped()
    {
        string simplified = Table("cn", new() { [1] = "同字" });
        string english = Table("en", new() { [1] = "Same word" });
        string traditional = Table("tw", new() { [1] = "同字" });
        string pack = Path.Combine(_root, "pack");
        TraditionalChineseMapBuilder.Build(simplified, english, traditional, pack, ["sid\t1\t明確介面用語\n"]);
        Assert.Equal(2, ReleaseFileSources.TraditionalChinesePack(_root, pack).Count);
        Assert.Contains("1\t明確介面用語", File.ReadAllText(Path.Combine(pack, TraditionalChineseMapBuilder.SidFile)));
        File.Delete(Path.Combine(pack, TraditionalChineseMapBuilder.SidFile));
        Assert.Throws<FileNotFoundException>(() => ReleaseFileSources.TraditionalChinesePack(_root, pack));
    }

    private string Table(string name, Dictionary<long, string> values)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(values.ToDictionary(p => p.Key, p => new { _kDes = p.Value })));
        return path;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
