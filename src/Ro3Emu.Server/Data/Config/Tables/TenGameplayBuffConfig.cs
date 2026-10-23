using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TenGameplayBuffConfig (data/config/TenGameplayBuffConfig.json).</summary>
[ConfigTable("TenGameplayBuffConfig")]
public readonly partial record struct TenGameplayBuffConfig(JsonElement Row)
{
    [ConfigField("_iAddReviveNum")] public partial long AddReviveNum { get; }
    [ConfigField("_iGroupType")] public partial long GroupType { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
    [ConfigField("_iUnlockDay")] public partial long UnlockDay { get; }
    [ConfigField("_kBuffID")] public partial long[] BuffId { get; }
}
