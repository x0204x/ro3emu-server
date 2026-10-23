using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's NPCInteractConfig (data/config/NPCInteractConfig.json).</summary>
[ConfigTable("NPCInteractConfig")]
public readonly partial record struct NPCInteractConfig(JsonElement Row)
{
    [ConfigField("_iButtenCond")] public partial long ButtenCond { get; }
    [ConfigField("_iCondition")] public partial long Condition { get; }
    [ConfigField("_iEXECAction")] public partial JsonElement? EXECAction { get; }
    [ConfigField("_iNPCCondition")] public partial long NPCCondition { get; }
    [ConfigField("_iStatusID")] public partial long StatusId { get; }
    [ConfigField("_iStatusLocation")] public partial long StatusLocation { get; }
}
