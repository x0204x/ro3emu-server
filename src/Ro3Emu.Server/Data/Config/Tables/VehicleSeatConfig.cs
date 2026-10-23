using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's VehicleSeatConfig (data/config/VehicleSeatConfig.json).</summary>
[ConfigTable("VehicleSeatConfig")]
public readonly partial record struct VehicleSeatConfig(JsonElement Row)
{
    [ConfigField("_iChange3C")] public partial long Change3C { get; }
    [ConfigField("_kGameplayBtnId")] public partial long[] GameplayBtnId { get; }
}
