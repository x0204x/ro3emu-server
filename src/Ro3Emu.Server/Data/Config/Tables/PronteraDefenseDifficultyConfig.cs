using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PronteraDefenseDifficultyConfig (data/config/PronteraDefenseDifficultyConfig.json).</summary>
[ConfigTable("PronteraDefenseDifficultyConfig")]
public readonly partial record struct PronteraDefenseDifficultyConfig(JsonElement Row)
{
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iKillAcive")] public partial long KillAcive { get; }
    [ConfigField("_iKillAward")] public partial long KillAward { get; }
    [ConfigField("_iKillPoint")] public partial long KillPoint { get; }
    [ConfigField("_iRefreshCount")] public partial long RefreshCount { get; }
    [ConfigField("_iRefreshDivisor")] public partial long RefreshDivisor { get; }
    [ConfigField("_iRefreshInterval")] public partial long RefreshInterval { get; }
}
