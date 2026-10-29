using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The war machines in combat (docs/systems/territory-wars.md "Machine HP and destruction"). A cannon or a vehicle is an
/// NPC with an HP bar (properties 101 and 2000, its MonsterService.MachineTemplate), so the other camp's attacks, skills
/// and cannon shots damage it through the monster pipeline and it dies as a monster does. A destroyed machine puts
/// whoever is on it back on foot, its owner's Tactics slot shows nothing in use any more (32285, 32286) and its wreck
/// leaves after NPCConfig _iBodyRemainTime.
/// </summary>
public sealed partial class TerritoryWarsService
{
    /// <summary>The one-point spawn group of a raised machine (no respawn), as MonsterService.SpawnSingleAsync makes.</summary>
    private static (SpawnGroup Group, SpawnPoint Home) MachineHome(int npcId, MapPoint at)
    {
        var point = new SpawnPoint(-1, at.X, at.Y, at.Z, at.Face, npcId);
        return (new SpawnGroup(-1, false, 1, 0, [], [point]), point);
    }

    /// <summary>Max HP, current HP and level, as a monster's entity carries them.</summary>
    private static void AddMachineHealth(EntityBase entity, MonsterUnit unit)
    {
        foreach (var (idx, value) in new (long, long)[]
                 { (101, unit.Template.MaxHp), (2000, unit.Dead ? 0 : unit.Hp), (2002, unit.Template.Level) })
            entity.Property.Add(new AoIProperty { Idx = idx, Data = value, BaseData = value });
    }

    /// <summary>The camp of a cannon or vehicle, or null for any other unit.</summary>
    private static long? MachineCamp(MonsterUnit m) => m switch
    {
        CannonUnit cannon => cannon.Camp,
        VehicleUnit vehicle => vehicle.Camp,
        _ => null,
    };

    /// <summary>
    /// A member of the running siege hits a machine of the other camp only (LC_CommonCondition.CheckCamp compares
    /// EntityCamp: the machines of the own camp are allies).
    /// </summary>
    private bool MachineHittable(GameSession s, SiegeRun battle, long camp) =>
        SiegeActive(battle) && SiegeMember(s, battle) && CampOf(s) != camp;

    /// <summary>CombatService.AfterLethalPublication: a cannon or vehicle was destroyed.</summary>
    private async Task MachineDestroyedAsync(MonsterUnit m)
    {
        switch (m)
        {
            case CannonUnit cannon when _cannons.TryRemove(cannon.Uid, out var standing):
                await EjectCannonOperatorAsync(cannon);
                await _guilds.MachineGoneAsync(cannon.Builder, cannon.Uid);
                RemoveWreck(standing.Room, cannon);
                break;
            case VehicleUnit vehicle when _vehicles.TryRemove(vehicle.Uid, out var standing):
                foreach (var rider in vehicle.Seats.OfType<GameSession>().ToList()) await GetOffAsync(rider, interrupt: true);
                await EndRepairAsync(vehicle);
                await _guilds.MachineGoneAsync(vehicle.Owner, vehicle.Uid);
                RemoveWreck(standing.Room, vehicle);
                break;
        }
    }

    /// <summary>The wreck stays for NPCConfig _iBodyRemainTime (MonsterTemplate.BodyRemainMs) and leaves the room.</summary>
    private void RemoveWreck(InstanceService.Room room, MonsterUnit unit) =>
        _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(unit.Template.BodyRemainMs + 200), () => _instances.RemovePropAsync(room, unit));
}
