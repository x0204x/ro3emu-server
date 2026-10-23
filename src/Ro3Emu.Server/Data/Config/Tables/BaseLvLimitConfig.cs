using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's BaseLvLimitConfig (data/config/BaseLvLimitConfig.json).</summary>
[ConfigTable("BaseLvLimitConfig")]
public readonly partial record struct BaseLvLimitConfig(JsonElement Row)
{
    [ConfigField("_iBaseLimit")] public partial long BaseLimit { get; }
    [ConfigField("_iDay")] public partial long Day { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iServerLv")] public partial long ServerLv { get; }
}
