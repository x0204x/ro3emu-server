using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// The client's InteractPerformance (data/config/InteractPerformance.json, read by no client Lua): the gameplay status a
/// player shows while a Bonfire Party interaction plays (CampFireFuel._iInteractPerformId), and for how long.
/// </summary>
[ConfigTable("InteractPerformance")]
public readonly partial record struct InteractPerformance(JsonElement Row)
{
    [ConfigField("_iId")] public partial long Id { get; }
    /// <summary>GameplayStatusConfig id (6 collect seeds, 7 throw seeds, 8 add accelerant, 9 treasure hunt).</summary>
    [ConfigField("_iGameStatusId")] public partial long GameStatusId { get; }
    [ConfigField("_iAnimId")] public partial long AnimId { get; }
    [ConfigField("_iAnimTime")] public partial long AnimTime { get; }
}
