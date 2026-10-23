using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's DropGroupConfig (data/config/DropGroupConfig.json).</summary>
[ConfigTable("DropGroupConfig", Grouped = true)]
public readonly partial record struct DropGroupConfig(JsonElement Row)
{
    // D Asia serializes only values that differ from the Lua row defaults.
    [ConfigField("_iActivityid", Optional = true)] public partial long Activityid { get; }
    [ConfigField("_iBind", Optional = true)] public partial long Bind { get; }
    [ConfigField("_iJobLimit", Optional = true)] public partial long[] JobLimit { get; }
    [ConfigField("_iMaxLevelLimit", Optional = true)] public partial long MaxLevelLimit { get; }
    [ConfigField("_iMaxNumber", Optional = true)] public partial long MaxNumber { get; }
    [ConfigField("_iMinLevelLimit", Optional = true)] public partial long MinLevelLimit { get; }
    [ConfigField("_iMinNumber", Optional = true)] public partial long MinNumber { get; }
    [ConfigField("_iQuestLimit", Optional = true)] public partial long QuestLimit { get; }
    [ConfigField("_iWeights", Optional = true)] public partial long Weights { get; }
    [ConfigField("_kCoefficient", Optional = true)] public partial long Coefficient { get; }
    [ConfigField("_kDropItem", Optional = true)] public partial long DropItem { get; }
    [ConfigField("_kIsBindGive", Optional = true)] public partial long[] IsBindGive { get; }
    [ConfigField("_kServerLevel", Optional = true)] public partial JsonElement? ServerLevel { get; }

    // D Asia omits Lua defaults from repeated rows; an explicit zero remains an explicit zero.
    public bool HasDropItem => Row.TryGetProperty("_kDropItem", out _);
    public bool HasMinNumber => Row.TryGetProperty("_iMinNumber", out _);
    public bool HasMaxNumber => Row.TryGetProperty("_iMaxNumber", out _);
    public long WeightOrDefault => Row.TryGetProperty("_iWeights", out _) ? Weights : 1;
    public long MinNumberOrDefault => HasMinNumber ? MinNumber : 1;
    public long MaxNumberOrDefault => HasMaxNumber ? MaxNumber : 1;
}
