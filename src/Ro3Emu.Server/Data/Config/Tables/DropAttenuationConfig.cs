using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's DropAttenuationConfig (data/config/DropAttenuationConfig.json).</summary>
[ConfigTable("DropAttenuationConfig")]
public readonly partial record struct DropAttenuationConfig(JsonElement Row)
{
    [ConfigField("_iAttenuationRatio")] public partial long AttenuationRatio { get; }
    [ConfigField("_iPlanID")] public partial long PlanId { get; }
    [ConfigField("_kLvDiffLowerlimit")] public partial long[] LvDiffLowerlimit { get; }
    [ConfigField("_kLvDiffUpLimit")] public partial long[] LvDiffUpLimit { get; }
}
