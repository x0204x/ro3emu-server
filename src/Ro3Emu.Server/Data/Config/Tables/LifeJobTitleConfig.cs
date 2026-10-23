using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's LifeJobTitleConfig (data/config/LifeJobTitleConfig.json).</summary>
[ConfigTable("LifeJobTitleConfig")]
public readonly partial record struct LifeJobTitleConfig(JsonElement Row)
{
    [ConfigField("_iCondition")] public partial long Condition { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iJobId")] public partial long JobId { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
    [ConfigField("_iSlotId")] public partial long SlotId { get; }
    [ConfigField("_kUnlockcost")] public partial long[][] Unlockcost { get; }
}
