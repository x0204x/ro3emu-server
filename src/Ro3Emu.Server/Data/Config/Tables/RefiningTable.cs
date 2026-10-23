using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's RefiningTable (data/config/RefiningTable.json).</summary>
[ConfigTable("RefiningTable")]
public readonly partial record struct RefiningTable(JsonElement Row)
{
    [ConfigField("_iEquipType")] public partial long EquipType { get; }
    [ConfigField("_iRefiningInterval")] public partial long RefiningInterval { get; }
}
