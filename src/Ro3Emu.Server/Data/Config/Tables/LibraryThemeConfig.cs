using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's LibraryThemeConfig (data/config/LibraryThemeConfig.json).</summary>
[ConfigTable("LibraryThemeConfig")]
public readonly partial record struct LibraryThemeConfig(JsonElement Row)
{
    [ConfigField("_iUnlockCondition")] public partial long UnlockCondition { get; }
}
