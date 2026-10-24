using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PatronTalentAttrConfig (data/config/PatronTalentAttrConfig.json); Astral Level, ParagonService.</summary>
[ConfigTable("PatronTalentAttrConfig")]
public readonly partial record struct PatronTalentAttrConfig(JsonElement Row)
{
    [ConfigField("_kAttrs")] public partial long[][] Attrs { get; }
}
