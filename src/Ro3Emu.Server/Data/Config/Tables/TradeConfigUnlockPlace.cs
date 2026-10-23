using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TradeConfigUnlockPlace (data/config/TradeConfigUnlockPlace.json).</summary>
[ConfigTable("TradeConfigUnlockPlace")]
public readonly partial record struct TradeConfigUnlockPlace(JsonElement Row)
{
    [ConfigField("_kSpend")] public partial long[] Spend { get; }
}
