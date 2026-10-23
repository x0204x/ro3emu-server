using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's ActivityConfig (data/config/ActivityConfig.json).</summary>
[ConfigTable("ActivityConfig")]
public readonly partial record struct ActivityConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_kJoinEndTime")] public partial long[] JoinEndTime { get; }
    [ConfigField("_kJoinStartTime")] public partial long[] JoinStartTime { get; }
}
