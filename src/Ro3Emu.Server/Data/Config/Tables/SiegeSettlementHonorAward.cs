using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SiegeSettlementHonorAward (data/config/SiegeSettlementHonorAward.json).</summary>
[ConfigTable("SiegeSettlementHonorAward")]
public readonly partial record struct SiegeSettlementHonorAward(JsonElement Row)
{
    [ConfigField("_iAwardGroupID")] public partial long AwardGroupId { get; }
    [ConfigField("_kMultiverseArray")] public partial long[] MultiverseArray { get; }
    [ConfigField("_kRank")] public partial long[] Rank { get; }
}
