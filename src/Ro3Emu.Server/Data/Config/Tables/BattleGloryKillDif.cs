using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's BattleGloryKillDif (data/config/BattleGloryKillDif.json).</summary>
[ConfigTable("BattleGloryKillDif")]
public readonly partial record struct BattleGloryKillDif(JsonElement Row)
{
    [ConfigField("_iCount")] public partial long Count { get; }
    [ConfigField("_iCoe")] public partial long Coe { get; }
}
