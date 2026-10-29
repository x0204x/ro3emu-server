using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's BattleGloryDeathDif (data/config/BattleGloryDeathDif.json).</summary>
[ConfigTable("BattleGloryDeathDif")]
public readonly partial record struct BattleGloryDeathDif(JsonElement Row)
{
    [ConfigField("_iCount")] public partial long Count { get; }
    [ConfigField("_iCoe")] public partial long Coe { get; }
}
