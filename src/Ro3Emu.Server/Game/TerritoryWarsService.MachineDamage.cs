using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The war machines in combat (docs/systems/territory-wars.md "Machine HP and destruction"). A cannon or a vehicle is an
/// NPC with an HP bar (properties 101 and 2000, its MonsterService.MachineTemplate), so the other side's attacks, skills
/// and cannon shots damage it through the monster pipeline and it dies as a monster does (the other camp in a siege, the
/// other guilds in a territory battle: TerritoryWarsService.CanHitObjective). A destroyed machine puts
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

    /// <summary>
    /// (inferred) The battle of <paramref name="room"/> settled: its standing cannons and vehicles leave with whoever is on
    /// them, and their owners' slots show nothing in use (32285, 32286), as when they are destroyed. The machines only
    /// serve the battle; the client's machine UI (the battle bag, the Instrument button) belongs to its dungeon's widgets.
    /// </summary>
    private async Task EndWarMachinesAsync(InstanceService.Room room)
    {
        foreach (var (uid, cannon) in _cannons.Where(p => p.Value.Room == room).ToList())
        {
            if (!_cannons.TryRemove(uid, out _)) continue;
            await EjectCannonOperatorAsync(cannon.Unit);
            await _instances.RemovePropAsync(room, cannon.Unit);
            await _guilds.MachineGoneAsync(cannon.Unit.Builder, uid);
        }
        foreach (var (uid, vehicle) in _vehicles.Where(p => p.Value.Room == room).ToList())
        {
            if (!_vehicles.TryRemove(uid, out _)) continue;
            foreach (var rider in vehicle.Unit.Seats.OfType<GameSession>().ToList()) await GetOffAsync(rider, interrupt: true);
            await EndRepairAsync(vehicle.Unit);
            await _instances.RemovePropAsync(room, vehicle.Unit);
            await _guilds.MachineGoneAsync(vehicle.Unit.Owner, uid);
        }
    }

    /// <summary>SiegeSettled, per loaded participant: the settled siege's machines end once (the first call finds them).</summary>
    private void SiegeMachinesSettled(long role, long score, bool won)
    {
        if (_sessions.All().FirstOrDefault(s => s.Role?.Id == role)?.SiegeBattle is { } battle)
            _loop.PostLegacy(() => EndWarMachinesAsync(battle.Room));
    }

    /// <summary>The record of the member fighting in the battle of <paramref name="m"/>'s room, or null.</summary>
    private Participant? MachineFighter(GameSession s, MonsterUnit m)
    {
        try
        {
            if (s.Role == null) return null;
            if (s.SiegeBattle is { } siege && m.InstanceId == siege.Room.Id && SiegeActive(siege) && SiegeMember(s, siege))
                return siege.State.Players[s.Role.Id];
            if (s.TerritoryBattle is { } battle && m.InstanceId == battle.Room.Id && Active(battle) && Member(s, battle))
                return battle.State.Players[s.Role.Id];
        }
        catch (InvalidOperationException) { }
        catch (KeyNotFoundException) { }
        return null;
    }

    /// <summary>
    /// CombatService.EventDamageRecorded: damage to a war machine counts as the hitter's MachineryDamage (statistics
    /// 1021003) besides their damage. (inferred) All damage dealt to the other side's cannons and vehicles, as the
    /// counter's name says; the client only displays it.
    /// </summary>
    private async Task MachineDamageRecordedAsync(GameSession s, MonsterUnit m, long amount)
    {
        if (amount <= 0 || MachineGuild(m) == null || MachineFighter(s, m) == null) return;
        using var reservation = await EnterGateAsync("territory/machine damage", s);
        using var stagedTerritoryState = StageState();
        try
        {
            if (MachineFighter(s, m) is not { } p) return;
            p.MachineDamage += amount;
            await SaveAsync(state);
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
    }

    /// <summary>CombatService.Killed: a destroyed war machine counts as its last hitter's DestroyMachinery (statistics
    /// 1021002; inferred: the last hit, as the battle's other kill counters).</summary>
    private async Task MachineKilledAsync(GameSession s, MonsterUnit m)
    {
        if (MachineGuild(m) == null || MachineFighter(s, m) == null) return;
        using var reservation = await EnterGateAsync("territory/machine kill", s);
        using var stagedTerritoryState = StageState();
        try
        {
            if (MachineFighter(s, m) is not { } p) return;
            p.MachineKills++;
            await SaveAsync(state);
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
    }
}
