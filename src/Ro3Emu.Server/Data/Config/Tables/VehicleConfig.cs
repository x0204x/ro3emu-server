using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's VehicleConfig (data/config/VehicleConfig.json).</summary>
[ConfigTable("VehicleConfig")]
public readonly partial record struct VehicleConfig(JsonElement Row)
{
    [ConfigField("_kSeats")] public partial long[] Seats { get; }
}
