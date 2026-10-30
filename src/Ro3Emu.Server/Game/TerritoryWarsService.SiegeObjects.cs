using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The castle of the siege (docs/systems/territory-wars.md "Siege castle"): the NPCs NpcLevelPointRespawnConfig places on
/// dungeon 2230101 (_iMapid is the DungeonConfig id, _iRespawnpointid a point of its scene editor
/// sc_gvg_gongchengzhan_001_dg01). The gates, guardians, barricades, the revive point and the markers that keep the
/// castle's buildings standing are spawned with the siege room as monsters of their NPCConfig rows (_iNPCType 1); the
/// crystal (point 44) keeps its own spawn (TerritoryWarsService.SiegeBattle.cs). A destroyed object stays destroyed
/// for the rest of the siege, also in a room opened again for it; a damaged one keeps its HP.
/// </summary>
public sealed partial class TerritoryWarsService
{
    /// <summary>enum_SiegeObjectType (Config/Define/Lua_GameDefine.lua:14875).</summary>
    private enum SiegeObjectType { Ladder = 0, Car = 1, CityGate = 2, OuterTower = 3, InnerTower = 4, Revive = 5, Crystal = 6 }

    /// <summary>
    /// DungeonLua_2230101.lua UIModule_NpcData (l.1070-1121, not exported to config): the points the siege UI follows,
    /// [npc, eObjectType, iAreaId], parsed by LC_SceneCom_UIModule_Siege.lua:150-185. The CityGate point becomes the top
    /// bar's iCityGatePointId.
    /// </summary>
    private static readonly Dictionary<int, SiegeObjectType> SiegeNpcData = new()
    {
        [36] = SiegeObjectType.CityGate,
        [38] = SiegeObjectType.OuterTower, [39] = SiegeObjectType.OuterTower, [40] = SiegeObjectType.OuterTower,
        [41] = SiegeObjectType.InnerTower, [42] = SiegeObjectType.InnerTower, [43] = SiegeObjectType.InnerTower,
        [44] = SiegeObjectType.Crystal, [46] = SiegeObjectType.Ladder, [47] = SiegeObjectType.Ladder,
        [48] = SiegeObjectType.Car, [286] = SiegeObjectType.Revive,
    };

    /// <summary>
    /// The NpcLevelPointRespawnConfig NPCs of 2230101 spawned with the room: City Gate 4900005 (points 36 and 214), Knight
    /// Guardian 4901011 (InnerTower 41-43), Bow Guardian 4901012 (OuterTower 38-40), Obstacle 4900022 (the ten barricades),
    /// the building markers 4903002 (UnitConfig 4900011, at the DestoryModelPerformParams points of the workshops and
    /// arrow towers), the revive point 4900010 and the revive markers 4901021 (attackers) / 4901022 (defenders). Not yet:
    /// the ladders 4900000/4900012 and the ram 4900002 (they need their route AI), the archers 4901001, the guide arrows
    /// 4903001, the fixed vehicles 4901004-4901009 and the invisible markers 4900017.
    /// </summary>
    private static readonly HashSet<long> SiegeSpawnedNpcs = [4900005, 4901011, 4901012, 4900022, 4903002, 4900010, 4901021, 4901022];

    /// <summary>The City Gates: 36 "城门刷新点" in the castle's gateway, 214 "进攻方城门" the attackers' camp gate.</summary>
    private const int SiegeGatePoint = 36, SiegeCampGatePoint = 214;
    private const int SiegeReviveNpc = 4900010, SiegeReviveRowPoint = 45, SiegeRevivePoint = 286;
    /// <summary>
    /// (inferred) The attackers' marker of the contested revive point 269 ("争夺复活点-进攻方") stands on the same spot as
    /// the defenders' 270 ("争夺复活点-防守方"): the point starts with the defenders, and its capture is not known.
    /// </summary>
    private const int SiegeContestedAttackerRevive = 269;
    /// <summary>(inferred) The attackers' revive marker under the castle wall (268 "进攻方城墙下") stands once the gate fell.</summary>
    private const int SiegeWallRevive = 268;

    /// <summary>Whether a castle object stands in the run's stage: the camp gate in Prepare only (TerritoryWarsService.SiegeStages.cs
    /// SiegeGateRulesAsync), the attackers' wall revive marker from the inner city on.</summary>
    private static bool SiegePointStands(SiegeRunState r, int point) => point switch
    {
        SiegeCampGatePoint => r.Stage == SiegePrepareStage,
        SiegeWallRevive => r.Stage >= SiegeStrongholdStage,
        _ => true,
    };

    /// <summary>A unit carrying Battle Start Protection takes no damage.</summary>
    private bool SiegeProtected(MonsterUnit m) => _buffs.UnitBuffs(m).Any(b => b.ConfigId == BattleStartProtection);

    /// <summary>(point, npc) of every spawned castle object, in point order; read once.</summary>
    private (int Point, int Npc)[]? _siegeObjectRows;

    /// <summary>
    /// NpcLevelPointRespawnConfig rows of _iMapid 2230101 with a spawned NPC. Row 4900016 places the revive point 4900010 at
    /// 45, which is a group id and no point of the scene; UIModule_NpcData puts that NPC at point 286 (inferred: 286 is
    /// meant).
    /// </summary>
    private (int Point, int Npc)[] SiegeObjectRows() => _siegeObjectRows ??= [.. NpcLevelPointRespawnConfig.All(_data)
        .Where(r => r.Mapid == SiegeScene && SiegeSpawnedNpcs.Contains(r.Npcid))
        .Select(r => ((int)(r.Npcid == SiegeReviveNpc && r.Respawnpointid == SiegeReviveRowPoint ? SiegeRevivePoint
            : r.Respawnpointid), (int)r.Npcid))
        .Where(r => r.Item1 != SiegeContestedAttackerRevive).OrderBy(r => r.Item1)];

