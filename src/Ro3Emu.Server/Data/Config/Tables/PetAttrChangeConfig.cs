using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PetAttrChangeConfig (data/config/PetAttrChangeConfig.json).</summary>
[ConfigTable("PetAttrChangeConfig")]
public readonly partial record struct PetAttrChangeConfig(JsonElement Row)
{
    [ConfigField("_iQuality")] public partial long Quality { get; }
    [ConfigField("_iStage")] public partial long Stage { get; }
    [ConfigField("_iStar")] public partial long Star { get; }
    [ConfigField("_kAttrPerc")] public partial long[][] AttrPerc { get; }
}
