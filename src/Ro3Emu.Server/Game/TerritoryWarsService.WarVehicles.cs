using System.Collections.Concurrent;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The war vehicles of a siege or a type-18 territory battle (docs/systems/territory-wars.md "War vehicles"): their
/// units, raising and removal, and who may board them. The seats themselves (boarding, getting off, seat changes, the
/// driver's walk, the seat's buttons and skills) are VehicleSeats, shared with Mock Battle's Madogear.
/// </summary>
public sealed partial class TerritoryWarsService
{
    /// <summary>Vehicle unit ids: next to the cannons', below 2^53.</summary>
    private const long VehicleUidBase = (1L << 49) | (1L << 48);
    private const int VehicleStatusType = VehicleSeats.StatusType;
    /// <summary>LC_ConditionDefine CheckGVGPermission.</summary>
    private const long GvgPermission = 2202;
    private long _vehicleSerial;
    private long _vehicleBuffGuid = (1L << 56) | (1L << 54);
    private readonly ConcurrentDictionary<long, (VehicleUnit Unit, InstanceService.Room Room)> _vehicles = new();
    private VehicleSeats? _vehicleSeats;
    private VehicleSeats VehicleRiders => _vehicleSeats ??= new VehicleSeats(_data, _world, Send);

    /// <summary>A war vehicle: its owner (the member who used the item), camp, guild, seats and the gunners' aim. A
    /// monster unit for the combat, as a cannon.</summary>
    internal sealed class VehicleUnit : SeatedVehicle
    {
        public required long Guild { get; init; }

        public override EntityBase Entity()
        {
            var entity = SeatedEntity();
            AddMachineHealth(entity, this);
            AddMachineGuild(entity, Guild);
            return entity;
        }
    }

    /// <summary>
    /// GuildService.WarMachines: the vehicle NPC beside its owner (inferred: 2 m in front, as GVG_S0_BuildMecha is a
    /// server script the client does not hold), with NPCConfig _kBornEventBuff standing on it.
    /// </summary>
    private async Task<long> RaiseVehicleAsync(GameSession owner, int npcId)
    {
        if (WarRoomOf(owner) is not { } battle || owner.Role is not { } role) return 0;
        foreach (var (uid, old) in _vehicles)
            if (old.Unit.Removed || old.Room.Closed) _vehicles.TryRemove(uid, out _);
        float rad = owner.Face * MathF.PI / 180f;
        var spot = new MapPoint(owner.Position.X + 2 * MathF.Sin(rad), owner.Position.Y, owner.Position.Z + 2 * MathF.Cos(rad), owner.Face);
        if (_maps.Map(owner.SceneEntry) is { } map) spot = map.Grounded(spot);
        if (NewVehicle(npcId, battle.Room, spot, role.Id, CampOf(owner), battle.Guild) is not { } unit
            || !await _instances.AddPropAsync(battle.Room, unit)) return 0;
        _vehicles[unit.Uid] = (unit, battle.Room);
        return unit.Uid;
    }

    /// <summary>A vehicle NPC of <paramref name="room"/> at <paramref name="spot"/>, of its owner (0: none), camp and guild,
    /// with NPCConfig _kBornEventBuff standing on it; null for an NPC that is no vehicle with seats.</summary>
    private VehicleUnit? NewVehicle(int npcId, InstanceService.Room room, MapPoint spot, long owner, long camp, long guild)
    {
        if (NPCConfig.Get(_data, npcId) is not { NPCSubType: 103 } npc
            || VehicleConfig.Get(_data, npc.SubTypeValue) is not { Seats: { Length: > 0 } seats }
            || _monsters.MachineTemplate(npcId) is not { } template) return null;
        var (group, home) = MachineHome(npcId, spot);
        var unit = new VehicleUnit
        {
            Uid = VehicleUidBase | Interlocked.Increment(ref _vehicleSerial), SceneEntry = room.SceneEntry, InstanceId = room.Id,
            X = spot.X, Y = spot.Y, Z = spot.Z, Face = spot.Face, NpcId = npcId, Owner = owner, Camp = camp, Guild = guild,
            CampOverride = (int)camp, Name = npc.Name is [var nameId, ..] ? _data.Text(nameId) : "",
            Template = template, Group = group, Home = home, Hp = template.MaxHp, BornTimeMs = MonsterService.NowMs,
            SeatRows = seats, Seats = new GameSession?[seats.Length],
            SeatTypes = [.. seats.Select(seat => VehicleSeatConfig.Get(_data, seat)?.Type ?? 0)],
            // As a cannon: damage stays until the repair zone mends it, and a hit does not lock or leash it.
            KeepsDamage = true, StaysOutOfBattle = true,
        };
        foreach (long[] entry in npc.BornEventBuff)
            if (entry is [var buff, ..] && buff > 0 && _data.Row("BuffConfig", buff) != null)
                unit.Buffs.Add(new BuffInfo
                {
                    BuffGuid = Interlocked.Increment(ref _vehicleBuffGuid), ConfigId = (int)buff, PileCount = 1, SourceId = unit.Uid,
                });
        return unit;
    }

