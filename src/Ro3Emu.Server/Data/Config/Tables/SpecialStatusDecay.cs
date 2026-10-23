using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SpecialStatusDecay (data/config/SpecialStatusDecay.json).</summary>
[ConfigTable("SpecialStatusDecay")]
public readonly partial record struct SpecialStatusDecay(JsonElement Row)
{
    [ConfigField("_iDecayRatio")] public partial long DecayRatio { get; }
    [ConfigField("_iDecayType")] public partial long DecayType { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iImmunityTime")] public partial long ImmunityTime { get; }
    [ConfigField("_iImmunodetection")] public partial long Immunodetection { get; }
    [ConfigField("_iIntervalDetection")] public partial long IntervalDetection { get; }
    [ConfigField("_kBuffCategory")] public partial long[] BuffCategory { get; }
}
