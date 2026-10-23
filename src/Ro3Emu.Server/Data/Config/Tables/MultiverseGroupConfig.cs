using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's MultiverseGroupConfig (data/config/MultiverseGroupConfig.json).</summary>
[ConfigTable("MultiverseGroupConfig")]
public readonly partial record struct MultiverseGroupConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iValue_101")] public partial long Value101 { get; }
    [ConfigField("_iValue_default")] public partial long ValueDefault { get; }
    [ConfigField("_itype")] public partial long Type { get; }
}
