using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's LeaderBoardsConfig (data/config/LeaderBoardsConfig.json).</summary>
[ConfigTable("LeaderBoardsConfig")]
public readonly partial record struct LeaderBoardsConfig(JsonElement Row)
{
    [ConfigField("_iFullRankMax")] public partial long FullRankMax { get; }
}
