using System.Collections.Concurrent;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The war vehicles of a siege or a type-18 territory battle (docs/systems/territory-wars.md "War vehicles"). A vehicle is
/// an NPC of NPCConfig _iNPCSubType 103 (enum_NpcSubType.Vehicle), so the client gives it LC_VehicleComponent with the
/// seats of its VehicleConfig (_iSubTypeValue -> _kSeats, VehicleSeatConfig rows by 0-based index, LC_VehicleComponent.GetSeatConfig). Seats are
/// taken through the NPC's interactions (4900022..24: NPCInteractConfig _iStatusID 3, GameplayStatusConfig type 3, seat
/// _iStatusLocation) and left through the seat's _iExitNpcAction (4900025, the QuitStatus of LC_VehicleServer). Who sits
/// where travels as AOI carry info (LC_VehicleHelper.ProcessVehicle): the passenger's be_carried {vehicle id, slot,
/// carry_type = the seat's enum_PassengerType} and the vehicle's all/add/remove_carries; the driver then controls the
/// vehicle (LC_PassengerComponent: ChangeControlUnit), so its 31082 walks are the vehicle's positions.
/// </summary>
public sealed partial class TerritoryWarsService
{
    /// <summary>Vehicle unit ids: next to the cannons', below 2^53.</summary>
    private const long VehicleUidBase = (1L << 49) | (1L << 48);
    private const int VehicleStatusType = 3;
    /// <summary>LC_ConditionDefine: VehicleHasEmptySeat, CheckTakeOnVehicleSeat, CheckGVGPermission.</summary>
    private const long VehicleHasEmptySeat = 12002, TakeOnVehicleSeat = 12003, GvgPermission = 2202;
    private long _vehicleSerial;
    private long _vehicleBuffGuid = (1L << 56) | (1L << 54);
    private readonly ConcurrentDictionary<long, (VehicleUnit Unit, InstanceService.Room Room)> _vehicles = new();
    private readonly Dictionary<GameSession, Rider> _riders = [];

    private sealed record Rider(VehicleUnit Unit, int Seat, long Interaction);

    /// <summary>A war vehicle: its owner (the member who used the item), camp, guild, seats and the gunners' aim. A
    /// monster unit for the combat, as a cannon.</summary>
    internal sealed class VehicleUnit : MonsterUnit
    {
        public required int NpcId { get; init; }
        public required long Owner { get; init; }
        public required long Camp { get; init; }
        public required long Guild { get; init; }
        public required string Name { get; init; }
        /// <summary>VehicleSeatConfig rows of the seats, in VehicleConfig _kSeats order.</summary>
        public required long[] SeatRows { get; init; }
        public required GameSession?[] Seats { get; init; }
        public List<BuffInfo> Buffs { get; } = [];
        public Dictionary<int, float> FireDirs { get; } = [];
        /// <summary>enum_PassengerType of each seat (VehicleSeatConfig _iType).</summary>
        public required long[] SeatTypes { get; init; }

        /// <summary>The vehicle's entry for the rider of <paramref name="seat"/> (id: the rider).</summary>
        public AoICarry Carry(int seat) => new()
        {
            Id = (ulong)(Seats[seat]?.Role?.Id ?? 0), CarrySlot = seat, CarryOffset = new Vec3(), CarryType = (int)SeatTypes[seat],
        };

        /// <summary>The rider's be_carried for <paramref name="seat"/> (id: this vehicle).</summary>
        public AoICarry Carrier(int seat) => new()
        {
            Id = (ulong)Uid, CarrySlot = seat, CarryOffset = new Vec3(), CarryType = (int)SeatTypes[seat],
        };

        public AoIVehicleDetail Detail()
        {
            var detail = new AoIVehicleDetail();
            detail.FireDirs.AddRange(FireDirs.Select(p => new AoIVehicleFireDirection { Slot = p.Key, FireDir = p.Value }));
            return detail;
        }

