using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PronteraDefenseRoundConfig (data/config/PronteraDefenseRoundConfig.json).</summary>
[ConfigTable("PronteraDefenseRoundConfig")]
public readonly partial record struct PronteraDefenseRoundConfig(JsonElement Row)
{
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iMapId")] public partial long MapId { get; }
    [ConfigField("_iRoundId")] public partial long RoundId { get; }
}
