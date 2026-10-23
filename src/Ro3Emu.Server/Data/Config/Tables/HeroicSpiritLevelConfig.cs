using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's HeroicSpiritLevelConfig (data/config/HeroicSpiritLevelConfig.json).</summary>
[ConfigTable("HeroicSpiritLevelConfig")]
public readonly partial record struct HeroicSpiritLevelConfig(JsonElement Row)
{
    [ConfigField("_iExp")] public partial long Exp { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
}
