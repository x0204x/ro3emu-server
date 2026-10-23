using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EquipAttriGroupConfig (data/config/EquipAttriGroupConfig.json).</summary>
[ConfigTable("EquipAttriGroupConfig")]
public readonly partial record struct EquipAttriGroupConfig(JsonElement Row)
{
    [ConfigField("_iAttriID")] public partial long AttriId { get; }
    [ConfigField("_iGroup")] public partial long Group { get; }
    [ConfigField("_iMax")] public partial long Max { get; }
    [ConfigField("_iMin")] public partial long Min { get; }
    [ConfigField("_iWeight")] public partial long Weight { get; }
}
