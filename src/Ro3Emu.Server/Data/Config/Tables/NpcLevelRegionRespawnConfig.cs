using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's NpcLevelRegionRespawnConfig (data/config/NpcLevelRegionRespawnConfig.json).</summary>
[ConfigTable("NpcLevelRegionRespawnConfig")]
public readonly partial record struct NpcLevelRegionRespawnConfig(JsonElement Row)
{
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
    [ConfigField("_iMapid")] public partial long Mapid { get; }
    [ConfigField("_iNpcrespawngroup")] public partial long[] Npcrespawngroup { get; }
    [ConfigField("_iRespawnregionid")] public partial long Respawnregionid { get; }
}
