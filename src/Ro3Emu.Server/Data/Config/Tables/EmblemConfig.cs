using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EmblemConfig (data/config/EmblemConfig.json).</summary>
[ConfigTable("EmblemConfig")]
public readonly partial record struct EmblemConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iSeasonPower")] public partial long SeasonPower { get; }
    [ConfigField("_iSubAttributeGroup")] public partial long SubAttributeGroup { get; }
    [ConfigField("_kInitialMark")] public partial long[][] InitialMark { get; }
    [ConfigField("_kPrimaryAttribute")] public partial long[][] PrimaryAttribute { get; }
    [ConfigField("_kPrimaryAttributeLvUp")] public partial long[][] PrimaryAttributeLvUp { get; }
    [ConfigField("_kSubAttributeNum")] public partial long[][] SubAttributeNum { get; }
}
