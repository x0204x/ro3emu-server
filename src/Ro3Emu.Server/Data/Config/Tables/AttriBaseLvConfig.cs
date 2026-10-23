using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AttriBaseLvConfig (data/config/AttriBaseLvConfig.json).</summary>
[ConfigTable("AttriBaseLvConfig")]
public readonly partial record struct AttriBaseLvConfig(JsonElement Row)
{
    [ConfigField("_kAttri")] public partial long[][] Attri { get; }
}
