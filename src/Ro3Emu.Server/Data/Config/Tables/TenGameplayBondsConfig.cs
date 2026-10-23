using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TenGameplayBondsConfig (data/config/TenGameplayBondsConfig.json).</summary>
[ConfigTable("TenGameplayBondsConfig")]
public readonly partial record struct TenGameplayBondsConfig(JsonElement Row)
{
    [ConfigField("_kBuff")] public partial long[] Buff { get; }
    [ConfigField("_kUnlock")] public partial long Unlock { get; }
}
