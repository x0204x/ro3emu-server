using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's MultiverseConfig (data/config/MultiverseConfig.json).</summary>
[ConfigTable("MultiverseConfig")]
public readonly partial record struct MultiverseConfig(JsonElement Row)
{
    [ConfigField("_iDanSystem")] public partial long[] DanSystem { get; }
    [ConfigField("_iID")] public partial long Id { get; }
}
