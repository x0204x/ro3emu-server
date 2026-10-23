using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SiegeConfig (data/config/SiegeConfig.json).</summary>
[ConfigTable("SiegeConfig")]
public readonly partial record struct SiegeConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iLosePlayerAward")] public partial long LosePlayerAward { get; }
    [ConfigField("_iWinPlayerAward")] public partial long WinPlayerAward { get; }
}