        public override EntityBase Entity()
        {
            var entity = new EntityBase
            {
                Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = 2, Name = Name, EntityCamp = Camp,
                Owner = Owner, Face = Face, State = Dead ? EntityState.Dead : EntityState.Alive,
                NotifyType = EntityBase.Types.NotifyType.All,
                MovementInfo = new MovementInfo
                {
                    Pos = Pos, Face = Face, ServerTime = MonsterService.NowMs, MoveType = MovementType.MoveTypeNone
                },
            };
            AddMachineHealth(entity, this);
            if (Buffs.Count > 0) entity.BuffData = new AoIBuffInfo { AddBuffs = { Buffs.Select(b => b.Clone()) } };
            var carries = new AoICarryInfo();
            for (int seat = 0; seat < Seats.Length; seat++)
                if (Seats[seat] != null) carries.AllCarries.Add(Carry(seat));
            if (carries.AllCarries.Count > 0) entity.CarryInfo = carries;
            if (FireDirs.Count > 0) entity.VehicleDetail = Detail();
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
        if (!_riders.TryGetValue(s, out var rider)) return false;
        switch (id)
        {
            case WorldInteractionService.Action:
                await Send(s, id, new UseInteractActionRes { Result = await RiderButtonAsync(s, rider, UseInteractActionReq.Parser.ParseFrom(body.Span)) });
                return true;
            case WorldInteractionService.SeatChange:
                await ChangeSeatAsync(s, rider, ChangeVehicleSeatReq.Parser.ParseFrom(body.Span));
                return true;
            case WorldInteractionService.VehicleSync:
                await SyncVehicleAsync(s, rider, SyncVehicleReq.Parser.ParseFrom(body.Span));
                return true;
            default: return false;
        }
    }

    private async Task<int> VehicleInteractAsync(GameSession s, VehicleUnit unit, InstanceService.Room room, NpcInteractionReq req)
    {
        if (_riders.TryGetValue(s, out var rider))
        {
            // The seat's exit action (VehicleSeatConfig _iExitNpcAction 4900025: ActionConfig 5004 QuitStatus) on its vehicle.
            if (rider.Unit != unit || req.InteractionId != VehicleSeatConfig.Get(_data, unit.SeatRows[rider.Seat])?.ExitNpcAction)
                return ErrorCodes.NpcParameter;
            await Send(s, SceneService.NpcInteraction, new NpcInteractionRsp { Result = 0, Req = req });
            await GetOffAsync(s, interrupt: false);
            return 0;
        }
        int code = CanBoard(s, unit, room, req, out int seat);
        if (code != 0) return code;
        await Send(s, SceneService.NpcInteraction, new NpcInteractionRsp { Result = 0, Req = req });
        await BoardAsync(s, unit, seat, req.InteractionId);
        return 0;
    }

    /// <summary>
    /// The seat interaction of a living vehicle of the player's battle, within NPCConfig _iInteractDistance: its
    /// GameplayStatusConfig is a vehicle state, its seat (_iStatusLocation, 1-based) is free and both its conditions
    /// hold (the interaction's: the seat taken by nobody and the Vehicle permission; the NPC's: an empty seat of that type
    /// on a vehicle of the player's side). Not while operating a cannon.
    /// </summary>
    private int CanBoard(GameSession s, VehicleUnit unit, InstanceService.Room room, NpcInteractionReq req, out int seat)
    {
        seat = -1;
        if (s.Role == null || s.Dead || s.SceneLoading || WarRoomOf(s)?.Room != room || unit.Removed || room.Closed)
            return ErrorCodes.NpcMissing;
        if (NPCConfig.Get(_data, unit.NpcId) is not { } npc || !npc.InteractId.Contains(req.InteractionId)
            || NPCInteractConfig.Get(_data, req.InteractionId) is not { } interaction
            || GameplayStatusConfig.Get(_data, interaction.StatusId) is not { StatusType: VehicleStatusType })
            return ErrorCodes.NpcParameter;
        if (MonsterService.Distance(s.Position.X, s.Position.Z, unit.X, unit.Z) > Math.Max(npc.InteractDistance, 3) + 1)
            return ErrorCodes.NpcTooFar;
        seat = (int)interaction.StatusLocation - 1;
        if (seat < 0 || seat >= unit.Seats.Length || unit.Seats[seat] != null || _operators.ContainsKey(s)
            || _activators.ContainsKey(s)) return ErrorCodes.NpcParameter;
        int index = seat;
        return VehicleCondition(s, unit, interaction.Condition, index) && VehicleCondition(s, unit, interaction.NPCCondition, index)
            ? 0 : ErrorCodes.NpcParameter;
    }

