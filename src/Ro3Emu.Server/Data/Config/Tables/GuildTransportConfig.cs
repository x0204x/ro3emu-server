using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GuildTransportConfig (data/config/GuildTransportConfig.json).</summary>
[ConfigTable("GuildTransportConfig")]
public readonly partial record struct GuildTransportConfig(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long AwardI { get; }
    [ConfigField("_iBasicWeight")] public partial long BasicWeight { get; }
    [ConfigField("_iDayMin")] public partial long DayMin { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iItem")] public partial long Item { get; }
    [ConfigField("_iNum")] public partial long Num { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kAward")] public partial long[] AwardK { get; }
    [ConfigField("_kMultiverseArray")] public partial long[] MultiverseArray { get; }
    [ConfigField("_kServerLv")] public partial long[] ServerLv { get; }
}
