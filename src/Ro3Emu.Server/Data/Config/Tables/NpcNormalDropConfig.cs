using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's NpcNormalDropConfig (data/config/NpcNormalDropConfig.json).</summary>
[ConfigTable("NpcNormalDropConfig")]
public readonly partial record struct NpcNormalDropConfig(JsonElement Row)
{
    [ConfigField("_iMapId")] public partial long[] MapId { get; }
}
