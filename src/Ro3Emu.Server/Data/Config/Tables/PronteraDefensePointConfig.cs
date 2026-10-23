using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PronteraDefensePointConfig (data/config/PronteraDefensePointConfig.json).</summary>
[ConfigTable("PronteraDefensePointConfig")]
public readonly partial record struct PronteraDefensePointConfig(JsonElement Row)
{
    [ConfigField("_iDifficulty")] public partial long Difficulty { get; }
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iMapId")] public partial long MapId { get; }
    [ConfigField("_iPointId")] public partial long[] PointId { get; }
    [ConfigField("_iRoundMonster")] public partial long[][] RoundMonster { get; }
}
