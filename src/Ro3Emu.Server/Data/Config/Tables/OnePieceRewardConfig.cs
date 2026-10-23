using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's OnePieceRewardConfig (data/config/OnePieceRewardConfig.json).</summary>
[ConfigTable("OnePieceRewardConfig")]
public readonly partial record struct OnePieceRewardConfig(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iExtraAward")] public partial long ExtraAward { get; }
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iMonsterId")] public partial long MonsterId { get; }
}
