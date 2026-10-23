using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EquipSpecialGroupConfig (data/config/EquipSpecialGroupConfig.json).</summary>
[ConfigTable("EquipSpecialGroupConfig")]
public readonly partial record struct EquipSpecialGroupConfig(JsonElement Row)
{
    [ConfigField("_iGroup")] public partial long Group { get; }
    [ConfigField("_iSpecialID")] public partial long SpecialId { get; }
    [ConfigField("_iWeight")] public partial long Weight { get; }
}
