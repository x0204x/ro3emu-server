using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's HeroicSpiritLayerConfig (data/config/HeroicSpiritLayerConfig.json).</summary>
[ConfigTable("HeroicSpiritLayerConfig")]
public readonly partial record struct HeroicSpiritLayerConfig(JsonElement Row)
{
    [ConfigField("_iFloor")] public partial long Floor { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iLevelLimit")] public partial long LevelLimit { get; }
    [ConfigField("_iPreLayer")] public partial long PreLayer { get; }
}
