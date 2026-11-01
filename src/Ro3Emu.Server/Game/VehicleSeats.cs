using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A vehicle NPC with seats: NPCConfig _iNPCSubType 103 (enum_NpcSubType.Vehicle), so the client gives it
/// LC_VehicleComponent with the seats of its VehicleConfig (_iSubTypeValue -> _kSeats, VehicleSeatConfig rows by 0-based
/// index, LC_VehicleComponent.GetSeatConfig). A monster unit for the combat. Its owner adds what its own game shows on
/// the entity (TerritoryWarsService.VehicleUnit: health and guild).
/// </summary>
internal abstract class SeatedVehicle : MonsterUnit
{
    public required int NpcId { get; init; }
    public required long Owner { get; init; }
    public required long Camp { get; init; }
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

    /// <summary>The NPC entity (type 2) with its buffs, riders and aim.</summary>
    protected EntityBase SeatedEntity()
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
        if (Buffs.Count > 0) entity.BuffData = new AoIBuffInfo { AddBuffs = { Buffs.Select(b => b.Clone()) } };
        var carries = new AoICarryInfo();
        for (int seat = 0; seat < Seats.Length; seat++)
            if (Seats[seat] != null) carries.AllCarries.Add(Carry(seat));
        if (carries.AllCarries.Count > 0) entity.CarryInfo = carries;
        if (FireDirs.Count > 0) entity.VehicleDetail = Detail();
        return entity;
    }

    /// <summary>Max HP, current HP and level, as a monster's entity carries them.</summary>
    public static void AddHealth(EntityBase entity, MonsterUnit unit)
    {
        foreach (var (idx, value) in new (long, long)[]
                 { (101, unit.Template.MaxHp), (2000, unit.Dead ? 0 : unit.Hp), (2002, unit.Template.Level) })
            entity.Property.Add(new AoIProperty { Idx = idx, Data = value, BaseData = value });
    }
}

/// <summary>
/// The seats of one game's vehicles (TerritoryWarsService's war vehicles, CasualBattleService's Madogear): who sits where,
/// boarding and getting off, seat changes, the driver's walk and facing, the seat's buttons and skills. Seats are taken
/// through the NPC's interactions (NPCInteractConfig _iStatusID 3, GameplayStatusConfig type 3, seat _iStatusLocation)
/// and left through the seat's _iExitNpcAction (4900025, the QuitStatus of LC_VehicleServer). Who sits where travels as
/// AOI carry info (LC_VehicleHelper.ProcessVehicle): the passenger's be_carried {vehicle id, slot, carry_type = the
/// seat's enum_PassengerType} and the vehicle's all/add/remove_carries; the driver then controls the vehicle
/// (LC_PassengerComponent: ChangeControlUnit), so its 31082 walks are the vehicle's positions.
/// </summary>
internal sealed class VehicleSeats(GameData data, World world, Func<GameSession, uint, IMessage, Task> send)
{
    public const int StatusType = 3;
    /// <summary>LC_ConditionDefine: VehicleHasEmptySeat, CheckTakeOnVehicleSeat, InStatus; LC_CommonCondition.CheckCamp.</summary>
    public const long HasEmptySeat = 12002, TakeOnSeat = 12003, InStatus = 12001, CheckCamp = 2200;
    private const int QuitStatusAction = 5004;

    public sealed record Rider(SeatedVehicle Unit, int Seat, long Interaction);

    private readonly Dictionary<GameSession, Rider> _riders = [];

    public bool Riding(GameSession s) => _riders.ContainsKey(s);

    public bool TryGetRider(GameSession s, out Rider rider) => _riders.TryGetValue(s, out rider!);

