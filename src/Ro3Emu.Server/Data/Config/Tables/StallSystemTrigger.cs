using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's StallSystemTrigger (data/config/StallSystemTrigger.json).</summary>
[ConfigTable("StallSystemTrigger")]
public readonly partial record struct StallSystemTrigger(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_kCond")] public partial long[][] Cond { get; }
    [ConfigField("_kJob")] public partial long[] Job { get; }
    [ConfigField("_kLevel")] public partial long[] Level { get; }
    [ConfigField("_kSaleGoods")] public partial long[] SaleGoods { get; }
}