    private bool VehicleStanding(long uid) => _vehicles.TryGetValue(uid, out var v) && !v.Unit.Removed && !v.Room.Closed;

    /// <summary>The vehicle is destroyed (replaced by its owner's next one): everyone on it gets off first.</summary>
    private async Task<bool> RemoveVehicleAsync(long uid)
    {
        if (!_vehicles.TryRemove(uid, out var v)) return false;
        foreach (var rider in v.Unit.Seats.OfType<GameSession>().ToList()) await GetOffAsync(rider, interrupt: true);
        await _instances.RemovePropAsync(v.Room, v.Unit);
        return true;
    }

    /// <summary>32700 on a vehicle, and 32703 / 32708 / 31074 of its riders; anything else goes on to its usual owner.</summary>
    public async Task<bool> VehicleSharedAsync(GameSession s, uint id, ReadOnlyMemory<byte> body)
    {
        if (id == SceneService.NpcInteraction)
        {
            var req = NpcInteractionReq.Parser.ParseFrom(body.Span);
            if (!_vehicles.TryGetValue(req.NpcUid, out var vehicle)) return await RingInteractAsync(s, req);
            int code = await VehicleInteractAsync(s, vehicle.Unit, vehicle.Room, req);
            if (code != 0) await Send(s, id, new NpcInteractionRsp { Result = code, Req = req });
            return true;
        }
        return await VehicleRiders.RiderMessageAsync(s, id, body);
    }

    private async Task<int> VehicleInteractAsync(GameSession s, VehicleUnit unit, InstanceService.Room room, NpcInteractionReq req)
    {
        if (VehicleRiders.TryGetRider(s, out var rider))
        {
            // The seat's exit action (VehicleSeatConfig _iExitNpcAction 4900025: ActionConfig 5004 QuitStatus) on its vehicle.
            if (!VehicleRiders.IsExit(rider, unit, req.InteractionId)) return ErrorCodes.NpcParameter;
            await Send(s, SceneService.NpcInteraction, new NpcInteractionRsp { Result = 0, Req = req });
            await GetOffAsync(s, interrupt: false);
            return 0;
        }
        int code = CanBoard(s, unit, room, req, out int seat);
        if (code != 0) return code;
        await Send(s, SceneService.NpcInteraction, new NpcInteractionRsp { Result = 0, Req = req });
        await VehicleRiders.BoardAsync(s, unit, seat, req.InteractionId);
        return 0;
    }

    /// <summary>
    /// The seat interaction of a living vehicle of the player's battle (VehicleSeats.SeatFor: the vehicle state, a free
    /// seat, both conditions). Not while operating a cannon or a summon platform.
    /// </summary>
    private int CanBoard(GameSession s, VehicleUnit unit, InstanceService.Room room, NpcInteractionReq req, out int seat)
    {
        seat = -1;
        if (s.Role == null || s.Dead || s.SceneLoading || WarRoomOf(s)?.Room != room || unit.Removed || room.Closed)
            return ErrorCodes.NpcMissing;
        int code = VehicleRiders.SeatFor(s, unit, req, (p, v) => OwnSide(p, v.Camp, ((VehicleUnit)v).Guild),
            (type, arg) => type == GvgPermission ? (long)_guilds.ToolOfRole(s) == arg : null, out seat);
        return code == 0 && (_operators.ContainsKey(s) || _activators.ContainsKey(s)) ? ErrorCodes.NpcParameter : code;
    }

    /// <summary>Gets off the rider's seat (VehicleSeats.GetOffAsync).</summary>
    private Task GetOffAsync(GameSession s, bool interrupt) => VehicleRiders.GetOffAsync(s, interrupt);

    private Task VehicleLeavingAsync(GameSession s) => GetOffAsync(s, interrupt: true);

    private Task VehicleDiedAsync(GameSession s) => GetOffAsync(s, interrupt: true);

    private Task VehicleRiderSeenAsync(GameSession viewer, GameSession subject) => VehicleRiders.RiderSeenAsync(viewer, subject);

    /// <summary>After a driver's 31082 moved the player: the walk is the vehicle's (VehicleSeats.MovedAsync).</summary>
    public Task VehicleMovedAsync(GameSession s, WalkReq walk) => VehicleRiders.MovedAsync(s, walk);

    /// <summary>CombatService.GrantedSkill: the skills of the rider's seat (VehicleSeats.SeatSkill).</summary>
    private bool SeatSkill(GameSession s, int skill) => VehicleRiders.SeatSkill(s, skill);
}
