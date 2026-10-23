using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GuideConfig (data/config/GuideConfig.json).</summary>
[ConfigTable("GuideConfig")]
public readonly partial record struct GuideConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iType")] public partial long Type { get; }
}
