using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EnergyConfig (data/config/EnergyConfig.json).</summary>
[ConfigTable("EnergyConfig")]
public readonly partial record struct EnergyConfig(JsonElement Row)
{
    [ConfigField("_iAutoRecoverInterval")] public partial long AutoRecoverInterval { get; }
    [ConfigField("_iAutoRecoverValue")] public partial long AutoRecoverValue { get; }
    [ConfigField("_iBelongJob")] public partial long[] BelongJob { get; }
    [ConfigField("_iBelongMount")] public partial long BelongMount { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iInitializeValue")] public partial long InitializeValue { get; }
    [ConfigField("_iLimitValue")] public partial long LimitValue { get; }
}
