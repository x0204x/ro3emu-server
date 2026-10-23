using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SpecialEffectConfig (data/config/SpecialEffectConfig.json).</summary>
[ConfigTable("SpecialEffectConfig")]
public readonly partial record struct SpecialEffectConfig(JsonElement Row)
{
    [ConfigField("_iGroupID")] public partial long GroupId { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iIsOtherDesc")] public partial long IsOtherDesc { get; }
    [ConfigField("_iPower")] public partial long Power { get; }
    [ConfigField("_kDescData")] public partial string[] DescData { get; }
    [ConfigField("_kOtherDesc")] public partial long[] OtherDesc { get; }
    [ConfigField("_kSpecialEffect")] public partial long[][] SpecialEffect { get; }
}
