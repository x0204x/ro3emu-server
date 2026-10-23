using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's NewbieTitlesConfig (data/config/NewbieTitlesConfig.json).</summary>
[ConfigTable("NewbieTitlesConfig")]
public readonly partial record struct NewbieTitlesConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iLev")] public partial long Lev { get; }
    [ConfigField("_iSubTaskId")] public partial long[] SubTaskId { get; }
    [ConfigField("_iTaskID")] public partial long TaskId { get; }
}
