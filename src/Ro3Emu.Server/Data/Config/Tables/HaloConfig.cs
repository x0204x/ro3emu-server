using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's HaloConfig (data/config/HaloConfig.json).</summary>
[ConfigTable("HaloConfig")]
public readonly partial record struct HaloConfig(JsonElement Row)
{
    [ConfigField("_iExtraTarget")] public partial long[] ExtraTarget { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iRadius")] public partial long Radius { get; }
    [ConfigField("_iTargetMax")] public partial long TargetMax { get; }
    [ConfigField("_iTargetType")] public partial long TargetType { get; }
    [ConfigField("_iTickTime")] public partial long TickTime { get; }
}
