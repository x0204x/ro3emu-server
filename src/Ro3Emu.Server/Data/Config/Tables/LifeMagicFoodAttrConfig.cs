using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's LifeMagicFoodAttrConfig (data/config/LifeMagicFoodAttrConfig.json).</summary>
[ConfigTable("LifeMagicFoodAttrConfig")]
public readonly partial record struct LifeMagicFoodAttrConfig(JsonElement Row)
{
    [ConfigField("_kAttrs")] public partial long[][] Attrs { get; }
    [ConfigField("_kLimit")] public partial long Limit { get; }
}
