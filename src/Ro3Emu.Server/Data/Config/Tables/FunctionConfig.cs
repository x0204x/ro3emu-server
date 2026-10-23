using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's FunctionConfig (data/config/FunctionConfig.json).</summary>
[ConfigTable("FunctionConfig")]
public readonly partial record struct FunctionConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_kOpenCondition")] public partial long OpenCondition { get; }
}
