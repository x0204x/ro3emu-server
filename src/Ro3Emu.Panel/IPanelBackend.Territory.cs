namespace Ro3Emu.Panel;

/// <summary>
/// The territory war and siege map: the zones of GVGWarZoneConfig with their owning guilds, the war's schedule
/// (TerritoryWarStageConfig), the battles running now with their scores, and the siege cities (SiegeConfig) with their
/// defenders, attackers and the state of a siege while it runs. Read from TerritoryWarsService on the game loop.
/// </summary>
public partial interface IPanelBackend
{
    Task<PanelTerritoryMap> TerritoryAsync();
}

/// <param name="Kind">What the stage is: "Escort", "Auction", "Battle preparation", "Battle", "Off season" and so on.</param>
/// <param name="Round">1 the strongholds, 2 the regions, 3 the capital; 0 outside a round.</param>
public sealed record PanelWarStage(int Id, int Round, string Name, string Kind, DateTimeOffset Start, DateTimeOffset End);

public sealed record PanelGuildScore(PanelGuildRef Guild, long Score);

/// <param name="Players">Characters in the battle's room now.</param>
public sealed record PanelZoneBattle(DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<PanelGuildScore> Scores, int Players);

/// <param name="Parent">The zone it belongs to (GVGWarZoneConfig _iBelongID); 0 for the capital.</param>
/// <param name="Kind">"Capital", "Region", "Stronghold" or "Field".</param>
/// <param name="Image">The zone map's outline picture.</param>
/// <param name="Battle">The battle for it running now; null when none runs.</param>
public sealed record PanelZone(int Id, int Parent, string Kind, string Name, long MapId, string Image, PanelGuildRef? Owner, PanelZoneBattle? Battle);

/// <param name="Zone">The territory zone whose owner defends the city; 0 when no zone holds it.</param>
/// <param name="State">"Not open", "Fighting" or "Over".</param>
/// <param name="Stage">The running siege's stage ("Prepare" to "Crystal"); empty when none runs.</param>
public sealed record PanelCastle(int City, string Name, string Kind, int Zone, PanelGuildRef? Defender, PanelGuildRef? Attacker, string State,
    string Stage, PanelGuildRef? Winner);

/// <param name="Running">The siege window is open now.</param>
public sealed record PanelSiegeWindow(DateTimeOffset Start, DateTimeOffset End, bool Running);

/// <param name="Stage">The stage in force now.</param>
/// <param name="Next">The next stage of another kind, or the next battle when one is scheduled; null at the end.</param>
/// <param name="Battles">The war's battle stages, past and coming.</param>
/// <param name="Siege">The current or next siege window; null when none is scheduled.</param>
public sealed record PanelTerritoryMap(DateTimeOffset Now, PanelWarStage Stage, PanelWarStage? Next, IReadOnlyList<PanelWarStage> Battles,
    IReadOnlyList<PanelZone> Zones, IReadOnlyList<PanelCastle> Castles, PanelSiegeWindow? Siege);
