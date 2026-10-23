using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's CampFireAffixNewConfig (data/config/CampFireAffixNewConfig.json).</summary>
[ConfigTable("CampFireAffixNewConfig")]
public readonly partial record struct CampFireAffixNewConfig(JsonElement Row)
{
    [ConfigField("_iActionParams")] public partial long[][] ActionParams { get; }
    [ConfigField("_iActionType")] public partial long ActionType { get; }
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iPhase")] public partial long Phase { get; }
}
