using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SeasonTalentTreeConfig (data/config/SeasonTalentTreeConfig.json).</summary>
[ConfigTable("SeasonTalentTreeConfig")]
public readonly partial record struct SeasonTalentTreeConfig(JsonElement Row)
{
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iNeedLevel")] public partial long NeedLevel { get; }
    [ConfigField("_iTreeCondition")] public partial long TreeCondition { get; }
    [ConfigField("_iUpPvPLvLimit")] public partial long UpPvPLvLimit { get; }
    [ConfigField("_kMultiverseArray")] public partial long[] MultiverseArray { get; }
}
