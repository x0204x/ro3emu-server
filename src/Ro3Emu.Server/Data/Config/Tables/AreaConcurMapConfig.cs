using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AreaConcurMapConfig (data/config/AreaConcurMapConfig.json).</summary>
[ConfigTable("AreaConcurMapConfig")]
public readonly partial record struct AreaConcurMapConfig(JsonElement Row)
{
    [ConfigField("_iArea")] public partial long Area { get; }
    [ConfigField("_iCondition")] public partial long Condition { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iMap")] public partial long Map { get; }
    [ConfigField("_iMapLimit")] public partial long MapLimit { get; }
    [ConfigField("_kNextMap")] public partial long[] NextMap { get; }
}
