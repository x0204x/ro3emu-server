using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The siege's moving and fighting castle units (docs/systems/territory-wars.md "Ladders, the ram and the archers"): the
/// attackers' siege ladders 4900012 (point 46) and 4900000 (47) and their Battering Ram 4900002 (48), the defenders' 38
/// archers 4901001 (group 248 "刷守卫", tag 3004), and what the dungeon script of DungeonLua_2230101 adds to them:
/// UIModule_NpcRefreshDuration (the ladders and the ram come back) and UIModule_KillAddTimeList (a guardian's kill adds to
/// the stage's countdown). The NPCs' AI scripts (GVG_S0_Ladder, GVG_S0_LeftLadder, GVG_S0_SiegeTrain, GVG_S0_Archer)
/// run on the original server; the client holds none of them, so how the units move and strike is reconstructed from
/// their data and the scene and marked so.
/// </summary>
public sealed partial class TerritoryWarsService
{
    private const int SiegeLeftLadderNpc = 4900012, SiegeLadderNpc = 4900000, SiegeRamNpc = 4900002, SiegeArcherNpc = 4901001,
        SiegeGuideArrowNpc = 4903001;
    /// <summary>The ram's point (UIModule_NpcData "48" Car) and its end, 35 "冲车-终点", 10 m before the gate at 36.</summary>
    private const int SiegeRamPoint = 48, SiegeRamEnd = 35;
    /// <summary>NpcLevelPointRespawnConfig places a second ladder 4900000 at 20 and a second ram at 34: the same spots as
    /// 47 and 48 (inferred: one unit each, at the point the UI follows).</summary>
    private static readonly HashSet<int> SiegeDuplicatePoints = [20, 34];

    /// <summary>
    /// A ladder: its point, the end it is carried to (the scene editor's "云梯N-终点", at the city wall), the city wall's
    /// air wall there and the two rails of its lane. 46 "云梯1" (4900012, z 93) goes to 18 at the wall 65 "空气墙_左中"
    /// between the rails 67/68 "空气墙_左车"; 47 "云梯2" (4900000, z 207) to 30 at 19 "空气墙_右中" between 32/33
    /// "空气墙_右车" (the editor's names cross 1 and 2; the positions pair them).
    /// </summary>
    private sealed record SiegeLadder(int Point, int End, long Wall, long[] Lane);

    private static readonly SiegeLadder[] SiegeLadders = [new(46, 18, 65, [67, 68]), new(47, 30, 19, [32, 33])];

    /// <summary>DungeonLua_2230101 UIModule_NpcRefreshDuration (not exported to config): 46/47 [70000, 75000] and 48 [20000,
    /// 60000] ms, every other point [0]. (inferred) A unit comes back after a time drawn between the two.</summary>
    private static readonly Dictionary<int, (int Min, int Max)> SiegeRefreshMs = new()
    {
        [46] = (70000, 75000), [47] = (70000, 75000), [SiegeRamPoint] = (20000, 60000),
    };

    /// <summary>
    /// DungeonLua_2230101 UIModule_KillAddTimeList "0": [120, 300]. The top bar's info tips show them as UIWords 61140
    /// "Defeat the Archer Guardian +${1} sec." and 61141 "Defeat the Knight Guardian +${1} sec."
    /// (UI_Sys_Siege_TopBarWidgetCtrl._onInfoClick, kKillAddTimeList[1] and [2]): the Bow Guardians 4901012 (38-40,
    /// OuterTower) and the Knight Guardians 4901011 (41-43, InnerTower).
    /// </summary>
    private static readonly long[] SiegeKillAddSeconds = [120, 300];

    /// <summary>
    /// (inferred) The ram is pushed: it moves while an attacker stands within its born buff 4900301's circle
    /// (BuffBehaviorConfig fx_gvg_rangecircle_green, zoom 480000) and waits otherwise. The guardians carry the same circle
    /// at zoom 225000 for the 15 m of their scan skill 4904001 (_kRange 1500), so this one is 32 m.
    /// </summary>
    private const float SiegeRamEscortRadius = 15f * 480000 / 225000;
    private const float SiegeArriveRadius = 1f;

    /// <summary>The archers: (inferred) a static guard with AIConfig 4900003's 45 m view and attack distance shoots the
    /// attackers it sees without waiting for a blow, and lets go of one that left its reach. Every other castle unit keeps
    /// its NPCConfig template.</summary>
    private MonsterTemplate? SiegeTemplate(int npc) => npc == SiegeArcherNpc && _monsters.Template(npc) is { } t
        ? t with { Aggressive = true, LeashDistance = t.AttackRange } : null;

    /// <summary>MonsterService.Spares: a castle unit of camp 1 or 2 (NPCConfig) takes only the other camp's siege members
    /// as targets.</summary>
    private bool SiegeSpares(MonsterUnit m, GameSession p) =>
        m.SceneEntry == SiegeScene && m.InstanceId != 0 && NPCConfig.Get(_data, m.Template.NpcId) is { Camp: 1 or 2 } npc
        && !(p.SiegeBattle is { } battle && battle.Room.Id == m.InstanceId && CampOf(p) != npc.Camp);

