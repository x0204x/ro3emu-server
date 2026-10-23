using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's MVPConfig (data/config/MVPConfig.json).</summary>
[ConfigTable("MVPConfig")]
public readonly partial record struct MVPConfig(JsonElement Row)
{
    [ConfigField("_iFirstDamageAward")] public partial long FirstDamageAward { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iLastDamageAward")] public partial long LastDamageAward { get; }
    [ConfigField("_iLevelDiff")] public partial long LevelDiff { get; }
    [ConfigField("_iMapID")] public partial long MapId { get; }
    [ConfigField("_iNPCID")] public partial long NPCId { get; }
    [ConfigField("_iPartAward")] public partial long PartAward { get; }
    [ConfigField("_iRareAward")] public partial long RareAward { get; }
    [ConfigField("_iRareRewardProbability")] public partial long RareRewardProbability { get; }
    [ConfigField("_iRefreshTime")] public partial long RefreshTime { get; }
    [ConfigField("_iTeamAward")] public partial long TeamAward { get; }
}
