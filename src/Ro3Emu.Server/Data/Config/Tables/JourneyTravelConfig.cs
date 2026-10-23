using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's JourneyTravelConfig (data/config/JourneyTravelConfig.json).</summary>
[ConfigTable("JourneyTravelConfig")]
public readonly partial record struct JourneyTravelConfig(JsonElement Row)
{
    [ConfigField("_iGroup")] public partial long Group { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iTime")] public partial long Time { get; }
    [ConfigField("_kEventGroup")] public partial long[] EventGroup { get; }
}
