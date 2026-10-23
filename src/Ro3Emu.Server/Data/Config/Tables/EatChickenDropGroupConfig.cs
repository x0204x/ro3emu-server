using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EatChickenDropGroupConfig (data/config/EatChickenDropGroupConfig.json).</summary>
[ConfigTable("EatChickenDropGroupConfig", Grouped = true)]
public readonly partial record struct EatChickenDropGroupConfig(JsonElement Row)
{
    [ConfigField("_iDropItem", Optional = true)] public partial long DropItem { get; }
    [ConfigField("_iWeights", Optional = true)] public partial long Weights { get; }
    [ConfigField("_iMaxNumber", Optional = true)] public partial long MaxNumber { get; }
    [ConfigField("_iMinNumber", Optional = true)] public partial long MinNumber { get; }
}