    /// <summary>Whether the castle object at <paramref name="point"/> comes back after its death: the ladders always, the
    /// ram while the gate stands (inferred: it has nothing to strike after).</summary>
    private static bool SiegeRefreshes(SiegeRunState r, int point) =>
        SiegeRefreshMs.ContainsKey(point) && !(point == SiegeRamPoint && r.Destroyed.Contains(SiegeGatePoint));

    /// <summary>[death, return] in epoch ms, from now.</summary>
    private long[] SiegeRefreshWindow(int point)
    {
        long now = _clock.Now * 1000;
        var (min, max) = SiegeRefreshMs[point];
        return [now, now + Random.Shared.Next(min, max + 1)];
    }

    /// <summary>
    /// 30401 npcRefreshModuleData (DungeonNpcRefreshModuleData, field 14): the units waiting to come back, by point,
    /// with their death and return in epoch ms. LC_SceneCom_UIModule_Siege._processDungeonEvent replaces its
    /// kRefreshTimeMap with it, and LC_SiegeHelper shows a dead object of such a point as a countdown
    /// (Common_Countdown) instead of destroyed. Always sent, so a unit that came back leaves the map.
    /// </summary>
    private static DungeonNpcRefreshModuleData SiegeRefreshData(SiegeRunState r)
    {
        var data = new DungeonNpcRefreshModuleData();
        foreach (var (point, window) in r.Refresh)
            if (window is [var start, var end])
                data.NpcRefreshTime[point] = new NpcRefreshTime { NpcPointId = point, RefreshStartTime = start, RefreshEndTime = end };
        return data;
    }

    /// <summary>
    /// A guardian's death adds UIModule_KillAddTimeList's seconds to the countdown running after it (inferred: to the
    /// stage's, var 542, whose second value the top bar shows as "+N S"; Prepare has none). The addition ends where the
    /// siege ends; one that moved nothing is not shown. True when the countdown moved.
    /// </summary>
    private static bool SiegeKillAddsTime(SiegeRunState r, int point)
    {
        if (r.Stage < SiegeGateStage || !SiegeNpcData.TryGetValue(point, out var type)) return false;
        long seconds = type switch
        {
            SiegeObjectType.OuterTower => SiegeKillAddSeconds[0],
            SiegeObjectType.InnerTower => SiegeKillAddSeconds[1],
            _ => 0,
        };
        if (seconds == 0) return false;
        long before = SiegeStageEnd(r);
        r.StageAdded += seconds;
        long moved = SiegeStageEnd(r) - before;
        if (moved <= 0) return false;
        r.LastAdded = moved * 1000;
        return true;
    }

    /// <summary>
    /// The siege clock's turn for the units, from the Gate stage on: the ladders and the ram set off for their ends
    /// (MonsterService.March over the navigation grid; inferred: they wait out Prepare at their points, as the attackers
    /// do in their camp), a ladder at its end stands against the wall (its lane opens, var 133), the ram moves while
    /// escorted and strikes the gate from its end, and the archers shoot the ladders they reach.
    /// </summary>
    private async Task SiegeUnitsClockAsync(SiegeRun battle)
    {
        var r = battle.State;
        if (r.Stage < SiegeGateStage || battle.Room.Closed || _maps.Map(SiegeScene) is not { } map) return;
        bool placed = false;
        foreach (var ladder in SiegeLadders)
        {
            if (battle.Objects.GetValueOrDefault(ladder.Point) is not { Dead: false } unit) continue;
            SiegeSetOff(unit, map, ladder.End);
            if (unit.March is { Arrived: true } && !r.PlacedLadders.Contains(ladder.Point))
            {
                r.PlacedLadders.Add(ladder.Point);
                placed = true;
            }
        }
        if (battle.Objects.GetValueOrDefault(SiegeRamPoint) is { Dead: false } ram)
        {
            SiegeSetOff(ram, map, SiegeRamEnd);
            if (ram.March is { Arrived: false }) await _monsters.HoldMarchAsync(ram, !SiegeRamEscorted(battle, ram));
            await SiegeRamStrikesAsync(battle, ram);
        }
        await SiegeArchersShootAsync(battle);
        if (!placed) return;
        await SaveAsync(state);
        await SiegeBroadcastBaseAsync(battle);
    }

    private static void SiegeSetOff(MonsterUnit unit, MapWorld map, int end)
    {
        if (unit.March != null || !map.Points.TryGetValue(end, out var goal)) return;
        unit.March = new MonsterMarch(goal.X, goal.Z, SiegeArriveRadius) { LeashX = unit.X, LeashZ = unit.Z };
    }

    private static bool SiegeRamEscorted(SiegeRun battle, MonsterUnit ram) => SiegeLoadedMembers(battle).Any(s =>
        !s.Dead && CampOf(s) == 1 && MonsterService.Distance(ram, s) <= SiegeRamEscortRadius);

