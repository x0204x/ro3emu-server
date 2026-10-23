using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's FunctionPreviewConfig (data/config/FunctionPreviewConfig.json).</summary>
[ConfigTable("FunctionPreviewConfig")]
public readonly partial record struct FunctionPreviewConfig(JsonElement Row)
{
    [ConfigField("_iAwardID")] public partial long AwardId { get; }
    [ConfigField("_iFunction")] public partial long Function { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iLv")] public partial long Lv { get; }
}
