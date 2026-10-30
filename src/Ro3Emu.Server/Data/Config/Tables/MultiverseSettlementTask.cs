using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's MultiverseSettlementTask (data/config/MultiverseSettlementTask.json).</summary>
[ConfigTable("MultiverseSettlementTask")]
public readonly partial record struct MultiverseSettlementTask(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iTaskID")] public partial long TaskId { get; }
    [ConfigField("_kMultiverseArray")] public partial long[] MultiverseArray { get; }
}
