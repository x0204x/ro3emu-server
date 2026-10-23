using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's CampFireShow (data/config/CampFireShow.json).</summary>
[ConfigTable("CampFireShow")]
public readonly partial record struct CampFireShow(JsonElement Row)
{
    [ConfigField("_iShowId")] public partial long ShowId { get; }
}
