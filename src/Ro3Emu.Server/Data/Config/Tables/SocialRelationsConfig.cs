using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// The client's SocialRelationsConfig (data/config/SocialRelationsConfig.json): what a Connect quest's ActionConfig 116
/// UpdateSocialityScore [id] gives, per relation, as action rows (["107", amount, p3], GainIntimacy). An empty slot
/// is [[""]]: the friend column reads it as [[0]]; the other three are empty in every row and stay raw.
/// </summary>
[ConfigTable("SocialRelationsConfig")]
public readonly partial record struct SocialRelationsConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iFunction")] public partial long Function { get; }
    [ConfigField("_iFriendValue")] public partial long[][] FriendValue { get; }
    [ConfigField("_iBrotherhoodValue")] public partial JsonElement? BrotherhoodValue { get; }
    [ConfigField("_iLoveValue")] public partial JsonElement? LoveValue { get; }
    [ConfigField("_iMasterValue")] public partial JsonElement? MasterValue { get; }
}
