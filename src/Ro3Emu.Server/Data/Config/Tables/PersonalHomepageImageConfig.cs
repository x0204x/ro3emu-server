using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PersonalHomepageImageConfig (data/config/PersonalHomepageImageConfig.json).</summary>
[ConfigTable("PersonalHomepageImageConfig")]
public readonly partial record struct PersonalHomepageImageConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iImageType")] public partial long ImageType { get; }
    [ConfigField("_iUnlockCondition")] public partial long UnlockCondition { get; }
}
