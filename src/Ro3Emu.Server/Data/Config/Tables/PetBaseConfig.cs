using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PetBaseConfig (data/config/PetBaseConfig.json).</summary>
[ConfigTable("PetBaseConfig")]
public readonly partial record struct PetBaseConfig(JsonElement Row)
{
    [ConfigField("_iActiveAward")] public partial long ActiveAward { get; }
    [ConfigField("_iCamp")] public partial long Camp { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iMonster")] public partial long Monster { get; }
    [ConfigField("_iQuality")] public partial long Quality { get; }
    [ConfigField("_kDecompose")] public partial long[] Decompose { get; }
    [ConfigField("_kMultiverseArray")] public partial JsonElement? MultiverseArray { get; }
}
