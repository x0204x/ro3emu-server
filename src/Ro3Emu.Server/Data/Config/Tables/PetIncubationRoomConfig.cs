using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PetIncubationRoomConfig (data/config/PetIncubationRoomConfig.json).</summary>
[ConfigTable("PetIncubationRoomConfig")]
public readonly partial record struct PetIncubationRoomConfig(JsonElement Row)
{
    [ConfigField("_iExp")] public partial long Exp { get; }
    [ConfigField("_iIncubateTime")] public partial long IncubateTime { get; }
    [ConfigField("_iLv")] public partial long Lv { get; }
    [ConfigField("_iUnlockSlot")] public partial long[] UnlockSlot { get; }
    [ConfigField("_kCost")] public partial long[][] Cost { get; }
}
