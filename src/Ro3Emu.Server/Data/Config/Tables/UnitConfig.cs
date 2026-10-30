using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's UnitConfig (data/config/UnitConfig.json): the unit an NPC is shown as.</summary>
[ConfigTable("UnitConfig")]
public readonly partial record struct UnitConfig(JsonElement Row)
{
    /// <summary>Walking speed in cm/s.</summary>
    [ConfigField("_fSpeed")] public partial double Speed { get; }
}