    /// <summary>The point a castle object of the run stands on, or null for any other unit.</summary>
    private static int? SiegeObjectPoint(SiegeRun battle, MonsterUnit m)
    {
        foreach (var (point, unit) in battle.Objects)
            if (ReferenceEquals(unit, m)) return point;
        return null;
    }

    /// <summary>
    /// Spawns the castle objects the room lacks at their scene-editor points: neither standing nor destroyed earlier in
    /// this siege, and standing in the run's stage. A damaged object comes back with its HP. True when one spawned.
    /// </summary>
    private async Task<bool> SiegeSpawnObjectsAsync(SiegeRun battle)
    {
        if (battle.Room.Closed || battle.State.Settled) return false;
        var map = _maps.Map(SiegeScene) ?? throw new InvalidOperationException("Siege scene 2230101 is absent");
        bool spawned = false;
        foreach (var (point, npc) in SiegeObjectRows())
        {
            if (battle.Objects.ContainsKey(point) || battle.State.Destroyed.Contains(point)
                || !SiegePointStands(battle.State, point)) continue;
            if (!map.Points.TryGetValue(point, out var at))
                throw new InvalidOperationException($"Siege object point {point} is absent from scene 2230101");
            if (await _instances.SpawnAsync(battle.Room, npc, map.Grounded(at)) is not { } actor) break; // the room closed
            if (battle.State.ObjectHp.TryGetValue(point, out long hp) && hp > 0) actor.Hp = Math.Min(actor.Hp, hp);
            SiegeHoldsDamage(actor);
            battle.Objects[point] = actor;
            spawned = true;
        }
        return spawned;
    }

    /// <summary>
    /// A castle object is no field monster: (inferred) the damage it took stays for the whole siege, also after it lost
    /// its target and stood back at its point, where a field monster heals to full (MonsterService); and one whose
    /// AIConfig has _iCanInBattle 0 (the City Gate's 4900000 "GVG_S0_Gate"; the barricades, markers and revive point have
    /// no AI) never takes a target, so a hit neither locks its attacker (31052) nor starts the leash that ended in the
    /// heal. The guardians (20000009) and the crystal (4900009) have _iCanInBattle 1 and fight back as before. The client
    /// never reads _iCanInBattle; reading 0 as "never in battle" is inferred.
    /// </summary>
    private void SiegeHoldsDamage(MonsterUnit actor)
    {
        actor.KeepsDamage = true;
        actor.StaysOutOfBattle = NPCConfig.Get(_data, actor.Template.NpcId) is not { } npc
            || AIConfig.Get(_data, npc.Ai) is not { CanInBattle: 1 };
    }

    /// <summary>
    /// (inferred) A castle object is hit by the other camp's siege members only: its NPCConfig _iCamp 2 objects (the gates,
    /// guardians and barricades) by the attackers, camp 1 objects by the defenders, the camp 4 revive point and markers
    /// by nobody, and nothing the client cannot select (_iNoSelected 1, the markers).
    /// </summary>
    private bool SiegeObjectHittable(GameSession s, SiegeRun battle, MonsterUnit m) =>
        SiegeActive(battle) && SiegeMember(s, battle)
        && NPCConfig.Get(_data, m.Template.NpcId) is { NoSelected: 0, Camp: 1 or 2 } npc && npc.Camp != CampOf(s)
        && !SiegeProtected(m);

    /// <summary>CombatService.Killed on a castle object of the killer's siege.</summary>
    private async Task SiegeObjectKilledAsync(GameSession s, SiegeRun battle, int point)
    {
        using var reservation = await EnterGateAsync("territory/siege object kill", s);
        using var stagedTerritoryState = StageState();
        try
        {
            if (!battle.Room.Closed && !battle.State.Settled) await SiegeObjectDestroyedAsync(battle, point);
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
    }

    /// <summary>A castle object died (CombatService.Killed, or found dead by the siege clock): it stays destroyed.</summary>
    private async Task SiegeObjectDestroyedAsync(SiegeRun battle, int point)
    {
        if (battle.State.Destroyed.Contains(point)) return;
        int stage = battle.State.Stage;
        var walls = SiegeActiveWalls(battle.State).ToHashSet();
        battle.State.Destroyed.Add(point);
        battle.State.ObjectHp.Remove(point);
        await SaveAsync(state);
        await SiegePublishObjectsAsync(battle);
        await SiegeAdvanceStageAsync(battle);
        // A barricade or a fortress opens its walls without a new stage (var 133).
        if (battle.State.Stage == stage && !walls.SetEquals(SiegeActiveWalls(battle.State))) await SiegeBroadcastBaseAsync(battle);
    }

    /// <summary>The siege clock: castle objects that died without a kill callback (their killer left meanwhile), then
    /// the HP changes of the last second (30115).</summary>
    private async Task SiegeObjectsClockAsync(SiegeRun battle)
    {
        foreach (var (point, unit) in battle.Objects.ToArray())
            if (unit.Dead && point != SiegeCrystalPoint) await SiegeObjectDestroyedAsync(battle, point);
        await SiegePublishObjectsAsync(battle);
    }
}
