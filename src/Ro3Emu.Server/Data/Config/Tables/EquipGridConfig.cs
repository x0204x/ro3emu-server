using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EquipGridConfig (data/config/EquipGridConfig.json).</summary>
[ConfigTable("EquipGridConfig")]
public readonly partial record struct EquipGridConfig(JsonElement Row)
{
    [ConfigField("_iEquipGridType")] public partial long EquipGridType { get; }
    [ConfigField("_iGridCategory")] public partial long GridCategory { get; }
}
