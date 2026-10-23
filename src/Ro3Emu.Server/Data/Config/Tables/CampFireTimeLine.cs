using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's CampFireTimeLine (data/config/CampFireTimeLine.json).</summary>
[ConfigTable("CampFireTimeLine")]
public readonly partial record struct CampFireTimeLine(JsonElement Row)
{
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iTimeLine")] public partial long TimeLine { get; }
}
