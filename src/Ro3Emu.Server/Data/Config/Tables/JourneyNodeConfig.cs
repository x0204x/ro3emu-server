using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's JourneyNodeConfig (data/config/JourneyNodeConfig.json).</summary>
[ConfigTable("JourneyNodeConfig")]
public readonly partial record struct JourneyNodeConfig(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iChapter")] public partial long Chapter { get; }
    [ConfigField("_iFirstAct")] public partial long FirstAct { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iNext")] public partial long Next { get; }
}
