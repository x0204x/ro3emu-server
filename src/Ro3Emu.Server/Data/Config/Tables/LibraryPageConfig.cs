using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's LibraryPageConfig (data/config/LibraryPageConfig.json).</summary>
[ConfigTable("LibraryPageConfig")]
public readonly partial record struct LibraryPageConfig(JsonElement Row)
{
    [ConfigField("_kPageItem")] public partial long PageItem { get; }
}
