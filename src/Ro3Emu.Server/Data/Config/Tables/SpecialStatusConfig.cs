using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SpecialStatusConfig (data/config/SpecialStatusConfig.json).</summary>
[ConfigTable("SpecialStatusConfig")]
public readonly partial record struct SpecialStatusConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iStaggerType")] public partial long StaggerType { get; }
    [ConfigField("_kForbidden")] public partial long[] Forbidden { get; }
}
