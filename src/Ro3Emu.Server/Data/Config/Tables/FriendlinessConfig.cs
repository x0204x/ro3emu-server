using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's FriendlinessConfig (data/config/FriendlinessConfig.json).</summary>
[ConfigTable("FriendlinessConfig")]
public readonly partial record struct FriendlinessConfig(JsonElement Row)
{
    [ConfigField("_iFriendNum")] public partial long FriendNum { get; }
    [ConfigField("_iFriendlev")] public partial long Friendlev { get; }
    [ConfigField("_iID")] public partial long Id { get; }
}
