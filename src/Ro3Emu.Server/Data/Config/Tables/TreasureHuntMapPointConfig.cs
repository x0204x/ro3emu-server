using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TreasureHuntMapPointConfig (data/config/TreasureHuntMapPointConfig.json).</summary>
[ConfigTable("TreasureHuntMapPointConfig")]
public readonly partial record struct TreasureHuntMapPointConfig(JsonElement Row)
{
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iMapId")] public partial long MapId { get; }
    [ConfigField("_iMapPointId")] public partial long MapPointId { get; }
}
