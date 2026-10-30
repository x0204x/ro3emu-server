using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The castle of the siege (docs/systems/territory-wars.md "Siege castle"): the NPCs NpcLevelPointRespawnConfig places on
/// dungeon 2230101 (_iMapid is the DungeonConfig id, _iRespawnpointid a point of its scene editor
/// sc_gvg_gongchengzhan_001_dg01). The gates, guardians, barricades, archers, the revive point, the guide arrows, the
/// attackers' ladders and ram and the markers that keep the castle's buildings standing are spawned with the siege room
/// as monsters of their NPCConfig rows (_iNPCType 1); the fixed war vehicles as vehicles
/// (TerritoryWarsService.SiegeVehicles.cs); the crystal (point 44) keeps its own spawn
/// (TerritoryWarsService.SiegeBattle.cs). A destroyed object stays destroyed for the rest of the siege, also in a room
/// opened again for it, except the ladders and the ram, which come back (TerritoryWarsService.SiegeUnits.cs); a damaged
/// one keeps its HP.
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
    /// The NpcLevelPointRespawnConfig NPCs of 2230101 spawned with the room as monsters: City Gate 4900005 (points 36 and
    /// 214), Knight Guardian 4901011 (InnerTower 41-43), Bow Guardian 4901012 (OuterTower 38-40), Obstacle 4900022 (the
    /// ten barricades), the building markers 4903002 (UnitConfig 4900011, at the DestoryModelPerformParams points of the
    /// workshops and arrow towers), the revive point 4900010, the revive markers 4901021 (attackers) / 4901022
    /// (defenders), the ladders 4900012 (46) and 4900000 (47), the Battering Ram 4900002 (48), the 38 archers 4901001
    /// (group 248 "刷守卫") and the guide arrows 4903001 (UnitConfig "引导箭头NPC"). The fixed vehicles 4901004-4901009
    /// are vehicles (TerritoryWarsService.SiegeVehicles.cs). Not spawned: the invisible markers 4900017 (camp 0, a buff
    /// 901301 and nothing that reads them).
    /// </summary>
    private static readonly HashSet<long> SiegeSpawnedNpcs = [4900005, 4901011, 4901012, 4900022, 4903002, 4900010, 4901021, 4901022,
        SiegeLeftLadderNpc, SiegeLadderNpc, SiegeRamNpc, SiegeArcherNpc, SiegeGuideArrowNpc];

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
    /// SiegeGateRulesAsync), the attackers' wall revive marker from the inner city on, a ladder or the ram once its
    /// refresh time is over (TerritoryWarsService.SiegeUnits.cs).</summary>
    private bool SiegePointStands(SiegeRunState r, int point) => point switch
    {
        SiegeCampGatePoint => r.Stage == SiegePrepareStage,
        SiegeWallRevive => r.Stage >= SiegeStrongholdStage,
        _ => !r.Refresh.TryGetValue(point, out long[]? refresh) || refresh is not [_, var back] || _clock.Now * 1000 >= back,
    };

    /// <summary>A unit carrying Battle Start Protection takes no damage.</summary>
    private bool SiegeProtected(MonsterUnit m) => _buffs.UnitBuffs(m).Any(b => b.ConfigId == BattleStartProtection);

    /// <summary>(point, npc, grounded) of every spawned castle object, in point order; read once.</summary>
    private (int Point, int Npc, bool Grounded)[]? _siegeObjectRows;

    /// <summary>
    /// NpcLevelPointRespawnConfig rows of _iMapid 2230101 with a spawned NPC; _iNotGroundBorn 1 (the guide arrows) keeps
    /// the editor's height. Row 4900016 places the revive point 4900010 at 45, which is a group id and no point of the
    /// scene; UIModule_NpcData puts that NPC at point 286 (inferred: 286 is meant). (inferred) Left out: the rows at a
    /// point the scene editor leaves at the origin (two guide arrows at 16 and 272: m_kAllPoints entries at 0,0,0 and no
    /// spawn point), and the second ladder at 20 and the second ram at 34, which stand on the very spots of 47 and 48, the
    /// points UIModule_NpcData follows.
    /// </summary>
    private (int Point, int Npc, bool Grounded)[] SiegeObjectRows()
    {
        if (_siegeObjectRows != null) return _siegeObjectRows;
        var map = _maps.Map(SiegeScene) ?? throw new InvalidOperationException("Siege scene 2230101 is absent");
        return _siegeObjectRows = [.. NpcLevelPointRespawnConfig.All(_data)
            .Where(r => r.Mapid == SiegeScene && SiegeSpawnedNpcs.Contains(r.Npcid))
            .Select(r => ((int)(r.Npcid == SiegeReviveNpc && r.Respawnpointid == SiegeReviveRowPoint ? SiegeRevivePoint
                : r.Respawnpointid), (int)r.Npcid, r.NotGroundBorn == 0))
            .Where(r => r.Item1 != SiegeContestedAttackerRevive && !SiegeDuplicatePoints.Contains(r.Item1)
                && !(map.Points.TryGetValue(r.Item1, out var at) && at.X == 0 && at.Y == 0 && at.Z == 0))
            .OrderBy(r => r.Item1)];
    }

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
        bool spawned = false, refreshed = false;
        foreach (var (point, npc, grounded) in SiegeObjectRows())
        {
            if (battle.Objects.ContainsKey(point) || battle.State.Destroyed.Contains(point)
                || !SiegePointStands(battle.State, point)) continue;
            if (!map.Points.TryGetValue(point, out var at))
                throw new InvalidOperationException($"Siege object point {point} is absent from scene 2230101");
            if (await _instances.SpawnAsync(battle.Room, npc, at, customTemplate: SiegeTemplate(npc), grounded: grounded)
                is not { } actor) break; // the room closed
            if (battle.State.ObjectHp.TryGetValue(point, out long hp) && hp > 0) actor.Hp = Math.Min(actor.Hp, hp);
            SiegeHoldsDamage(actor);
            battle.Objects[point] = actor;
            // A ladder or the ram back from its refresh, or set off again from its start in a reopened room.
            refreshed |= battle.State.Refresh.Remove(point) | battle.State.PlacedLadders.Remove(point);
            spawned = true;
        }
        if (refreshed) await SaveAsync(state);
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

    /// <summary>
    /// A castle object died (CombatService.Killed, or found dead by the siege clock): it stays destroyed, or a ladder or
    /// the ram waits for its refresh and leaves the room's objects once its death went out (30115 hp 0). A guardian's
    /// death adds time to the stage's countdown (TerritoryWarsService.SiegeUnits.cs).
    /// </summary>
    private async Task SiegeObjectDestroyedAsync(SiegeRun battle, int point)
    {
        var r = battle.State;
        if (r.Destroyed.Contains(point) || r.Refresh.ContainsKey(point)) return;
        int stage = r.Stage;
        var walls = SiegeActiveWalls(r).ToHashSet();
        bool refresh = SiegeRefreshes(r, point);
        if (refresh) r.Refresh[point] = SiegeRefreshWindow(point);
        else r.Destroyed.Add(point);
        r.PlacedLadders.Remove(point);
        r.ObjectHp.Remove(point);
        await SaveAsync(state);
        await SiegePublishObjectsAsync(battle);
        if (refresh)
        {
            battle.Objects.Remove(point);
            battle.SentHp.Remove(point);
        }
        await SiegeAdvanceStageAsync(battle);
        bool added = SiegeKillAddsTime(r, point);
        if (added) await SaveAsync(state);
        // A barricade, a fortress or a ladder changes the walls without a new stage (var 133), a guardian's kill the
        // countdown (542) and a refresh the countdowns of the ladders and the ram (npcRefreshModuleData).
        if (r.Stage != stage && !added) return;
        if (added || refresh || !walls.SetEquals(SiegeActiveWalls(r))) await SiegeBroadcastBaseAsync(battle);
    }

    /// <summary>The siege clock: castle objects that died without a kill callback (their killer left meanwhile), the HP
    /// they lost to anything but a player's blow (the ram, the archers; a player's is recorded with it), then the HP
    /// changes of the last second (30115).</summary>
    private async Task SiegeObjectsClockAsync(SiegeRun battle)
    {
        foreach (var (point, unit) in battle.Objects.ToArray())
        {
            if (point == SiegeCrystalPoint) continue;
            if (unit.Dead) await SiegeObjectDestroyedAsync(battle, point);
            else if (unit.Hp < unit.Template.MaxHp && battle.State.ObjectHp.GetValueOrDefault(point) != unit.Hp)
            {
                battle.State.ObjectHp[point] = unit.Hp;
                _siegeDirty = true;
            }
        }
        await SiegePublishObjectsAsync(battle);
    }
}
