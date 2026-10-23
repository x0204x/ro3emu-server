using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EleConfig (data/config/EleConfig.json).</summary>
[ConfigTable("EleConfig")]
public readonly partial record struct EleConfig(JsonElement Row)
{
    [ConfigField("_iEarth")] public partial long Earth { get; }
    [ConfigField("_iFire")] public partial long Fire { get; }
    [ConfigField("_iGhost")] public partial long Ghost { get; }
    [ConfigField("_iHoly")] public partial long Holy { get; }
    [ConfigField("_iNone")] public partial long None { get; }
    [ConfigField("_iPoison")] public partial long Poison { get; }
    [ConfigField("_iShadow")] public partial long Shadow { get; }
    [ConfigField("_iUndead")] public partial long Undead { get; }
    [ConfigField("_iWater")] public partial long Water { get; }
    [ConfigField("_iWind")] public partial long Wind { get; }
}
