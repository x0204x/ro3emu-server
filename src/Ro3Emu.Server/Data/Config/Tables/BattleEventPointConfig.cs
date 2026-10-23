using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's BattleEventPointConfig (data/config/BattleEventPointConfig.json).</summary>
[ConfigTable("BattleEventPointConfig")]
public readonly partial record struct BattleEventPointConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iName")] public partial long[] Name { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kData")] public partial long[] Data { get; }
    [ConfigField("_kGameType")] public partial long[] GameType { get; }
    [ConfigField("_kScore")] public partial long[] Score { get; }
}
