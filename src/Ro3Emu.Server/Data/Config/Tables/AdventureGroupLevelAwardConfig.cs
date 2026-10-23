using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AdventureGroupLevelAwardConfig (data/config/AdventureGroupLevelAwardConfig.json).</summary>
[ConfigTable("AdventureGroupLevelAwardConfig")]
public readonly partial record struct AdventureGroupLevelAwardConfig(JsonElement Row)
{
    [ConfigField("_iConveneNum")] public partial long ConveneNum { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
    [ConfigField("_iLevelAward")] public partial long LevelAward { get; }
    [ConfigField("_iNum")] public partial long Num { get; }
}
