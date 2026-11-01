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
    /// <summary>The zone's own map (MapConfig id), 30301-30336 in the export.</summary>
    [ConfigField("_iMapID")] public partial long MapId { get; }
    /// <summary>The zone's name as a text id with its parameters ("Stronghold @{1}", 4).</summary>
    [ConfigField("_iName")] public partial JsonElement? Name { get; }
}
