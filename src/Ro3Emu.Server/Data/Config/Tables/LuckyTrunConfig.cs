using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's LuckyTrunConfig (data/config/LuckyTrunConfig.json).</summary>
[ConfigTable("LuckyTrunConfig")]
public readonly partial record struct LuckyTrunConfig(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iweight")] public partial long Weight { get; }
}
