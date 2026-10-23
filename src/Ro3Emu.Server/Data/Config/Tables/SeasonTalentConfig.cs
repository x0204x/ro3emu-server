using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SeasonTalentConfig (data/config/SeasonTalentConfig.json).</summary>
[ConfigTable("SeasonTalentConfig")]
public readonly partial record struct SeasonTalentConfig(JsonElement Row)
{
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iIsStartPoint")] public partial long IsStartPoint { get; }
    [ConfigField("_iLevelGroupId")] public partial long LevelGroupId { get; }
    [ConfigField("_iMaxLevel")] public partial long MaxLevel { get; }
    [ConfigField("_iNeedLevel")] public partial long NeedLevel { get; }
    [ConfigField("_iTalentTreeID")] public partial long TalentTreeId { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kAfterids")] public partial long[] Afterids { get; }
}
