using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's RefiningSeasonTable (data/config/RefiningSeasonTable.json).</summary>
[ConfigTable("RefiningSeasonTable")]
public readonly partial record struct RefiningSeasonTable(JsonElement Row)
{
    [ConfigField("_iEquipType")] public partial long EquipType { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_kAttrsId")] public partial long[][] AttrsId { get; }
    [ConfigField("_kDeplete")] public partial long[][] Deplete { get; }
}
