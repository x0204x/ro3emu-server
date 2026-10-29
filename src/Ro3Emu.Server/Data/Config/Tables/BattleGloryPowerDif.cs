using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's BattleGloryPowerDif (data/config/BattleGloryPowerDif.json).</summary>
[ConfigTable("BattleGloryPowerDif")]
public readonly partial record struct BattleGloryPowerDif(JsonElement Row)
{
    [ConfigField("_iBattle")] public partial long Battle { get; }
    [ConfigField("_iCoe")] public partial long Coe { get; }
}
