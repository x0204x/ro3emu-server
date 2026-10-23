using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GVGWarZoneConfig (data/config/GVGWarZoneConfig.json).</summary>
[ConfigTable("GVGWarZoneConfig")]
public readonly partial record struct GVGWarZoneConfig(JsonElement Row)
{
    [ConfigField("_iBelongID")] public partial long BelongId { get; }
    [ConfigField("_iDungeonID")] public partial long DungeonId { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iType")] public partial long Type { get; }
}
