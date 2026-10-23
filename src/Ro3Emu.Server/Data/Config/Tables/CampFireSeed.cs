using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's CampFireSeed (data/config/CampFireSeed.json).</summary>
[ConfigTable("CampFireSeed")]
public readonly partial record struct CampFireSeed(JsonElement Row)
{
    [ConfigField("_iExp")] public partial long Exp { get; }
    [ConfigField("_iId")] public partial long Id { get; }
}
