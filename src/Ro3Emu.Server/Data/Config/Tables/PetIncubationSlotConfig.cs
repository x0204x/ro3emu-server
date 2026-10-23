using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PetIncubationSlotConfig (data/config/PetIncubationSlotConfig.json).</summary>
[ConfigTable("PetIncubationSlotConfig")]
public readonly partial record struct PetIncubationSlotConfig(JsonElement Row)
{
    [ConfigField("_iActivity")] public partial long Activity { get; }
    [ConfigField("_iSlotType")] public partial long SlotType { get; }
    [ConfigField("_kDrawIds")] public partial long[] DrawIds { get; }
    [ConfigField("_kMaterial")] public partial long[][] Material { get; }
}
