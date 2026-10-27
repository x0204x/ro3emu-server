using Ro3Emu.ClientTools;

namespace Ro3Emu.Setup.Tests;

public sealed class ReleaseFileSourcesTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-release-builder-" + Guid.NewGuid().ToString("N"));

    public ReleaseFileSourcesTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void UsesTheClientsLocalizationMapWhenPresent()
    {
        string map = Path.Combine(_dir, "ro3emu", "loc_en.tsv");
        Directory.CreateDirectory(Path.GetDirectoryName(map)!);
        File.WriteAllText(map, "test");

        var file = ReleaseFileSources.LocalizationMap(_dir, null);

        Assert.Equal("ro3emu/loc_en.tsv", file?.Key);
        Assert.Equal(Path.GetFullPath(map), file?.Value);
    }

    [Fact]
    public void ExplicitLocalizationMapCanLiveOutsideTheClient()
    {
        string map = Path.Combine(_dir, "zh-TW.tsv");
        File.WriteAllText(map, "test");

        var file = ReleaseFileSources.LocalizationMap(Path.Combine(_dir, "client"), map);

        Assert.Equal("ro3emu/loc_en.tsv", file?.Key);
        Assert.Equal(Path.GetFullPath(map), file?.Value);
    }

    [Fact]
    public void MissingImplicitMapIsOptional()
    {
        Assert.Null(ReleaseFileSources.LocalizationMap(_dir, null));
    }

    [Fact]
    public void MissingExplicitMapIsAnError()
    {
        string map = Path.Combine(_dir, "missing.tsv");

        var error = Assert.Throws<FileNotFoundException>(() => ReleaseFileSources.LocalizationMap(_dir, map));

        Assert.Equal(Path.GetFullPath(map), error.FileName);
    }

    [Fact]
    public void SkillTraceMarkerIsCreatedOnlyWhenRequested()
    {
        Assert.Null(ReleaseFileSources.SkillTraceMarker(_dir, false));

        var file = ReleaseFileSources.SkillTraceMarker(_dir, true);

        Assert.Equal("ro3emu/skilltrace.on", file?.Key);
        Assert.Equal(Path.Combine(_dir, "ro3emu", "skilltrace.on"), file?.Value);
        Assert.Empty(File.ReadAllBytes(file!.Value.Value));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
