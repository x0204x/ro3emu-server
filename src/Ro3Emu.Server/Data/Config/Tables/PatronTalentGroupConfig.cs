using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PatronTalentGroupConfig (data/config/PatronTalentGroupConfig.json); Astral Level, ParagonService.</summary>
[ConfigTable("PatronTalentGroupConfig")]
public readonly partial record struct PatronTalentGroupConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_kTanlentPoints")] public partial long[] TalentPoints { get; }
}
