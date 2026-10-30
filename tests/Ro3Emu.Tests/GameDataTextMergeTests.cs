using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Tests;

public sealed class GameDataTextMergeTests
{
    [Fact]
    public void OfficialRawFieldLookupAndClientLanguageKeepTheirOwnResolvers()
    {
        const long textId = 990098765;
        var data = new GameData(Options.Create(new ServerOptions
        { GameDataDir = SyntheticData.Dir, ClientTextLanguage = "zh_TW" }), NullLogger<GameData>.Instance);
        ((IDictionary<string, JsonElement>)data.Table("Localization_en"))[textId.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
            JsonSerializer.SerializeToElement(new { _iID = textId, _kDes = "Invented English @{1}" });
        ((IDictionary<string, JsonElement>)data.Table("Localization_zh_TW"))[textId.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
            JsonSerializer.SerializeToElement(new { _iID = textId, _kDes = "自造繁中 @{1}" });
        var row = JsonSerializer.SerializeToElement(new { name = new object[] { textId, "probe" } });

        Assert.Equal("Invented English probe", data.TextOf(row, "name"));
        Assert.Equal("Invented English probe", data.TextOf(row.GetProperty("name")));
        Assert.Equal("自造繁中 probe", data.ClientTextOf(row, "name"));
        Assert.Equal("自造繁中 7", data.ClientTextOf(new long[] { textId, 7 }));
        Assert.Equal("", data.TextOf((JsonElement?)null));
        Assert.Equal("", data.TextOf(row, "missing"));
        Assert.Equal("", data.ClientTextOf(row, "missing"));
    }
}
