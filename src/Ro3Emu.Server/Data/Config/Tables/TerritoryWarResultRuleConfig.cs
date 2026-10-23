using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TerritoryWarResultRuleConfig (data/config/TerritoryWarResultRuleConfig.json).</summary>
[ConfigTable("TerritoryWarResultRuleConfig")]
public readonly partial record struct TerritoryWarResultRuleConfig(JsonElement Row)
{
    [ConfigField("_iLimitCount")] public partial long LimitCount { get; }
    [ConfigField("_iRuleID")] public partial long RuleId { get; }
    [ConfigField("_iRuleType")] public partial long RuleType { get; }
}
