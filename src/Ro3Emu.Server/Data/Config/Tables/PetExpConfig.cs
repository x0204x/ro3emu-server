using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PetExpConfig (data/config/PetExpConfig.json).</summary>
[ConfigTable("PetExpConfig")]
public readonly partial record struct PetExpConfig(JsonElement Row)
{
    [ConfigField("_iExp")] public partial long Exp { get; }
    [ConfigField("_iLv")] public partial long Lv { get; }
    [ConfigField("_iLvAssistStrength")] public partial long LvAssistStrength { get; }
    [ConfigField("_iLvFightStrength")] public partial long LvFightStrength { get; }
    [ConfigField("_iQuality")] public partial long Quality { get; }
}
