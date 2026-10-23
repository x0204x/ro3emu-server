using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PlotInterpretation (data/config/PlotInterpretation.json).</summary>
[ConfigTable("PlotInterpretation")]
public readonly partial record struct PlotInterpretation(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iType")] public partial long Type { get; }
}