    private bool VehicleCondition(GameSession s, VehicleUnit unit, long conditionId, int seat)
    {
        if (conditionId == 0) return true;
        if (LCConditionList.Get(_data, conditionId) is not { } condition) return false;
        if (condition.SubConditionList is not { ValueKind: System.Text.Json.JsonValueKind.Array } list)
            return VehicleSubCondition(s, unit, condition, seat);
        return list.EnumerateArray().All(sub => VehicleSubCondition(s, unit, new LCConditionList(sub), seat));
    }

    /// <summary>
    /// LC_InteractCondition.CheckVehicleHasEmptySeat (12002: a living vehicle that is not an enemy, a seat of that
    /// enum_PassengerType free), CheckTakeOnVehicleSeat (12003: the player sits nowhere yet), CheckGVGPermission (2202:
    /// SiegePermissionSelfData eToolType) and LC_CommonCondition.CheckCamp (2200: the same EntityCamp, its argument unused);
    /// 12001 the vehicle state (7000030). Any other type is closed. (inferred) In a territory battle, where every camp is 1,
    /// "not an enemy" and "the same camp" are the same guild, as the client's relation there.
    /// </summary>
    private bool VehicleSubCondition(GameSession s, VehicleUnit unit, LCConditionList sub, int seat)
    {
        if (sub.Data is not [var type, var arg, ..]) return false;
        bool? yes = type switch
        {
            VehicleHasEmptySeat => OwnSide(s, unit.Camp, unit.Guild)
                && Enumerable.Range(0, unit.Seats.Length).Any(i => unit.SeatTypes[i] == arg && unit.Seats[i] == null),
            TakeOnVehicleSeat => !_riders.ContainsKey(s),
            GvgPermission => (long)_guilds.ToolOfRole(s) == arg,
            CheckCamp => OwnSide(s, unit.Camp, unit.Guild),
            InStatus => arg == VehicleStatusType && _riders.ContainsKey(s),
            _ => null,
        };
        return yes is { } value && value != sub.Inverse;
    }

    /// <summary>Everyone who shows the rider or the vehicle, and the rider.</summary>
    private List<GameSession> CarryAudience(GameSession s, VehicleUnit unit) =>
        [.. new[] { s }.Concat(_world.Viewers(s)).Concat(unit.Viewers).Distinct()];

    private static AoIInfoTotal CarryChange(GameSession s, AoICarry beCarried, VehicleUnit unit, AoICarryInfo vehicleCarries)
    {
        var total = new AoIInfoTotal { AoITickMs = 50, TickNowMs = (ulong)MonsterService.NowMs };
        total.AoiData.Add(new AoIInfoSingle { EntityUuid = (ulong)s.Role!.Id, CarryInfo = new AoICarryInfo { BeCarried = beCarried } });
        total.AoiData.Add(new AoIInfoSingle { EntityUuid = (ulong)unit.Uid, CarryInfo = vehicleCarries });
        return total;
    }

    /// <summary>
    /// Takes the seat: the carry info (31036) first, so LC_GamePlayStatusMgr.EnterVehicle finds the passenger component,
    /// then 32702 Vehicle (GameplayStatusConfig 3, the interaction, the vehicle) and the seat's camera (32704 _iChange3C).
    /// </summary>
    private async Task BoardAsync(GameSession s, VehicleUnit unit, int seat, long interaction)
    {
        unit.Seats[seat] = s;
        _riders[s] = new Rider(unit, seat, interaction);
        var carry = unit.Carry(seat);
        var change = CarryChange(s, unit.Carrier(seat), unit, new AoICarryInfo { AddCarries = { carry } });
        foreach (var viewer in CarryAudience(s, unit)) await Send(viewer, WorldCoreService.AoiInfo, change);
        await VehicleStateAsync(s, _riders[s], interrupt: false);
        if (VehicleSeatConfig.Get(_data, unit.SeatRows[seat]) is { } row)
            await Send(s, WorldInteractionService.Change3C, new Change3CNtf { CfgId = (int)row.Change3C });
    }

    private async Task VehicleStateAsync(GameSession s, Rider? rider, bool interrupt)
    {
        if (s.Role == null) return;
        var info = rider == null ? new InteractInfo { EType = InteractInfo.Types.EState.Idle } : new InteractInfo
        {
            EType = InteractInfo.Types.EState.Vehicle, StateId = VehicleStatusType, InteractConfigId = (int)rider.Interaction,
            ActiveObjId = rider.Unit.Uid,
        };
        var message = new InteractChangeNtf { Uid = s.Role.Id, DataInfo = info, Interrupt = interrupt };
        await Send(s, WorldInteractionService.Change, message);
        foreach (var viewer in _world.Viewers(s)) await Send(viewer, WorldInteractionService.Change, message);
    }

