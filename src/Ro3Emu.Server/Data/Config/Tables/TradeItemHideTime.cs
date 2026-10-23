using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TradeItemHideTime (data/config/TradeItemHideTime.json).</summary>
[ConfigTable("TradeItemHideTime")]
public readonly partial record struct TradeItemHideTime(JsonElement Row)
{
    [ConfigField("_kHideTimeMin")] public partial long HideTimeMin { get; }
    [ConfigField("_kServerLev")] public partial long ServerLev { get; }
}
