using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's ShopConfig (data/config/ShopConfig.json).</summary>
[ConfigTable("ShopConfig")]
public readonly partial record struct ShopConfig(JsonElement Row)
{
    [ConfigField("_iClassID")] public partial long ClassId { get; }
    [ConfigField("_iFunctionId")] public partial long FunctionId { get; }
    [ConfigField("_iShopType")] public partial long ShopType { get; }
    [ConfigField("_kMultiverseArray")] public partial long[] MultiverseArray { get; }
}