    /// <summary>
    /// Leaves the seat: 32702 Idle first (LC_GamePlayStatusMgr.ExitVehicle caches it on the passenger), then the carry
    /// info that ends the ride (be_carried with id 0, LC_PassengerComponent.ExitVehicle). (inferred) the order and the
    /// empty offset that keeps be_carried non-empty for LC_VehicleHelper.ProcessVehicle.
    /// </summary>
    private async Task GetOffAsync(GameSession s, bool interrupt)
    {
        if (!_riders.Remove(s, out var rider)) return;
        var unit = rider.Unit;
        var carry = unit.Carry(rider.Seat);
        unit.Seats[rider.Seat] = null;
        unit.FireDirs.Remove(rider.Seat);
        var gone = new AoICarry { Id = 0, CarrySlot = rider.Seat, CarryOffset = new Vec3(), CarryType = carry.CarryType };
        if (s.Role == null || s.SceneLoading)
        {
            var total = new AoIInfoTotal { AoITickMs = 50, TickNowMs = (ulong)MonsterService.NowMs };
            total.AoiData.Add(new AoIInfoSingle { EntityUuid = (ulong)unit.Uid, CarryInfo = new AoICarryInfo { RemoveCarries = { carry } } });
            foreach (var viewer in unit.Viewers.ToList()) await Send(viewer, WorldCoreService.AoiInfo, total);
            return;
        }
        await VehicleStateAsync(s, null, interrupt);
        var change = CarryChange(s, gone, unit, new AoICarryInfo { RemoveCarries = { carry } });
        foreach (var viewer in CarryAudience(s, unit)) await Send(viewer, WorldCoreService.AoiInfo, change);
    }

    private Task VehicleLeavingAsync(GameSession s) => GetOffAsync(s, interrupt: true);

    private Task VehicleDiedAsync(GameSession s) => GetOffAsync(s, interrupt: true);

    /// <summary>A player who rides is shown on its seat to a new viewer (its AOI base carries no carry info).</summary>
    private async Task VehicleRiderSeenAsync(GameSession viewer, GameSession subject)
    {
        if (!_riders.TryGetValue(subject, out var rider) || subject.Role == null) return;
        var total = new AoIInfoTotal { AoITickMs = 50, TickNowMs = (ulong)MonsterService.NowMs };
        total.AoiData.Add(new AoIInfoSingle
        {
            EntityUuid = (ulong)subject.Role.Id, CarryInfo = new AoICarryInfo { BeCarried = rider.Unit.Carrier(rider.Seat) },
        });
        await Send(viewer, WorldCoreService.AoiInfo, total);
        await Send(viewer, WorldInteractionService.Change, new InteractChangeNtf
        {
            Uid = subject.Role.Id,
            DataInfo = new InteractInfo
            {
                EType = InteractInfo.Types.EState.Vehicle, StateId = VehicleStatusType, InteractConfigId = (int)rider.Interaction,
                ActiveObjId = rider.Unit.Uid,
            },
        });
    }

    /// <summary>
    /// 32703 of a rider: the seat's buttons (VehicleSeatConfig _kGameplayBtnId); a QuitStatus button gets off, the
    /// others (the seat's skill slots) are cast through 31101 and refused here.
    /// </summary>
    private async Task<int> RiderButtonAsync(GameSession s, Rider rider, UseInteractActionReq req)
    {
        if (VehicleSeatConfig.Get(_data, rider.Unit.SeatRows[rider.Seat]) is not { } seat || !seat.GameplayBtnId.Contains(req.ButtonId)
            || GameplayStatusButtonConfig.Get(_data, req.ButtonId) is not { ButtonFuncType: 0 } button || !QuitsStatus(button))
            return ErrorCodes.NpcParameter;
        await GetOffAsync(s, interrupt: false);
        return 0;
    }

