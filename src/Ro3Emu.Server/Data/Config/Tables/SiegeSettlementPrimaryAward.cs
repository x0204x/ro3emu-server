using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SiegeSettlementPrimaryAward (data/config/SiegeSettlementPrimaryAward.json).</summary>
[ConfigTable("SiegeSettlementPrimaryAward")]
public readonly partial record struct SiegeSettlementPrimaryAward(JsonElement Row)
{
    [ConfigField("_iAwardGroupID")] public partial long AwardGroupId { get; }
    [ConfigField("_kMultiverseArray")] public partial long[] MultiverseArray { get; }
    [ConfigField("_kRank")] public partial long[] Rank { get; }
}
