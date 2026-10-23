using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PetIncubationResultConfig (data/config/PetIncubationResultConfig.json).</summary>
[ConfigTable("PetIncubationResultConfig")]
public readonly partial record struct PetIncubationResultConfig(JsonElement Row)
{
    [ConfigField("_iExp")] public partial long Exp { get; }
}
