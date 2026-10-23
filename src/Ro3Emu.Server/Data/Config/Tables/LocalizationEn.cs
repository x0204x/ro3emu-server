using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's Localization_en (data/config/Localization_en.json).</summary>
[ConfigTable("Localization_en")]
public readonly partial record struct LocalizationEn(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_kDes")] public partial string Des { get; }
}
