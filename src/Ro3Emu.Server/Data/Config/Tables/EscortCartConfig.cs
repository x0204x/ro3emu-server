using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EscortCartConfig (data/config/EscortCartConfig.json).</summary>
[ConfigTable("EscortCartConfig")]
public readonly partial record struct EscortCartConfig(JsonElement Row)
{
    [ConfigField("_iMaxTransportNum")] public partial long MaxTransportNum { get; }
    [ConfigField("_kDamageLevelArgs")] public partial long[][] DamageLevelArgs { get; }
    [ConfigField("_kDrops")] public partial long[][] Drops { get; }
}
