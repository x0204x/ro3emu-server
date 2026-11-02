using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// The client's ProductConfig (data/config/ProductConfig.json): the paid products the pass and month card panels buy
/// (Lua_SDK_PayMgr.ReqPurchaseRequest with EPayChargeType.Product, enum_ProductId 4-8).
/// </summary>
[ConfigTable("ProductConfig")]
public readonly partial record struct ProductConfig(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iPlatform")] public partial long Platform { get; }
    [ConfigField("_iType")] public partial long Type { get; }
}
