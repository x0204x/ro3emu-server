using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GameplayStatusButtonConfig (data/config/GameplayStatusButtonConfig.json).</summary>
[ConfigTable("GameplayStatusButtonConfig")]
public readonly partial record struct GameplayStatusButtonConfig(JsonElement Row)
{
    [ConfigField("_iButtonFuncType")] public partial long ButtonFuncType { get; }
    [ConfigField("_kAction")] public partial JsonElement? Action { get; }
}
