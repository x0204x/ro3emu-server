using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Mock Battle's Adamantine Madogear 16300008: NPCConfig _iNPCSubType 103 (a vehicle), VehicleConfig 16300008 with one
/// driver seat (VehicleSeatConfig 16300008: _kMainSkills 16301001-16301701 Hammer Strike, Charge, Earthquake,
/// Whirlwind, AoE Acceleration, Earth-Splitting Blow; exit 4900025; camera 10). Interaction 1630000801 boards it
/// (GameplayStatusConfig 3, NPC condition 7100044: an empty seat on a vehicle of the player's camp); its seats are the
/// shared VehicleSeats. Skill 16305501 "Summon Madogear Pilot" (script GVG_Relax_Summon, _kSkillCategory 16300101, a
/// battlefield skill) brings it; BattleEventPointConfig 41009 (type 3001, data 16300008, chat 5702 "${1} Adamantine
/// Madogear Pilot has respawned.") announces it and 41031 (type 149, data 16305501: 1 personal, squad and camp) scores
/// the summon.
/// (inferred) The Large Combat Supplies give the summon (the strongest battlefield skill); it raises a Madogear of the
/// caster's camp 2 m in front of the caster, ${1} is the caster's name, the caster may board it or leave it to a
/// camp-mate, and it stays until it is destroyed or the match ends. Its stats are NPCConfig's, as a war vehicle's.
/// </summary>
public sealed partial class CasualBattleService
{
    private const int Madogear = 16300008, SummonMadogear = 16305501, TypeSummonCast = 149;
    private const long MadogearUidBase = (1L << 53) | (1L << 52);
    private long _madogearSerial;
    private long _madogearBuffGuid = (1L << 56) | (1L << 53);
    private VehicleSeats? _madogearSeats;
    private VehicleSeats MadogearRiders => _madogearSeats ??= new VehicleSeats(_data, _world, (s, id, msg) => s.SendGameAsync(id, msg));

    /// <summary>A Madogear of a camp; its health shows as a monster's.</summary>
    internal sealed class MadogearUnit : SeatedVehicle
    {
        public override EntityBase Entity()
        {
            var entity = SeatedEntity();
            AddHealth(entity, this);
            return entity;
        }
    }

    /// <summary>Summon Madogear Pilot landed: the caster's camp gets a Madogear, announced and scored.</summary>
    private async Task SummonMadogearAsync(GameSession s, Match m)
    {
        if (m.Ending || m.Room.Closed || NewMadogear(s, m) is not { } unit || !await _instances.AddPropAsync(m.Room, unit)) return;
        m.Arena.Madogears[unit.Uid] = unit;
        foreach (var row in Rows(TypeSpawn, Madogear)) await ChatAsync(m, (int)row.ChatId, m.Players[s].Name);
        var rows = Rows(TypeSummonCast, SummonMadogear).ToArray();
        foreach (var row in rows) Award(m, s, row);
        if (rows.Length > 0) await AfterScoreAsync(m);
    }

    private MadogearUnit? NewMadogear(GameSession s, Match m)
    {
        if (NPCConfig.Get(_data, Madogear) is not { NPCSubType: 103 } npc
            || VehicleConfig.Get(_data, npc.SubTypeValue) is not { Seats: { Length: > 0 } seats }
            || _monsters.MachineTemplate(Madogear) is not { } template) return null;
        float rad = s.Face * MathF.PI / 180f;
        var spot = new MapPoint(s.Position.X + 2 * MathF.Sin(rad), s.Position.Y, s.Position.Z + 2 * MathF.Cos(rad), s.Face);
        if (_worldData.Map(Scene) is { } map) spot = map.Grounded(spot);
        var home = new SpawnPoint(-1, spot.X, spot.Y, spot.Z, spot.Face, Madogear);
        int camp = m.Players[s].Camp;
        var unit = new MadogearUnit
        {
            Uid = MadogearUidBase | Interlocked.Increment(ref _madogearSerial), SceneEntry = m.Room.SceneEntry,
            InstanceId = m.Room.Id, X = spot.X, Y = spot.Y, Z = spot.Z, Face = spot.Face, NpcId = Madogear,
            Owner = m.Players[s].Id, Camp = camp, CampOverride = camp, Name = npc.Name is [var nameId, ..] ? _data.Text(nameId) : "",
            Template = template, Group = new SpawnGroup(-1, false, 1, 0, [], [home]), Home = home, Hp = template.MaxHp,
            BornTimeMs = MonsterService.NowMs, SeatRows = seats, Seats = new GameSession?[seats.Length],
            SeatTypes = [.. seats.Select(seat => VehicleSeatConfig.Get(_data, seat)?.Type ?? 0)],
            KeepsDamage = true, StaysOutOfBattle = true,
        };
        foreach (long[] entry in npc.BornEventBuff)
            if (entry is [var buff, ..] && buff > 0 && _data.Row("BuffConfig", buff) != null)
                unit.Buffs.Add(new BuffInfo
                {
                    BuffGuid = Interlocked.Increment(ref _madogearBuffGuid), ConfigId = (int)buff, PileCount = 1, SourceId = unit.Uid,
                });
        return unit;
    }

    /// <summary>32700 on a Madogear: its rider's exit, or a camp member's boarding (VehicleSeats.SeatFor).</summary>
    private async Task MadogearInteractAsync(GameSession s, Match m, MadogearUnit unit, NpcInteractionReq req)
    {
        int code, seat = -1;
        bool exit = MadogearRiders.TryGetRider(s, out var rider);
        if (exit) code = MadogearRiders.IsExit(rider, unit, req.InteractionId) ? 0 : ErrorCodes.NpcParameter;
        else if (s.Dead || unit.Dead || unit.Removed || m.Stage == 0) code = ErrorCodes.NpcMissing;
        else code = MadogearRiders.SeatFor(s, unit, req,
            (p, v) => m.Players.TryGetValue(p, out var who) && who.Camp == v.Camp, null, out seat);
        await s.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionRsp { Result = code, Req = req });
        if (code != 0) return;
        if (exit) await MadogearRiders.GetOffAsync(s, interrupt: false);
        else await MadogearRiders.BoardAsync(s, unit, seat, req.InteractionId);
    }

    /// <summary>A destroyed Madogear: everyone on it gets off and its wreck leaves the room.</summary>
    private async Task MadogearDestroyedAsync(Match m, MadogearUnit unit)
    {
        if (!m.Arena.Madogears.Remove(unit.Uid)) return;
        foreach (var rider in unit.Seats.OfType<GameSession>().ToList()) await MadogearRiders.GetOffAsync(rider, interrupt: true);
        await _instances.RemovePropAsync(m.Room, unit);
    }

    /// <summary>32703 / 32708 / 31074 of a Madogear rider; false for anyone else.</summary>
    public Task<bool> MadogearSharedAsync(GameSession s, uint id, ReadOnlyMemory<byte> body) =>
        MadogearRiders.RiderMessageAsync(s, id, body);

    /// <summary>A driver's walk is its Madogear's (RoleSystems.MovedAsync).</summary>
    public Task MadogearMovedAsync(GameSession s, WalkReq walk) => MadogearRiders.MovedAsync(s, walk);

    private Task MadogearRiderSeenAsync(GameSession viewer, GameSession subject) => MadogearRiders.RiderSeenAsync(viewer, subject);
}
