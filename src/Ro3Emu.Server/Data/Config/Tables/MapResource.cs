using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's MapResource (data/config/MapResource.json).</summary>
[ConfigTable("MapResource")]
public readonly partial record struct MapResource(JsonElement Row)
{
    [ConfigField("_kNavDataName")] public partial string NavDataName { get; }
}
