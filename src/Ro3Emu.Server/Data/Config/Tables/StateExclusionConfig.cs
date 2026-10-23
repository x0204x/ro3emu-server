using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's StateExclusionConfig (data/config/StateExclusionConfig.json).</summary>
[ConfigTable("StateExclusionConfig")]
public readonly partial record struct StateExclusionConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iSpecialStatusID")] public partial long SpecialStatusId { get; }
}
