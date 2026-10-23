using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's DecomposeConditionConfig (data/config/DecomposeConditionConfig.json).</summary>
[ConfigTable("DecomposeConditionConfig")]
public readonly partial record struct DecomposeConditionConfig(JsonElement Row)
{
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kParam")] public partial long[] Param { get; }
}
