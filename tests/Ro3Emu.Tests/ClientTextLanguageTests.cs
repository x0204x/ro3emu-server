using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Tests;

public sealed class ClientTextLanguageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3-client-language-" + Guid.NewGuid().ToString("N"));

    public ClientTextLanguageTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        Table("en", "Map merchant", "Guard", "Help");
        Table("zh_CN", "地图商贩", "卫兵", "帮助");
        Table("zh_TW", "地圖商人", "2", "None");
    }

    [Theory]
    [InlineData("zh_TW", "地圖商人", "卫兵", "帮助")]
    [InlineData("zh_CN", "地图商贩", "卫兵", "帮助")]
    [InlineData("en", "Map merchant", "Guard", "Help")]
    public void ConfiguredDisplayLanguageUsesNativeTablesWithoutChangingRuleText(string language, string one, string two, string three)
    {
        var data = new GameData(Options.Create(new ServerOptions { GameDataDir = _root, ClientTextLanguage = language }), NullLogger<GameData>.Instance);
        Assert.Equal(one, data.ClientText(1));
        Assert.Equal(two, data.ClientText(2));
        Assert.Equal(three, data.ClientText(3));
        Assert.Equal("Map merchant", data.Text(1));
    }

    [Fact]
    public void UnknownLanguageIsRejectedBeforeLoadingData() =>
        Assert.Throws<ArgumentException>(() => new GameData(Options.Create(new ServerOptions
        { GameDataDir = _root, ClientTextLanguage = "../../another-table" }), NullLogger<GameData>.Instance));

    private void Table(string language, params string[] text) => File.WriteAllText(Path.Combine(_root, "config", "Localization_" + language + ".json"),
        JsonSerializer.Serialize(text.Select((value, index) => new { Id = index + 1, Value = new { _iID = index + 1, _kDes = value } }).ToDictionary(p => p.Id, p => p.Value)));

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
