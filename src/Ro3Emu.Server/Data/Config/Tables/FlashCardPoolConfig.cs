using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's FlashCardPoolConfig (data/config/FlashCardPoolConfig.json).</summary>
[ConfigTable("FlashCardPoolConfig")]
public readonly partial record struct FlashCardPoolConfig(JsonElement Row)
{
    [ConfigField("_iGroupID")] public partial long GroupId { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iWeight")] public partial long Weight { get; }
    [ConfigField("_kCardList")] public partial long[] CardList { get; }
}
