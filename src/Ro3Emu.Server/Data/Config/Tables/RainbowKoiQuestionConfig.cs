using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's RainbowKoiQuestionConfig (data/config/RainbowKoiQuestionConfig.json).</summary>
[ConfigTable("RainbowKoiQuestionConfig")]
public readonly partial record struct RainbowKoiQuestionConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iQuestionAnswer")] public partial long QuestionAnswer { get; }
}
