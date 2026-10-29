using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GuildActivityConfig (data/config/GuildActivityConfig.json): the guild activities.</summary>
[ConfigTable("GuildActivityConfig")]
public readonly partial record struct GuildActivityConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iGroupID")] public partial long GroupId { get; }
}
