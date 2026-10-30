using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The siege's fixed war vehicles (docs/systems/territory-wars.md "Fixed vehicles"): NpcLevelPointRespawnConfig places
/// the Steel, Mithril and Adamantine Madogears 4901004-4901006 (camp 1, the attackers) at points 301, 251 and 250 and
/// 4901007-4901009 (camp 2, the defenders) at 253, 302 and 303. They are the warehouse's vehicles (NPCConfig _iNPCSubType
/// 103, VehicleConfig 2, interactions 4900022 seat and 4900025 exit, the same units 4900401-4900403) without an item:
/// they stand at their points from the start, and the members of their camp board them as a vehicle raised from the war
/// bag (TerritoryWarsService.WarVehicles.cs): the Vehicle permission, a free seat, the same camp. The other camp destroys
/// them; NPCConfig _iNeedNotifyGlobalData 1 puts them in 30115 at their points.
/// </summary>
public sealed partial class TerritoryWarsService
{
    private static readonly HashSet<long> SiegeFixedVehicleNpcs = [4901004, 4901005, 4901006, 4901007, 4901008, 4901009];

    /// <summary>(point, npc) of the fixed vehicles; read once.</summary>
    private (int Point, int Npc)[]? _siegeVehicleRows;

    private (int Point, int Npc)[] SiegeVehicleRows() => _siegeVehicleRows ??= [.. NpcLevelPointRespawnConfig.All(_data)
        .Where(r => r.Mapid == SiegeScene && SiegeFixedVehicleNpcs.Contains(r.Npcid))
        .Select(r => ((int)r.Respawnpointid, (int)r.Npcid)).OrderBy(r => r.Item1)];

    /// <summary>
    /// The fixed vehicles the room lacks, once per room: owned by nobody (no Tactics slot names them), of their NPCConfig
    /// camp and that camp's guild. (inferred) A destroyed one stays destroyed for the siege (UIModule_NpcRefreshDuration
    /// names none of their points) and a damaged one keeps its HP, like the castle's objects.
    /// </summary>
    private async Task SiegeSpawnVehiclesAsync(SiegeRun battle)
    {
        if (battle.Room.Closed || battle.State.Settled || _maps.Map(SiegeScene) is not { } map) return;
        foreach (var (point, npcId) in SiegeVehicleRows())
        {
            if (battle.Objects.ContainsKey(point) || battle.State.Destroyed.Contains(point)
                || !map.Points.TryGetValue(point, out var at) || NPCConfig.Get(_data, npcId) is not { Camp: 1 or 2 } npc) continue;
            long guild = npc.Camp == 1 ? battle.State.Attack : battle.State.Defense;
            if (NewVehicle(npcId, battle.Room, map.Grounded(at), owner: 0, npc.Camp, guild) is not { } unit) continue;
            if (battle.State.ObjectHp.TryGetValue(point, out long hp) && hp > 0) unit.Hp = Math.Min(unit.Hp, hp);
            if (!await _instances.AddPropAsync(battle.Room, unit)) return;
            _vehicles[unit.Uid] = (unit, battle.Room);
            battle.Objects[point] = unit;
        }
    }
}
