using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's MVPScoreConfig (data/config/MVPScoreConfig.json).</summary>
[ConfigTable("MVPScoreConfig")]
public readonly partial record struct MVPScoreConfig(JsonElement Row)
{
    [ConfigField("_iDeath")] public partial long Death { get; }
    [ConfigField("_iDemage")] public partial long Demage { get; }
    [ConfigField("_iDemageSuffer")] public partial long DemageSuffer { get; }
    [ConfigField("_iHeal")] public partial long Heal { get; }
}