    /// <summary>
    /// 32708 (LC_VehicleServer.ChangeSeat): seatIdx is the 0-based seat. (inferred) Any free seat of the vehicle; the new
    /// seat shows as the rider's be_carried and the vehicle's passengers, and its camera follows.
    /// </summary>
    private async Task ChangeSeatAsync(GameSession s, Rider rider, ChangeVehicleSeatReq req)
    {
        var unit = rider.Unit;
        int to = req.SeatIdx;
        int code = to >= 0 && to < unit.Seats.Length && to != rider.Seat && unit.Seats[to] == null ? 0 : ErrorCodes.NpcParameter;
        await Send(s, WorldInteractionService.SeatChange, new ChangeVehicleSeatRes { SeatIdx = to, Result = code });
        if (code != 0) return;
        unit.Seats[rider.Seat] = null;
        unit.FireDirs.Remove(rider.Seat);
        unit.Seats[to] = s;
        _riders[s] = rider with { Seat = to };
        var all = new AoICarryInfo();
        for (int seat = 0; seat < unit.Seats.Length; seat++)
            if (unit.Seats[seat] != null) all.AllCarries.Add(unit.Carry(seat));
        var change = CarryChange(s, unit.Carrier(to), unit, all);
        foreach (var viewer in CarryAudience(s, unit)) await Send(viewer, WorldCoreService.AoiInfo, change);
        if (VehicleSeatConfig.Get(_data, unit.SeatRows[to]) is { } row)
            await Send(s, WorldInteractionService.Change3C, new Change3CNtf { CfgId = (int)row.Change3C });
    }

    /// <summary>
    /// 31074 of a rider (LC_PassengerComponent, its facing): the driver turns the vehicle, a gunner aims its seat.
    /// (inferred) The gunner's face is that seat's fire_dir in the vehicle's AOI detail (LC_VehicleHelper.ProcessVehicle_Detail).
    /// </summary>
    private async Task SyncVehicleAsync(GameSession s, Rider rider, SyncVehicleReq req)
    {
        if (!float.IsFinite(req.Face) || req.Face < -360 || req.Face > 720) return;
        var unit = rider.Unit;
        if (unit.SeatTypes[rider.Seat] == 0)
        {
            unit.Face = req.Face;
            var stop = new MovementChangeNtf
            {
                Uid = unit.Uid, TransType = MovementTransitionType.MoveTransNormal,
                MovementInfo = new MovementInfo { Pos = unit.Pos, Face = unit.Face, ServerTime = MonsterService.NowMs, MoveType = MovementType.MoveTypeNone },
            };
            await _world.MoveUnitAsync(unit, stop, except: s);
            return;
        }
        unit.FireDirs[rider.Seat] = req.Face;
        var total = new AoIInfoTotal { AoITickMs = 50, TickNowMs = (ulong)MonsterService.NowMs };
        total.AoiData.Add(new AoIInfoSingle { EntityUuid = (ulong)unit.Uid, VehicleDetail = unit.Detail() });
        foreach (var viewer in unit.Viewers.ToList())
            if (viewer != s) await Send(viewer, WorldCoreService.AoiInfo, total);
    }

    /// <summary>
    /// After a driver's 31082 moved the player: the walk is the vehicle's (the driver controls it), so the vehicle goes
    /// there for everyone else and the other riders are carried along. (inferred) The vehicle moves at its driver's speed
    /// and the riders' positions follow without a walk of their own.
    /// </summary>
    public async Task VehicleMovedAsync(GameSession s, WalkReq walk)
    {
        if (!_riders.TryGetValue(s, out var rider) || rider.Unit.SeatTypes[rider.Seat] != 0 || rider.Unit.Removed) return;
        var unit = rider.Unit;
        (unit.X, unit.Y, unit.Z, unit.Face) = (walk.Pos.X, walk.Pos.Y, walk.Pos.Z, walk.Face);
        var movement = GameAoiSink.Movement(s, walk);
        movement.Uid = unit.Uid;
        await _world.MoveUnitAsync(unit, movement, except: s);
        foreach (var other in unit.Seats.OfType<GameSession>().Where(p => p != s).ToList())
        {
            other.PreviousPosition = other.Position;
            other.Position = walk.Pos.Clone();
            await _world.MoveAsync(other, new WalkReq { Pos = walk.Pos.Clone(), Face = walk.Face, BStop = true, SpeedRate = 1 });
        }
    }

    /// <summary>CombatService.GrantedSkill: the skills of the rider's seat (VehicleSeatConfig _kMainSkills and _kSkills).</summary>
    private bool SeatSkill(GameSession s, int skill) =>
        _riders.TryGetValue(s, out var rider) && VehicleSeatConfig.Get(_data, rider.Unit.SeatRows[rider.Seat]) is { } seat
        && (seat.MainSkills.Contains(skill) || seat.Skills.Contains(skill));
}
