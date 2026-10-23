using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's DecomposeConfig (data/config/DecomposeConfig.json).</summary>
[ConfigTable("DecomposeConfig")]
public readonly partial record struct DecomposeConfig(JsonElement Row)
{
    [ConfigField("_iSubType")] public partial long[][] SubType { get; }
    [ConfigField("_iType")] public partial long[] Type { get; }
    [ConfigField("_kAutomatic")] public partial long[] Automatic { get; }
}
