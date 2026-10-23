using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's CampFireFuel (data/config/CampFireFuel.json).</summary>
[ConfigTable("CampFireFuel")]
public readonly partial record struct CampFireFuel(JsonElement Row)
{
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iItemId")] public partial long ItemId { get; }
    [ConfigField("_iQuality")] public partial long Quality { get; }
}
