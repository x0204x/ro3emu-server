using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TeamTargetConfig (data/config/TeamTargetConfig.json).</summary>
[ConfigTable("TeamTargetConfig")]
public readonly partial record struct TeamTargetConfig(JsonElement Row)
{
    [ConfigField("_iIsRaidTarget")] public partial long IsRaidTarget { get; }
}
