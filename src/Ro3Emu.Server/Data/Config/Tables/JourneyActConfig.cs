using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's JourneyActConfig (data/config/JourneyActConfig.json).</summary>
[ConfigTable("JourneyActConfig")]
public readonly partial record struct JourneyActConfig(JsonElement Row)
{
    [ConfigField("_iAction")] public partial JsonElement? Action { get; }
    [ConfigField("_iCondition")] public partial long Condition { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iNode")] public partial long Node { get; }
    [ConfigField("_iResultAction")] public partial JsonElement? ResultAction { get; }
    [ConfigField("_kNext")] public partial long[] Next { get; }
}
