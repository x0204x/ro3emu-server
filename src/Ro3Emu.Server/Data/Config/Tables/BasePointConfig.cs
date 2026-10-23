using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's BasePointConfig (data/config/BasePointConfig.json).</summary>
[ConfigTable("BasePointConfig")]
public readonly partial record struct BasePointConfig(JsonElement Row)
{
    [ConfigField("_iStatusPoint")] public partial long StatusPoint { get; }
}
