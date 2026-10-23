using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's ExploreNodeConfig (data/config/ExploreNodeConfig.json).</summary>
[ConfigTable("ExploreNodeConfig")]
public readonly partial record struct ExploreNodeConfig(JsonElement Row)
{
    [ConfigField("_kPiecesID")] public partial long[] PiecesId { get; }
}
