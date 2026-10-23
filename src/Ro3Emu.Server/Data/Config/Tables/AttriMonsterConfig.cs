using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AttriMonsterConfig (data/config/AttriMonsterConfig.json).</summary>
[ConfigTable("AttriMonsterConfig")]
public readonly partial record struct AttriMonsterConfig(JsonElement Row)
{
    [ConfigField("_iLevel")] public partial long Level { get; }
    [ConfigField("_kAttri")] public partial long[][] Attri { get; }
}
