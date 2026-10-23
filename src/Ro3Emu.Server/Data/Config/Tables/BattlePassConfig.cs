using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's BattlePassConfig (data/config/BattlePassConfig.json).</summary>
[ConfigTable("BattlePassConfig")]
public readonly partial record struct BattlePassConfig(JsonElement Row)
{
    [ConfigField("_iExpCurrencyId")] public partial long ExpCurrencyId { get; }
    [ConfigField("_iGiftItemId")] public partial long GiftItemId { get; }
    [ConfigField("_iMaxLevel")] public partial long MaxLevel { get; }
    [ConfigField("_iPremiumItemId")] public partial long PremiumItemId { get; }
    [ConfigField("_iPrivilegeIds")] public partial long[] PrivilegeIds { get; }
    [ConfigField("_kBuyLevelConsume")] public partial long[][] BuyLevelConsume { get; }
}
