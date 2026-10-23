using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's MailConfig (data/config/MailConfig.json).</summary>
[ConfigTable("MailConfig")]
public readonly partial record struct MailConfig(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iContent")] public partial JsonElement? Content { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iTimerMethod")] public partial long TimerMethod { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kTimes")] public partial long Times { get; }
}
