using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GuildIconConfig (data/config/GuildIconConfig.json).</summary>
[ConfigTable("GuildIconConfig")]
public readonly partial record struct GuildIconConfig(JsonElement Row)
{
    [ConfigField("_iCreateShow")] public partial long CreateShow { get; }
    [ConfigField("_iDefaultUnlock")] public partial long DefaultUnlock { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    /// <summary>The large picture of a badge frame (type 1) or design (type 2), a sprite name.</summary>
    [ConfigField("_kBigIcon")] public partial string BigIcon { get; }
    [ConfigField("_iNameDesc")] public partial JsonElement? NameDesc { get; }
}
