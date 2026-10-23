using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's BecomeStrongerConfig (data/config/BecomeStrongerConfig.json).</summary>
[ConfigTable("BecomeStrongerConfig")]
public readonly partial record struct BecomeStrongerConfig(JsonElement Row)
{
    [ConfigField("_iFunction")] public partial long Function { get; }
}