    /// <summary>
    /// (inferred) The ram at its end strikes the City Gate once per AIConfig 4900002 _iAttackTickTime (1000 ms) while the
    /// gate stands and has lost its Battle Start Protection. NPCConfig 4900002 has no skill but its _kAttriScale raises
    /// its PATK (103) and MATK (104) ninetyfold, so each blow deals its ATK (210 240 at level 59), as a monster's blow
    /// without a skill row. It reaches the gate from its end point 35 (10.4 m away) across both models' _iPrefabRadius
    /// (1000 + 500 cm). The gate it breaks falls as by a player's blow, without AttackCityValue (nobody struck it).
    /// </summary>
    private async Task SiegeRamStrikesAsync(SiegeRun battle, MonsterUnit ram)
    {
        if (ram.March is not { Arrived: true } || battle.Objects.GetValueOrDefault(SiegeGatePoint) is not { Dead: false } gate
            || SiegeProtected(gate)) return;
        long now = MonsterService.NowMs;
        if (now < ram.NextSwingMs) return;
        long tick = NPCConfig.Get(_data, SiegeRamNpc) is { } npc && AIConfig.Get(_data, npc.Ai) is { AttackTickTime: > 0 } ai
            ? ai.AttackTickTime : 1000;
        ram.NextSwingMs = now + tick;
        float reach = ((NPCConfig.Get(_data, SiegeRamNpc)?.PrefabRadius ?? 0) + (NPCConfig.Get(_data, gate.Template.NpcId)?.PrefabRadius ?? 0)) / 100f;
        if (!await _combat.MonsterStrikeAsync(ram, gate, reach, damage => damage)) return;
        if (gate.Dead) await SiegeObjectDestroyedAsync(battle, SiegeGatePoint);
    }

    /// <summary>
    /// (inferred) An archer that holds no player shoots a ladder within its attack's reach (SkillConfig 4905101, 50 m),
    /// its normal attack at its pace: PublicParamConfig GuardAttackOtherReduceDamage [3004, 3005, -5000] halves an archer's
    /// (tag 3004) damage on a ladder (3005), so archers fight ladders; the ladders against the wall stand within reach of
    /// the archers on it (points 76-89, 18-23 m). A player in sight comes first (the archers are aggressive).
    /// </summary>
    private async Task SiegeArchersShootAsync(SiegeRun battle)
    {
        long now = MonsterService.NowMs;
        var ladders = SiegeLadders.Select(l => battle.Objects.GetValueOrDefault(l.Point)).OfType<MonsterUnit>()
            .Where(l => !l.Dead && l.Hp > 0).ToList();
        if (ladders.Count == 0) return;
        foreach (var archer in battle.Objects.Values.Where(u => u.Template.NpcId == SiegeArcherNpc).ToList())
        {
            if (archer.Dead || archer.Hp <= 0 || archer.Target != null || archer.BusyUntilMs > now || now < archer.NextSwingMs) continue;
            var ladder = ladders.Where(l => MonsterService.Distance(archer.X, archer.Z, l.X, l.Z) <= archer.Template.AttackRange)
                .MinBy(l => MonsterService.Distance(archer.X, archer.Z, l.X, l.Z));
            if (ladder == null) continue;
            archer.NextSwingMs = now + archer.Template.SwingIntervalMs;
            long[] from = NPCConfig.Get(_data, archer.Template.NpcId)?.NpcTag ?? [], to = NPCConfig.Get(_data, ladder.Template.NpcId)?.NpcTag ?? [];
            await _combat.MonsterStrikeAsync(archer, ladder, archer.Template.AttackRange, damage => TagDamage(from, to, damage));
        }
    }

    /// <summary>
    /// SceneService.WalkBridged: (inferred) a ladder against the wall carries its climbers over the city wall's blocked
    /// cells (the navigation grid has the wall top at 29 m and the ground at 14 m with no walkable cells between): a
    /// siege member's walk that starts or ends in a standing ladder's lane, the box of its two rails, is not held to the
    /// grid. Its rails still hold the climber in the lane (var 133).
    /// </summary>
    private bool SiegeWalkBridged(GameSession s, Vec3 from, Vec3 to)
    {
        if (s.SiegeBattle is not { Room.Closed: false } battle || s.SceneEntry != SiegeScene || _maps.Map(SiegeScene) is not { } map)
            return false;
        foreach (var ladder in SiegeLadders)
        {
            if (!battle.State.PlacedLadders.Contains(ladder.Point)) continue;
            var cells = ladder.Lane.Select(id => map.AirWalls.GetValueOrDefault(id)).OfType<SceneAirWall>().SelectMany(w => w.Cells).ToList();
            if (cells.Count == 0) continue;
            int minX = cells.Min(c => c.X), maxX = cells.Max(c => c.X), minZ = cells.Min(c => c.Z), maxZ = cells.Max(c => c.Z);
            bool In(Vec3 p) => p.X >= minX && p.X < maxX + 1 && p.Z >= minZ && p.Z < maxZ + 1;
            if (In(from) || In(to)) return true;
        }
        return false;
    }
}
