using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's NpcRespawnGroupConfig (data/config/NpcRespawnGroupConfig.json).</summary>
[ConfigTable("NpcRespawnGroupConfig")]
public readonly partial record struct NpcRespawnGroupConfig(JsonElement Row)
{
    [ConfigField("_iByweightnum")] public partial long Byweightnum { get; }
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_kNpcgroup")] public partial long[] Npcgroup { get; }
}
