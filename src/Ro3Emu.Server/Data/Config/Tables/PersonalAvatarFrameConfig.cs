using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PersonalAvatarFrameConfig (data/config/PersonalAvatarFrameConfig.json).</summary>
[ConfigTable("PersonalAvatarFrameConfig")]
public readonly partial record struct PersonalAvatarFrameConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
}
