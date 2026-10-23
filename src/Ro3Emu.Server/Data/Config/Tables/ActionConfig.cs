using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's ActionConfig (data/config/ActionConfig.json).</summary>
[ConfigTable("ActionConfig")]
public readonly partial record struct ActionConfig(JsonElement Row)
{
    [ConfigField("_iEXECHost")] public partial long EXECHost { get; }
    [ConfigField("_iFunctionName")] public partial string FunctionName { get; }
}
