using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's CardConfig (data/config/CardConfig.json).</summary>
[ConfigTable("CardConfig")]
public readonly partial record struct CardConfig(JsonElement Row)
{
    [ConfigField("_iCardID")] public partial long CardId { get; }
    [ConfigField("_iFlashPower")] public partial long FlashPower { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
    [ConfigField("_iPower")] public partial long Power { get; }
    [ConfigField("_iTier")] public partial long Tier { get; }
    [ConfigField("_kBasicAttribute")] public partial long[][] BasicAttribute { get; }
    [ConfigField("_kCost")] public partial long[][] Cost { get; }
    [ConfigField("_kFlashAttribute")] public partial long[] FlashAttribute { get; }
    [ConfigField("_kSkillAttribute")] public partial long[] SkillAttribute { get; }
}
