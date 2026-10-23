using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's MatchConfig (data/config/MatchConfig.json).</summary>
[ConfigTable("MatchConfig")]
public readonly partial record struct MatchConfig(JsonElement Row)
{
    [ConfigField("_iMatchType")] public partial long MatchType { get; }
    [ConfigField("_iStartQuantity")] public partial long StartQuantity { get; }
}
