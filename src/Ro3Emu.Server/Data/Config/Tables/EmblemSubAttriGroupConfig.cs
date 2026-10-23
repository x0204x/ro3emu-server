using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EmblemSubAttriGroupConfig (data/config/EmblemSubAttriGroupConfig.json).</summary>
[ConfigTable("EmblemSubAttriGroupConfig")]
public readonly partial record struct EmblemSubAttriGroupConfig(JsonElement Row)
{
    [ConfigField("_iGroup")] public partial long Group { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iMax")] public partial long Max { get; }
    [ConfigField("_iMin")] public partial long Min { get; }
    [ConfigField("_iSeasonPower")] public partial long SeasonPower { get; }
    [ConfigField("_iSubAttriID")] public partial long SubAttriId { get; }
    [ConfigField("_iWeight")] public partial long Weight { get; }
    [ConfigField("_kMultiverseArray")] public partial JsonElement? MultiverseArray { get; }
}
