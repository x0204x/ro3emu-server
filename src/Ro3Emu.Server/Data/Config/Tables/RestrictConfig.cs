using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's RestrictConfig (data/config/RestrictConfig.json).</summary>
[ConfigTable("RestrictConfig")]
public readonly partial record struct RestrictConfig(JsonElement Row)
{
    [ConfigField("_iMaxRestrictNum")] public partial long MaxRestrictNum { get; }
    [ConfigField("_iRecoverType")] public partial long RecoverType { get; }
    [ConfigField("_iRestrictId")] public partial long RestrictId { get; }
    [ConfigField("_iType")] public partial long Type { get; }
}
