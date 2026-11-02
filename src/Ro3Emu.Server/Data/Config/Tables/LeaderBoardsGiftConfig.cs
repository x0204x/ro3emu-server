using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// The client's LeaderBoardsGiftConfig (data/config/LeaderBoardsGiftConfig.json): one rank band of a board's reward
/// list (LeaderBoardsConfig._iRankRewards), [first, last] place and its MultiverseGroupConfig award
/// (Lua_CFG_LeaderBoardsGiftConfig.GetRange / GetGiftReward / GetGiftIdByGiftList).
/// </summary>
[ConfigTable("LeaderBoardsGiftConfig")]
public readonly partial record struct LeaderBoardsGiftConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_kParaNum")] public partial long[] ParaNum { get; }
    [ConfigField("_kReward")] public partial long Reward { get; }
}
