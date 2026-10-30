using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The war vehicles' repair zone (docs/systems/territory-wars.md "Machine HP and destruction"). The repair item 1006010
/// "Madogear Pilot Repair" ("Use to create a Madogear Pilot repair zone for @{1} seconds. Madogear Pilots within the zone
/// can be repaired.", _iDescription [12390100289, "30"]) uses skill 4901901 GVG_S0_RepairArea, a server script the client
/// does not hold. (inferred) The zone is the NPC named "Madogear Repair Zone" (4901010, NPCConfig _iNPCType 4, a ground
/// object) at the member's feet for those seconds, within the skill's _iDistanceMax; a vehicle of the member's camp inside
/// it carries BuffConfig 4903501 "Madogear Pilot Repair" ("The Madogear Pilot is being repaired.") and heals its
/// T_AXadd0_etc [2, 100, 101, 2]: 100 / 10 000 of its own (2: the recipient's) attribute 101, max HP, each second.
/// </summary>
public sealed partial class TerritoryWarsService
{
    private const int RepairZoneNpc = 4901010, RepairBuff = 4903501;
    private const string RepairFormula = "T_AXadd0_etc";
    /// <summary>(inferred) The repair cadence, as the other T_AXadd0_etc drains and heals that tick once a second.</summary>
    private const int RepairTickMs = 1000;
    /// <summary>Repair zone unit ids: next to the vehicles', below 2^53.</summary>
    private const long RepairZoneUidBase = (1L << 49) | (1L << 47);
    private long _repairSerial;
    /// <summary>The vehicles a zone repairs now (they carry its buff).</summary>
    private readonly Dictionary<VehicleUnit, RepairZoneUnit> _repairing = [];

    /// <summary>A repair zone on the ground: NPCConfig 4901010 of the member's camp until <see cref="EndMs"/>.</summary>
    internal sealed class RepairZoneUnit : WorldUnit
    {
        public required int NpcId { get; init; }
        public required int EntityType { get; init; }
        public required long Owner { get; init; }
        public required long Camp { get; init; }
        public required long Guild { get; init; }
        public required float Radius { get; init; }
        public required long BornMs { get; init; }
        public required long EndMs { get; init; }

        public override EntityBase Entity() => new()
        {
            Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = EntityType, Owner = Owner, AiMasterEntityId = (ulong)Owner,
            EntityCamp = Camp, GuildId = Guild, State = EntityState.Alive, BornTimeMs = BornMs, Face = Face, NotifyType = EntityBase.Types.NotifyType.All,
            MovementInfo = new MovementInfo { Pos = Pos, Face = Face, ServerTime = MonsterService.NowMs, MoveType = MovementType.MoveTypeNone },
        };
    }

    /// <summary>GuildService.WarMachines: the zone of a repair item stands at the member's feet in their battle.</summary>
    private async Task<bool> RaiseRepairZoneAsync(GameSession user, int item, long skill)
    {
        if (WarRoomOf(user) is not { } battle || user.Role is not { } role || battle.Room.Closed
            || NPCConfig.Get(_data, RepairZoneNpc) is not { } npc || SkillConfig.Get(_data, skill) is not { } row) return false;
        long now = MonsterService.NowMs;
        var zone = new RepairZoneUnit
        {
            Uid = RepairZoneUidBase | Interlocked.Increment(ref _repairSerial), SceneEntry = user.SceneEntry, InstanceId = battle.Room.Id,
            X = user.Position.X, Y = user.Position.Y, Z = user.Position.Z, Face = user.Face, NpcId = RepairZoneNpc,
            EntityType = (int)npc.NPCType, Owner = role.Id, Camp = CampOf(user), Guild = battle.Guild, Radius = Math.Max(1, row.DistanceMax) / 100f,
            BornMs = now, EndMs = now + RepairSeconds(item) * 1000L,
        };
        if (!await _instances.AddPropAsync(battle.Room, zone)) return false;
        ScheduleRepair(battle.Room, zone);
        return true;
    }

    /// <summary>(inferred) The zone's seconds: the @{1} of the item's description ("for @{1} seconds"), else 30.</summary>
    private long RepairSeconds(int item) =>
        ItemConfig.Get(_data, item)?.Description is { ValueKind: JsonValueKind.Array } description && description.GetArrayLength() > 1
        && long.TryParse(description[1].ValueKind == JsonValueKind.String ? description[1].GetString() : description[1].GetRawText(),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds) && seconds > 0 ? seconds : 30;

    private void ScheduleRepair(InstanceService.Room room, RepairZoneUnit zone) =>
        _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(RepairTickMs), () => RepairTickAsync(room, zone));

    /// <summary>One second of the zone: the vehicles of its side inside heal (its camp and guild: in a siege a camp is one
    /// guild, in a territory battle every camp is 1), the others lose its buff; at its end it leaves.</summary>
    private async Task RepairTickAsync(InstanceService.Room room, RepairZoneUnit zone)
    {
        bool live = !room.Closed && !zone.Removed && MonsterService.NowMs < zone.EndMs;
        foreach (var (vehicle, _) in _vehicles.Values.Where(v => v.Room == room).ToList())
        {
            bool inside = live && !vehicle.Dead && !vehicle.Removed && vehicle.Camp == zone.Camp && vehicle.Guild == zone.Guild
                && MonsterService.Distance(vehicle.X, vehicle.Z, zone.X, zone.Z) <= zone.Radius;
            if (inside) await RepairAsync(vehicle, zone);
            else if (_repairing.TryGetValue(vehicle, out var by) && by == zone) await EndRepairAsync(vehicle);
        }
        if (live)
        {
            ScheduleRepair(room, zone);
            return;
        }
        foreach (var vehicle in _repairing.Where(p => p.Value == zone).Select(p => p.Key).ToList()) await EndRepairAsync(vehicle);
        await _instances.RemovePropAsync(room, zone);
    }

    /// <summary>The vehicle carries the repair buff and heals its share of max HP (property 2000 to its viewers).</summary>
    private async Task RepairAsync(VehicleUnit vehicle, RepairZoneUnit zone)
    {
        if (!_repairing.ContainsKey(vehicle))
        {
            _repairing[vehicle] = zone;
            await _buffs.AddPermanentAsync(vehicle, RepairBuff, zone.Uid);
        }
        long heal = RepairAmount(vehicle);
        if (heal <= 0 || vehicle.Hp >= vehicle.Template.MaxHp) return;
        vehicle.Hp = Math.Min(vehicle.Template.MaxHp, vehicle.Hp + heal);
        await _world.ToUnitViewersAsync(vehicle, CombatService.AoiAttrChange, MonsterService.Hp(vehicle));
    }

    /// <summary>BuffConfig 4903501 T_AXadd0_etc [2 recipient, basis points, attribute 101 max HP, _]: the heal of one tick.</summary>
    private long RepairAmount(VehicleUnit vehicle) =>
        BuffConfig.Get(_data, RepairBuff) is { } buff && buff.DamageFormulaExtra is [RepairFormula, ..]
        && buff.DamageParamExtra is [[2, var basisPoints, AttributeId.MaxHp, ..], ..]
            ? vehicle.Template.MaxHp * basisPoints / 10_000 : 0;

    /// <summary>The vehicle left the zone (or the zone ended, or the vehicle is gone): its repair buff goes.</summary>
    private async Task EndRepairAsync(VehicleUnit vehicle)
    {
        if (!_repairing.Remove(vehicle)) return;
        await _buffs.RemoveUnitConfigAsync(vehicle, RepairBuff);
    }
}
