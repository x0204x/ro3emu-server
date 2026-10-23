using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SocialOutfitConfig (data/config/SocialOutfitConfig.json).</summary>
[ConfigTable("SocialOutfitConfig")]
public readonly partial record struct SocialOutfitConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iIsDefaultUnLock")] public partial long IsDefaultUnLock { get; }
    [ConfigField("_iIsDefaultWear")] public partial long IsDefaultWear { get; }
    [ConfigField("_iJobID")] public partial long JobId { get; }
    [ConfigField("_iSex")] public partial long Sex { get; }
    [ConfigField("_iType")] public partial long Type { get; }
}
