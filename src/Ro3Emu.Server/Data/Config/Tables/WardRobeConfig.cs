using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's WardRobeConfig (data/config/WardRobeConfig.json).</summary>
[ConfigTable("WardRobeConfig")]
public readonly partial record struct WardRobeConfig(JsonElement Row)
{
    [ConfigField("_iDefaultLightUp")] public partial long DefaultLightUp { get; }
    [ConfigField("_iDisable")] public partial long Disable { get; }
    [ConfigField("_iFashionScore")] public partial long FashionScore { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iMain")] public partial long Main { get; }
    [ConfigField("_iQuality")] public partial long Quality { get; }
    [ConfigField("_iSexNeed")] public partial long SexNeed { get; }
    [ConfigField("_iSystemID")] public partial long SystemId { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kMaterial")] public partial long[][] Material { get; }
}
