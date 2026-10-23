using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's StallSystemItem (data/config/StallSystemItem.json).</summary>
[ConfigTable("StallSystemItem")]
public readonly partial record struct StallSystemItem(JsonElement Row)
{
    [ConfigField("_iBuyLimit")] public partial long BuyLimit { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iItemID")] public partial long ItemId { get; }
    [ConfigField("_iTakeCycle")] public partial long[] TakeCycle { get; }
    [ConfigField("_iTakeFirst")] public partial long[] TakeFirst { get; }
    [ConfigField("_kCond")] public partial long[][] Cond { get; }
    [ConfigField("_kIsBindGive")] public partial long[] IsBindGive { get; }
    [ConfigField("_kPrice")] public partial long[] Price { get; }
}
