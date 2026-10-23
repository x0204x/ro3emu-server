using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AppearanceEtchConfig (data/config/AppearanceEtchConfig.json).</summary>
[ConfigTable("AppearanceEtchConfig")]
public readonly partial record struct AppearanceEtchConfig(JsonElement Row)
{
    [ConfigField("_iAppearanceID")] public partial long AppearanceId { get; }
    [ConfigField("_iDefault")] public partial long Default { get; }
    [ConfigField("_iFashionScore")] public partial long FashionScore { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iPart")] public partial long Part { get; }
    [ConfigField("_iWeight")] public partial long Weight { get; }
}
