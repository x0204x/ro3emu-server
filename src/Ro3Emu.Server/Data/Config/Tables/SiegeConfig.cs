using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SiegeConfig (data/config/SiegeConfig.json).</summary>
[ConfigTable("SiegeConfig")]
public readonly partial record struct SiegeConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iLosePlayerAward")] public partial long LosePlayerAward { get; }
    [ConfigField("_iWinPlayerAward")] public partial long WinPlayerAward { get; }
    /// <summary>1 a main city, 2 a prefecture.</summary>
    [ConfigField("_iCityType")] public partial long CityType { get; }
    [ConfigField("_iBelongArea")] public partial long BelongArea { get; }
    [ConfigField("_iCityName")] public partial JsonElement? CityName { get; }
}
