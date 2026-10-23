using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SiegeSettlementSeniorAward (data/config/SiegeSettlementSeniorAward.json).</summary>
[ConfigTable("SiegeSettlementSeniorAward")]
public readonly partial record struct SiegeSettlementSeniorAward(JsonElement Row)
{
    [ConfigField("_iAwardGroupID")] public partial long AwardGroupId { get; }
    [ConfigField("_kMultiverseArray")] public partial long[] MultiverseArray { get; }
}
