using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SocialOutfitWearConfig (data/config/SocialOutfitWearConfig.json).</summary>
[ConfigTable("SocialOutfitWearConfig")]
public readonly partial record struct SocialOutfitWearConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iSocialOutfit")] public partial long SocialOutfit { get; }
    [ConfigField("_iTimeLimit")] public partial long TimeLimit { get; }
}
