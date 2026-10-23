using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's RefreshInfo (data/config/RefreshInfo.json).</summary>
[ConfigTable("RefreshInfo")]
public readonly partial record struct RefreshInfo(JsonElement Row)
{
    [ConfigField("_iMinExistUnitNum")] public partial long MinExistUnitNum { get; }
    [ConfigField("_iRefreshTime")] public partial long RefreshTime { get; }
    [ConfigField("_iUnitNum")] public partial long UnitNum { get; }
    [ConfigField("_kUnitIDs")] public partial long[][] UnitIDs { get; }
}
