using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's NewActivityConfig (data/config/NewActivityConfig.json).</summary>
[ConfigTable("NewActivityConfig")]
public readonly partial record struct NewActivityConfig(JsonElement Row)
{
    [ConfigField("_iActivityType")] public partial long ActivityType { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_kMultiverseArray")] public partial long[] MultiverseArray { get; }
    [ConfigField("_kOpenCondition")] public partial long OpenCondition { get; }
    [ConfigField("_kTaskRenew")] public partial long[][] TaskRenew { get; }
}
