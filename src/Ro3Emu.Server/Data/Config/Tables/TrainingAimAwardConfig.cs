using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TrainingAimAwardConfig (data/config/TrainingAimAwardConfig.json).</summary>
[ConfigTable("TrainingAimAwardConfig")]
public readonly partial record struct TrainingAimAwardConfig(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iDps")] public partial long Dps { get; }
    [ConfigField("_iRootJob")] public partial long RootJob { get; }
}
