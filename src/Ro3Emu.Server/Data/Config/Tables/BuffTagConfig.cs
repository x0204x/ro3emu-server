using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's BuffTagConfig (data/config/BuffTagConfig.json).</summary>
[ConfigTable("BuffTagConfig")]
public readonly partial record struct BuffTagConfig(JsonElement Row)
{
    [ConfigField("_iAtkAttrID")] public partial long AtkAttrId { get; }
    [ConfigField("_iDefAttrID")] public partial long DefAttrId { get; }
    [ConfigField("_iFormula")] public partial long Formula { get; }
    [ConfigField("_iID")] public partial long Id { get; }
}
