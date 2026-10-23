using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TreasureHuntResultRateConfig (data/config/TreasureHuntResultRateConfig.json).</summary>
[ConfigTable("TreasureHuntResultRateConfig")]
public readonly partial record struct TreasureHuntResultRateConfig(JsonElement Row)
{
    [ConfigField("_iAction")] public partial JsonElement? Action { get; }
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kGroupId")] public partial long[] GroupId { get; }
    [ConfigField("_kGroupProbability")] public partial long GroupProbability { get; }
}
