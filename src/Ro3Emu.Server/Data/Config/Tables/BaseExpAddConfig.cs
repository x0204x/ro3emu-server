using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's BaseExpAddConfig (data/config/BaseExpAddConfig.json).</summary>
[ConfigTable("BaseExpAddConfig")]
public readonly partial record struct BaseExpAddConfig(JsonElement Row)
{
    [ConfigField("_iBaseLvGap")] public partial long BaseLvGap { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iPursueExpAdditionPercent")] public partial long PursueExpAdditionPercent { get; }
}
