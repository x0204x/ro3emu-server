using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PronteraDefenseMonsterConfig (data/config/PronteraDefenseMonsterConfig.json).</summary>
[ConfigTable("PronteraDefenseMonsterConfig")]
public readonly partial record struct PronteraDefenseMonsterConfig(JsonElement Row)
{
    [ConfigField("_iDifficulty")] public partial long Difficulty { get; }
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iNpcId")] public partial long NpcId { get; }
}
