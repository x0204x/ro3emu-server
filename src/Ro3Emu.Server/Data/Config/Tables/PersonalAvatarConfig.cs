using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PersonalAvatarConfig (data/config/PersonalAvatarConfig.json).</summary>
[ConfigTable("PersonalAvatarConfig")]
public readonly partial record struct PersonalAvatarConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
}
