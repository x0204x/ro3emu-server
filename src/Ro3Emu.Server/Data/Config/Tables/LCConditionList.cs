using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's LC_ConditionList (data/config/LC_ConditionList.json).</summary>
[ConfigTable("LC_ConditionList")]
public readonly partial record struct LCConditionList(JsonElement Row)
{
    [ConfigField("m_kSubConditionList", Optional = true)] public partial JsonElement? SubConditionList { get; }
    [ConfigField("m_bInverse", Optional = true)] public partial bool Inverse { get; }
    [ConfigField("m_kData", Optional = true)] public partial long[] Data { get; }
}
