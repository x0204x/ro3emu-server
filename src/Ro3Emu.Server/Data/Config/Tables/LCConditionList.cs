using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's LC_ConditionList (data/config/LC_ConditionList.json).</summary>
[ConfigTable("LC_ConditionList")]
public readonly partial record struct LCConditionList(JsonElement Row)
{
    /// <summary>enum_NodeType of the node (LC_ConditionDefine): 1 AND, 2 OR of its sub conditions, 3 one condition.</summary>
    [ConfigField("m_eType", Optional = true)] public partial long Type { get; }
    [ConfigField("m_kSubConditionList", Optional = true)] public partial JsonElement? SubConditionList { get; }

    /// <summary>Does this condition hold a sub condition [kind, value] in its m_kData (a bow skill's [2001, 11])?</summary>
    public bool HasSubCondition(long kind, long value) => SubConditionList is { ValueKind: JsonValueKind.Array } list && list.EnumerateArray().Any(sub =>
        sub.TryGetProperty("m_kData", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() >= 2
            && data[0].TryGetInt64(out long first) && first == kind && data[1].TryGetInt64(out long second) && second == value);
    [ConfigField("m_bInverse", Optional = true)] public partial bool Inverse { get; }
    [ConfigField("m_kData", Optional = true)] public partial long[] Data { get; }
}
