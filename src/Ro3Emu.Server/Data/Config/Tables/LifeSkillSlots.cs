using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's LifeSkillSlots (data/config/LifeSkillSlots.json).</summary>
[ConfigTable("LifeSkillSlots")]
public readonly partial record struct LifeSkillSlots(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iJobId")] public partial long JobId { get; }
    [ConfigField("_iSort")] public partial long Sort { get; }
    [ConfigField("_kRefreshcost")] public partial long[][] Refreshcost { get; }
    [ConfigField("_kSkillpool")] public partial long[][] Skillpool { get; }
}
