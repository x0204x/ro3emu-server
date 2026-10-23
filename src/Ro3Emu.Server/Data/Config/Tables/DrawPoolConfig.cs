using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's DrawPoolConfig (data/config/DrawPoolConfig.json).</summary>
[ConfigTable("DrawPoolConfig")]
public readonly partial record struct DrawPoolConfig(JsonElement Row)
{
    [ConfigField("_iGroupID")] public partial long GroupId { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iWeight")] public partial long Weight { get; }
    [ConfigField("_kItem")] public partial long[][] Item { get; }
}
