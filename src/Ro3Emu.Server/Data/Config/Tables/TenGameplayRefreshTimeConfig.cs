using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TenGameplayRefreshTimeConfig (data/config/TenGameplayRefreshTimeConfig.json).</summary>
[ConfigTable("TenGameplayRefreshTimeConfig")]
public readonly partial record struct TenGameplayRefreshTimeConfig(JsonElement Row)
{
    [ConfigField("_iGroupID")] public partial long GroupId { get; }
}
