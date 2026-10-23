using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EmblemMarkConfig (data/config/EmblemMarkConfig.json).</summary>
[ConfigTable("EmblemMarkConfig")]
public readonly partial record struct EmblemMarkConfig(JsonElement Row)
{
    [ConfigField("_iEmblemMarkID")] public partial long EmblemMarkId { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iMarkNum")] public partial long MarkNum { get; }
    [ConfigField("_iMarkStage")] public partial long MarkStage { get; }
    [ConfigField("_iSeasonPower")] public partial long SeasonPower { get; }
    [ConfigField("_kBasicAttribute")] public partial JsonElement? BasicAttribute { get; }
    [ConfigField("_kMultiverseArray")] public partial JsonElement? MultiverseArray { get; }
    [ConfigField("_kSpecialAttribute")] public partial long[] SpecialAttribute { get; }
}
