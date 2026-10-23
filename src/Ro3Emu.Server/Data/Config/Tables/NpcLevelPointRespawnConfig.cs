using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's NpcLevelPointRespawnConfig (data/config/NpcLevelPointRespawnConfig.json).</summary>
[ConfigTable("NpcLevelPointRespawnConfig")]
public readonly partial record struct NpcLevelPointRespawnConfig(JsonElement Row)
{
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
    [ConfigField("_iMapid")] public partial long Mapid { get; }
    [ConfigField("_iNpcid")] public partial long Npcid { get; }
    [ConfigField("_iRespawnpointid")] public partial long Respawnpointid { get; }
}