    /// <summary>A GameplayStatusButtonConfig button that runs ActionConfig 5004 QuitStatus.</summary>
    public static bool QuitsStatus(GameData data, GameplayStatusButtonConfig button) =>
        button.Action is { } action && action.ToString().Contains(QuitStatusAction.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        && ActionConfig.Get(data, QuitStatusAction) is { FunctionName: "QuitStatus" };

    /// <summary>
    /// The seat interaction of a vehicle within NPCConfig _iInteractDistance: its GameplayStatusConfig is a vehicle
    /// state, its seat (_iStatusLocation, 1-based) is free and both its conditions hold
    /// (<see cref="Condition"/>). The caller checks that the player and the vehicle are in its game.
    /// </summary>
    public int SeatFor(GameSession s, SeatedVehicle unit, NpcInteractionReq req, Func<GameSession, SeatedVehicle, bool> sameSide,
        Func<long, long, bool?>? other, out int seat)
    {
        seat = -1;
        if (NPCConfig.Get(data, unit.NpcId) is not { } npc || !npc.InteractId.Contains(req.InteractionId)
            || NPCInteractConfig.Get(data, req.InteractionId) is not { } interaction
            || GameplayStatusConfig.Get(data, interaction.StatusId) is not { StatusType: StatusType })
            return ErrorCodes.NpcParameter;
        if (MonsterService.Distance(s.Position.X, s.Position.Z, unit.X, unit.Z) > Math.Max(npc.InteractDistance, 3) + 1)
            return ErrorCodes.NpcTooFar;
        seat = (int)interaction.StatusLocation - 1;
        if (seat < 0 || seat >= unit.Seats.Length || unit.Seats[seat] != null) return ErrorCodes.NpcParameter;
        return Condition(s, unit, interaction.Condition, sameSide, other) && Condition(s, unit, interaction.NPCCondition, sameSide, other)
            ? 0 : ErrorCodes.NpcParameter;
    }

    private bool Condition(GameSession s, SeatedVehicle unit, long conditionId, Func<GameSession, SeatedVehicle, bool> sameSide,
        Func<long, long, bool?>? other)
    {
        if (conditionId == 0) return true;
        if (LCConditionList.Get(data, conditionId) is not { } condition) return false;
        if (condition.SubConditionList is not { ValueKind: System.Text.Json.JsonValueKind.Array } list)
            return SubCondition(s, unit, condition, sameSide, other);
        return list.EnumerateArray().All(sub => SubCondition(s, unit, new LCConditionList(sub), sameSide, other));
    }

    /// <summary>
    /// LC_InteractCondition.CheckVehicleHasEmptySeat (12002: a living vehicle that is not an enemy, a seat of that
    /// enum_PassengerType free), CheckTakeOnVehicleSeat (12003: the player sits nowhere yet), LC_CommonCondition.CheckCamp
    /// (2200: the same side, its argument unused) and 12001 the vehicle state; the game decides the others
    /// (<paramref name="other"/>), any it does not know is closed.
    /// </summary>
    private bool SubCondition(GameSession s, SeatedVehicle unit, LCConditionList sub, Func<GameSession, SeatedVehicle, bool> sameSide,
        Func<long, long, bool?>? other)
    {
        if (sub.Data is not [var type, var arg, ..]) return false;
        bool? yes = type switch
        {
            HasEmptySeat => sameSide(s, unit)
                && Enumerable.Range(0, unit.Seats.Length).Any(i => unit.SeatTypes[i] == arg && unit.Seats[i] == null),
            TakeOnSeat => !_riders.ContainsKey(s),
            CheckCamp => sameSide(s, unit),
            InStatus => arg == StatusType && _riders.ContainsKey(s),
            _ => other?.Invoke(type, arg),
        };
        return yes is { } value && value != sub.Inverse;
    }

    /// <summary>The exit interaction of the rider's seat on its vehicle (VehicleSeatConfig _iExitNpcAction).</summary>
    public bool IsExit(Rider rider, SeatedVehicle unit, long interaction) =>
        rider.Unit == unit && interaction == VehicleSeatConfig.Get(data, unit.SeatRows[rider.Seat])?.ExitNpcAction;

    /// <summary>Everyone who shows the rider or the vehicle, and the rider.</summary>
    private List<GameSession> CarryAudience(GameSession s, SeatedVehicle unit) =>
        [.. new[] { s }.Concat(world.Viewers(s)).Concat(unit.Viewers).Distinct()];

    private static AoIInfoTotal CarryChange(GameSession s, AoICarry beCarried, SeatedVehicle unit, AoICarryInfo vehicleCarries)
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
    public async Task BoardAsync(GameSession s, SeatedVehicle unit, int seat, long interaction)
    {
        unit.Seats[seat] = s;
        _riders[s] = new Rider(unit, seat, interaction);
        var carry = unit.Carry(seat);
        var change = CarryChange(s, unit.Carrier(seat), unit, new AoICarryInfo { AddCarries = { carry } });
        foreach (var viewer in CarryAudience(s, unit)) await send(viewer, WorldCoreService.AoiInfo, change);
        await StateAsync(s, _riders[s], interrupt: false);
        if (VehicleSeatConfig.Get(data, unit.SeatRows[seat]) is { } row)
            await send(s, WorldInteractionService.Change3C, new Change3CNtf { CfgId = (int)row.Change3C });
    }

    private async Task StateAsync(GameSession s, Rider? rider, bool interrupt)
    {
        if (s.Role == null) return;
        var info = rider == null ? new InteractInfo { EType = InteractInfo.Types.EState.Idle } : new InteractInfo
        {
            EType = InteractInfo.Types.EState.Vehicle, StateId = StatusType, InteractConfigId = (int)rider.Interaction,
            ActiveObjId = rider.Unit.Uid,
        };
        var message = new InteractChangeNtf { Uid = s.Role.Id, DataInfo = info, Interrupt = interrupt };
        await send(s, WorldInteractionService.Change, message);
        foreach (var viewer in world.Viewers(s)) await send(viewer, WorldInteractionService.Change, message);
    }

    /// <summary>
    /// Leaves the seat: 32702 Idle first (LC_GamePlayStatusMgr.ExitVehicle caches it on the passenger), then the carry
    /// info that ends the ride (be_carried with id 0, LC_PassengerComponent.ExitVehicle). (inferred) the order and the
    /// empty offset that keeps be_carried non-empty for LC_VehicleHelper.ProcessVehicle.
    /// </summary>
    public async Task GetOffAsync(GameSession s, bool interrupt)
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
            foreach (var viewer in unit.Viewers.ToList()) await send(viewer, WorldCoreService.AoiInfo, total);
            return;
        }
        await StateAsync(s, null, interrupt);
        var change = CarryChange(s, gone, unit, new AoICarryInfo { RemoveCarries = { carry } });
        foreach (var viewer in CarryAudience(s, unit)) await send(viewer, WorldCoreService.AoiInfo, change);
    }

    /// <summary>A player who rides is shown on its seat to a new viewer (its AOI base carries no carry info).</summary>
    public async Task RiderSeenAsync(GameSession viewer, GameSession subject)
    {
        if (!_riders.TryGetValue(subject, out var rider) || subject.Role == null) return;
        var total = new AoIInfoTotal { AoITickMs = 50, TickNowMs = (ulong)MonsterService.NowMs };
        total.AoiData.Add(new AoIInfoSingle
        {
            EntityUuid = (ulong)subject.Role.Id, CarryInfo = new AoICarryInfo { BeCarried = rider.Unit.Carrier(rider.Seat) },
        });
        await send(viewer, WorldCoreService.AoiInfo, total);
        await send(viewer, WorldInteractionService.Change, new InteractChangeNtf
        {
            Uid = subject.Role.Id,
            DataInfo = new InteractInfo
            {
                EType = InteractInfo.Types.EState.Vehicle, StateId = StatusType, InteractConfigId = (int)rider.Interaction,
                ActiveObjId = rider.Unit.Uid,
            },
        });
    }

    /// <summary>32703 / 32708 / 31074 of a rider; false for anyone else or any other message.</summary>
    public async Task<bool> RiderMessageAsync(GameSession s, uint id, ReadOnlyMemory<byte> body)
    {
        if (!_riders.TryGetValue(s, out var rider)) return false;
        switch (id)
        {
            case WorldInteractionService.Action:
                await send(s, id, new UseInteractActionRes { Result = await ButtonAsync(s, rider, UseInteractActionReq.Parser.ParseFrom(body.Span)) });
                return true;
            case WorldInteractionService.SeatChange:
                await ChangeSeatAsync(s, rider, ChangeVehicleSeatReq.Parser.ParseFrom(body.Span));
                return true;
            case WorldInteractionService.VehicleSync:
                await SyncAsync(s, rider, SyncVehicleReq.Parser.ParseFrom(body.Span));
                return true;
            default: return false;
        }
    }

    /// <summary>
    /// 32703 of a rider: the seat's buttons (VehicleSeatConfig _kGameplayBtnId); a QuitStatus button gets off, the
    /// others (the seat's skill slots) are cast through 31101 and refused here.
    /// </summary>
    private async Task<int> ButtonAsync(GameSession s, Rider rider, UseInteractActionReq req)
    {
        if (VehicleSeatConfig.Get(data, rider.Unit.SeatRows[rider.Seat]) is not { } seat || !seat.GameplayBtnId.Contains(req.ButtonId)
            || GameplayStatusButtonConfig.Get(data, req.ButtonId) is not { ButtonFuncType: 0 } button || !QuitsStatus(data, button))
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
        await send(s, WorldInteractionService.SeatChange, new ChangeVehicleSeatRes { SeatIdx = to, Result = code });
        if (code != 0) return;
        unit.Seats[rider.Seat] = null;
        unit.FireDirs.Remove(rider.Seat);
        unit.Seats[to] = s;
        _riders[s] = rider with { Seat = to };
        var all = new AoICarryInfo();
        for (int seat = 0; seat < unit.Seats.Length; seat++)
            if (unit.Seats[seat] != null) all.AllCarries.Add(unit.Carry(seat));
        var change = CarryChange(s, unit.Carrier(to), unit, all);
        foreach (var viewer in CarryAudience(s, unit)) await send(viewer, WorldCoreService.AoiInfo, change);
        if (VehicleSeatConfig.Get(data, unit.SeatRows[to]) is { } row)
            await send(s, WorldInteractionService.Change3C, new Change3CNtf { CfgId = (int)row.Change3C });
    }

    /// <summary>
    /// 31074 of a rider (LC_PassengerComponent, its facing): the driver turns the vehicle, a gunner aims its seat.
    /// (inferred) The gunner's face is that seat's fire_dir in the vehicle's AOI detail (LC_VehicleHelper.ProcessVehicle_Detail).
    /// </summary>
    private async Task SyncAsync(GameSession s, Rider rider, SyncVehicleReq req)
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
            await world.MoveUnitAsync(unit, stop, except: s);
            return;
        }
        unit.FireDirs[rider.Seat] = req.Face;
        var total = new AoIInfoTotal { AoITickMs = 50, TickNowMs = (ulong)MonsterService.NowMs };
        total.AoiData.Add(new AoIInfoSingle { EntityUuid = (ulong)unit.Uid, VehicleDetail = unit.Detail() });
        foreach (var viewer in unit.Viewers.ToList())
            if (viewer != s) await send(viewer, WorldCoreService.AoiInfo, total);
    }

    /// <summary>
    /// After a driver's 31082 moved the player: the walk is the vehicle's (the driver controls it), so the vehicle goes
    /// there for everyone else and the other riders are carried along. (inferred) The vehicle moves at its driver's speed
    /// and the riders' positions follow without a walk of their own.
    /// </summary>
    public async Task MovedAsync(GameSession s, WalkReq walk)
    {
        if (!_riders.TryGetValue(s, out var rider) || rider.Unit.SeatTypes[rider.Seat] != 0 || rider.Unit.Removed) return;
        var unit = rider.Unit;
        (unit.X, unit.Y, unit.Z, unit.Face) = (walk.Pos.X, walk.Pos.Y, walk.Pos.Z, walk.Face);
        var movement = GameAoiSink.Movement(s, walk);
        movement.Uid = unit.Uid;
        await world.MoveUnitAsync(unit, movement, except: s);
        foreach (var other in unit.Seats.OfType<GameSession>().Where(p => p != s).ToList())
        {
            other.PreviousPosition = other.Position;
            other.Position = walk.Pos.Clone();
            await world.MoveAsync(other, new WalkReq { Pos = walk.Pos.Clone(), Face = walk.Face, BStop = true, SpeedRate = 1 });
        }
    }

    /// <summary>CombatService.GrantedSkill: the skills of the rider's seat (VehicleSeatConfig _kMainSkills and _kSkills).</summary>
    public bool SeatSkill(GameSession s, int skill) =>
        _riders.TryGetValue(s, out var rider) && VehicleSeatConfig.Get(data, rider.Unit.SeatRows[rider.Seat]) is { } seat
        && (seat.MainSkills.Contains(skill) || seat.Skills.Contains(skill));
}
