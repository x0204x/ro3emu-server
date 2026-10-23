using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's LifeGameConfig (data/config/LifeGameConfig.json).</summary>
[ConfigTable("LifeGameConfig")]
public readonly partial record struct LifeGameConfig(JsonElement Row)
{
    [ConfigField("_iDuration")] public partial long Duration { get; }
}
