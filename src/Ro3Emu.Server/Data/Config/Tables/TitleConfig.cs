using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TitleConfig (data/config/TitleConfig.json).</summary>
[ConfigTable("TitleConfig")]
public readonly partial record struct TitleConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iPower")] public partial long Power { get; }
    [ConfigField("_iPowerAdd")] public partial long PowerAdd { get; }
    [ConfigField("_kAttr")] public partial long[][] Attr { get; }
}
