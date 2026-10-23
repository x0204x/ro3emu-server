using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PetSetLimitConfig (data/config/PetSetLimitConfig.json).</summary>
[ConfigTable("PetSetLimitConfig")]
public readonly partial record struct PetSetLimitConfig(JsonElement Row)
{
    [ConfigField("_iAssistAttrAddition")] public partial long AssistAttrAddition { get; }
    [ConfigField("_iCondition")] public partial long Condition { get; }
    [ConfigField("_iCondition1")] public partial long Condition1 { get; }
    [ConfigField("_iCondition2")] public partial long Condition2 { get; }
    [ConfigField("_iFightAttrAddition")] public partial long FightAttrAddition { get; }
    [ConfigField("_iID")] public partial long Id { get; }
}
