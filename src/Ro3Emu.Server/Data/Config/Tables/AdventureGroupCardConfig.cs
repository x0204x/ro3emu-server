using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AdventureGroupCardConfig (data/config/AdventureGroupCardConfig.json).</summary>
[ConfigTable("AdventureGroupCardConfig")]
public readonly partial record struct AdventureGroupCardConfig(JsonElement Row)
{
    [ConfigField("_iDefaultUnlock")] public partial long DefaultUnlock { get; }
}
