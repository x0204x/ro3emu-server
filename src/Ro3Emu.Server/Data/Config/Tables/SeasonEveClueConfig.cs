using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SeasonEveClueConfig (data/config/SeasonEveClueConfig.json).</summary>
[ConfigTable("SeasonEveClueConfig")]
public readonly partial record struct SeasonEveClueConfig(JsonElement Row)
{
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iMapId")] public partial long MapId { get; }
    [ConfigField("_iPointId")] public partial long PointId { get; }
}
