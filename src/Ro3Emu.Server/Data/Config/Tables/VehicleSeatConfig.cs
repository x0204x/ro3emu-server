using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's VehicleSeatConfig (data/config/VehicleSeatConfig.json).</summary>
[ConfigTable("VehicleSeatConfig")]
public readonly partial record struct VehicleSeatConfig(JsonElement Row)
{
    [ConfigField("_iChange3C")] public partial long Change3C { get; }
    [ConfigField("_iExitNpcAction")] public partial long ExitNpcAction { get; }
    /// <summary>enum_PassengerType: 0 Driver, 1 Killer (shooter), 2 Seater.</summary>
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kGameplayBtnId")] public partial long[] GameplayBtnId { get; }
    [ConfigField("_kMainSkills")] public partial long[] MainSkills { get; }
    [ConfigField("_kSkills")] public partial long[] Skills { get; }
}
