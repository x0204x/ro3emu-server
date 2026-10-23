using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EnchantIDConfig (data/config/EnchantIDConfig.json).</summary>
[ConfigTable("EnchantIDConfig")]
public readonly partial record struct EnchantIDConfig(JsonElement Row)
{
    [ConfigField("_iEnchantIDs")] public partial long EnchantIDs { get; }
    [ConfigField("_kAttrs")] public partial long[] Attrs { get; }
}
