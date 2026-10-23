using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's RefiningSeasonAttrConfig (data/config/RefiningSeasonAttrConfig.json).</summary>
[ConfigTable("RefiningSeasonAttrConfig")]
public readonly partial record struct RefiningSeasonAttrConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_kAttrs")] public partial long[][] Attrs { get; }
}
