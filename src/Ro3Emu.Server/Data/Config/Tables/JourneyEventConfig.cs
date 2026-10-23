using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's JourneyEventConfig (data/config/JourneyEventConfig.json).</summary>
[ConfigTable("JourneyEventConfig")]
public readonly partial record struct JourneyEventConfig(JsonElement Row)
{
    [ConfigField("_iCondition")] public partial long Condition { get; }
    [ConfigField("_iGroup")] public partial long Group { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iResultAction")] public partial JsonElement? ResultAction { get; }
    [ConfigField("_iWeight")] public partial long Weight { get; }
    [ConfigField("_kBtn")] public partial long[] Btn { get; }
    [ConfigField("_kEnterAction")] public partial JsonElement? EnterAction { get; }
}
