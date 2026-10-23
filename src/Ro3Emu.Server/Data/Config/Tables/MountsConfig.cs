using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's MountsConfig (data/config/MountsConfig.json).</summary>
[ConfigTable("MountsConfig")]
public readonly partial record struct MountsConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kAttr")] public partial long[][] Attr { get; }
    [ConfigField("_kBindBuff")] public partial long[] BindBuff { get; }
    [ConfigField("_kJob")] public partial long[] Job { get; }
}
