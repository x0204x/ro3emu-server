using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// 30129 GLOBAL_SCENE_POINT_DATA_NOTIFTY: the stronghold battle's fifteen gameplay points. The point list, their wire
/// state and the 4905010 marker actors are reconstructed from client data; what each object does is not in the client
/// and is not invented here (docs/systems/territory-wars.md, "Stronghold gameplay points").
/// </summary>
public sealed partial class TerritoryWarsService
{
    public const uint ScenePointNotify = 30129;
    private const long StrongholdScene = 3030101;
    /// <summary>UnitConfig 4905010 "据点战-交互点" (stronghold battle interaction point): NPCConfig MutiInteractMainNpc
    /// (_iNPCSubType 250), unselectable, no name or HP bar, _iNeedNotifyGlobalData 1.</summary>
    private const int ScenePointNpc = 4905010;
    private const long ScenePointUidBase = 1L << 53;
    private long _scenePointSerial;

    /// <summary>
    /// Config/Dungeon/DungeonLua_3030101.lua UIModule_NpcData {spawnPointId: [4905010, eObjectType]}, read by
    /// LC_SceneCom_UIModule_TerritoryWars.lua:40-90. Types are LC_TerritoryWars_GameplayPointHelper.lua
    /// enum_GameplayPointType: 7 SteleBarrier, 8 DivinityStatue, 9 SummonPlatform, 10 BossPlatform, 11 AirWallGate,
    /// 12 RevivalPoint. No other DungeonLua declares types 7-12, so only this scene gets 30129.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, int> StrongholdGameplayPoints = new Dictionary<int, int>
    {
        [34] = 7, [41] = 8, [49] = 9, [51] = 10, [55] = 12, [56] = 12, [57] = 9, [59] = 7,
        [63] = 11, [66] = 11, [74] = 7, [81] = 12, [83] = 9, [85] = 12, [86] = 12,
    };

    /// <summary>One GlobalScenePointData (MG_Dungeon.proto:362-367): status is EnumGlobalScenePoint, ReadyTime is
    /// epoch milliseconds (UI_Sys_TerritoryWars_MapWidgetCtrl.lua:462-470 divides it by 1000 against GetSystemTime).</summary>
    public sealed class ScenePoint
    {
        public int Id { get; set; }
        public int Type { get; set; }
        public int Status { get; set; }
        public long ReadyTime { get; set; }
        public long Owner { get; set; }
    }

    /// <summary>The marker actor at a gameplay point: NPCType 1, so the client creates it as a monster-type entity
    /// (as DungeonService.SceneActorUnit), carrying its template HP so it is not shown dead.</summary>
    internal sealed class ScenePointUnit : WorldUnit
    {
        public required int PointId { get; init; }
        public MonsterTemplate? Template { get; init; }
        public long BornMs { get; init; }

        public override EntityBase Entity()
        {
            var entity = new EntityBase
            {
                Uuid = (ulong)Uid, EntityTemplateID = ScenePointNpc, EntityType = 1, Face = Face, State = EntityState.Alive,
                BornTimeMs = BornMs, NotifyType = EntityBase.Types.NotifyType.All, EntityCamp = Template?.Camp ?? 1,
                MovementInfo = new MovementInfo
                {
                    Pos = Pos, Face = Face, ServerTime = MonsterService.NowMs,
                    MoveType = MovementType.MoveTypeNone
                },
            };
            if (Template is { } t)
                foreach (var (idx, value) in new (long, long)[] { (101, t.MaxHp), (2000, t.MaxHp), (2002, t.Level) })
                    entity.Property.Add(new AoIProperty { Idx = idx, Data = value, BaseData = value });
            return entity;
        }
    }

    /// <summary>
    /// The initial state of every point (inferred): ready, no countdown, no owner. The client treats ready and an
    /// unReady point without readyTime alike (Available, LC_DAL_TerritoryWars.lua:3238-3256 and
    /// LC_TerritoryWars_GameplayPointHelper.lua:160-190); which objects start unavailable or owned is not exported.
    /// </summary>
    private static Dictionary<int, ScenePoint> DefaultScenePoints(long scene) => scene != StrongholdScene ? new()
        : StrongholdGameplayPoints.ToDictionary(p => p.Key, p => new ScenePoint
        {
            Id = p.Key, Type = p.Value,
            Status = (int)EnumGlobalScenePoint.Ready
        });

    /// <summary>A record saved before 30129 existed has no stored points and reads the defaults.</summary>
    private static IReadOnlyDictionary<int, ScenePoint> ScenePointsOf(BattleState r) =>
        r.ScenePoints.Count > 0 ? r.ScenePoints : DefaultScenePoints(r.Scene);

    private static GlobalScenePointDataNotify ScenePointMessage(BattleState r, IReadOnlyCollection<int>? only = null)
    {
        var msg = new GlobalScenePointDataNotify();
        foreach (var p in ScenePointsOf(r).Values.Where(p => only == null || only.Contains(p.Id)).OrderBy(p => p.Id))
            msg.GlobalPoints[p.Id] = new GlobalScenePointData
            {
                PointId = p.Id, Status = (EnumGlobalScenePoint)p.Status,
                ReadyTime = p.ReadyTime, BelongGuildId = p.Owner
            };
        return msg;
    }

    private static IEnumerable<GameSession> LoadedMembers(Battle b) => b.Room.ActiveMembers.Select(m => m.Session)
        .Where(s => ReferenceEquals(s.TerritoryBattle, b) && s.InstanceId == b.Room.Id && !s.SceneLoading);

    /// <summary>
    /// The full point set on every load of the battle scene: the client deletes TerritoryWarsScenePointInfo on each
    /// scene switch (LC_DAL_TerritoryWars.lua:500-510) and never asks for it (30129 has no client send site).
    /// </summary>
    private async Task SendScenePointsAsync(GameSession s, Battle b)
    {
        if (ScenePointsOf(b.State).Count == 0) return;
        await Send(s, ScenePointNotify, ScenePointMessage(b.State));
        await SendScenePointObjectsAsync(s, b);
    }

    /// <summary>
    /// 30115 entries for the markers (NPCConfig 4905010 _iNeedNotifyGlobalData 1). The client finds a revival point's
    /// position and its Self/Enemy/Neutral owner only through the GlobalObjectData at that spawn point
    /// (UI_Sys_TerritoryWarsDungeon_MapContainerWidgetCtrl.lua:470-480, LC_TerritoryWars_GameplayPointHelper.lua:196-251),
    /// since revival points are neither monster spawn points nor air walls of the client scene. hp must stay above zero
    /// or the client drops the entry (LC_SceneCom_GlobalObjMgr.lua:82-88).
    /// </summary>
    private Task SendScenePointObjectsAsync(GameSession s, Battle b, IReadOnlyCollection<int>? only = null)
    {
        var points = ScenePointsOf(b.State);
        var msg = new GlobalObjectDataNotify();
        foreach (var (pointId, unit) in b.ScenePointActors)
        {
            if (only != null && !only.Contains(pointId) || !points.TryGetValue(pointId, out var p)) continue;
            long hp = unit.Template?.MaxHp ?? 1;
            msg.GlobalDatas[unit.Uid] = new GlobalObjectData
            {
                GlobalObjId = unit.Uid, ConfigId = ScenePointNpc,
                Pos = new Vec3 { X = unit.X, Y = unit.Y, Z = unit.Z }, Hp = hp, MaxHp = hp, DungeonPointId = pointId,
                BelongGuild = p.Owner
            };
        }
        return msg.GlobalDatas.Count == 0 ? Task.CompletedTask : Send(s, GlobalObjectNotify, msg);
    }

    /// <summary>
    /// One 4905010 marker per UIModule_NpcData point at its scene-editor point (m_kAllPoints of
    /// sc_gvg_lingtuzhan_001_1_dg01). The npc column naming 4905010 for all fifteen points, the unit's name and its
    /// global-data flag make the marker the point's actor (inferred: no spawn row names it, unlike the guardians,
    /// stele, vehicles and vanguard of NpcLevelPointRespawnConfig 4905001-4905011). Markers are props, never targets.
    /// </summary>
    private async Task SpawnScenePointsAsync(Battle b)
    {
        if (b.Room.Closed || b.State.Settled || _maps.Map(b.State.Scene) is not { } map) return;
        var added = new List<int>();
        foreach (var pointId in ScenePointsOf(b.State).Keys.Order())
        {
            if (b.ScenePointActors.ContainsKey(pointId) || !map.Points.TryGetValue(pointId, out var at)) continue;
            var unit = new ScenePointUnit
            {
                Uid = ScenePointUidBase | Interlocked.Increment(ref _scenePointSerial), SceneEntry = b.Room.SceneEntry,
                InstanceId = b.Room.Id, X = at.X, Y = at.Y, Z = at.Z, Face = at.Face, PointId = pointId,
                Template = _monsters.Template(ScenePointNpc), BornMs = MonsterService.NowMs,
            };
            if (!await _instances.AddPropAsync(b.Room, unit)) return;
            b.ScenePointActors[pointId] = unit;
            added.Add(pointId);
        }
        // A member may finish loading while the markers spawn; it already received the point list.
        if (added.Count > 0)
            await RoleFanout.EachAsync(LoadedMembers(b), s => SendScenePointObjectsAsync(s, b, added));
    }

    /// <summary>
    /// Changes one point and publishes only that point: the client merges 30129 into its dataset (Update,
    /// LC_DAL_TerritoryWars.lua:3270-3276) and refreshes the map widgets. The marker's 30115 belongGuild follows the
    /// owner, which revival points read. No original rule that changes a point is known; the only caller is the
    /// !twpoint test aid.
    /// </summary>
    private async Task SetScenePointAsync(Battle b, int pointId, EnumGlobalScenePoint status, long readyTime, long owner)
    {
        if (b.State.ScenePoints.Count == 0) b.State.ScenePoints = DefaultScenePoints(b.State.Scene);
        if (!b.State.ScenePoints.TryGetValue(pointId, out var p)) return;
        if (p.Status == (int)status && p.ReadyTime == readyTime && p.Owner == owner) return;
        p.Status = (int)status; p.ReadyTime = readyTime; p.Owner = owner;
        await SaveAsync(state);
        var delta = ScenePointMessage(b.State, [pointId]);
        await RoleFanout.EachAsync(LoadedMembers(b), async s =>
        { await Send(s, ScenePointNotify, delta); await SendScenePointObjectsAsync(s, b, [pointId]); });
    }

    /// <summary>Test aid, not original behaviour: sets a point of the caller's stronghold battle so the client's map
    /// icons, owner colours and countdown can be checked. Usage: point unready|ready|complete [readySeconds] [owner|self].</summary>
    public async Task<string> ScenePointGmAsync(GameSession s, string[] args)
    {
        if (args.Length is < 2 or > 4 || !int.TryParse(args[0], out int pointId)
            || args[1].ToLowerInvariant() is not ("unready" or "ready" or "complete"))
            throw new ArgumentException("point unready|ready|complete [readySeconds] [owner|self]");
        var status = args[1].ToLowerInvariant() switch
        {
            "unready" => EnumGlobalScenePoint.UnReady,
            "ready" => EnumGlobalScenePoint.Ready,
            _ => EnumGlobalScenePoint.Complete,
        };
        long seconds = args.Length > 2 ? long.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 0;
        using var reservation = await EnterGateAsync("territory/scene point gm", s);
        using var stagedTerritoryState = StageState();
        try
        {
            if (s.TerritoryBattle is not { } b || s.InstanceId != b.Room.Id) return "Not in a territory battle.";
            if (!ScenePointsOf(b.State).TryGetValue(pointId, out var current))
                return $"No gameplay point {pointId} here: {string.Join(" ", ScenePointsOf(b.State).Keys.Order())}";
            long owner = args.Length < 4 ? current.Owner : args[3].Equals("self", StringComparison.OrdinalIgnoreCase)
                ? Own(s)?.Id ?? 0 : long.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
            long readyTime = seconds > 0 ? (_clock.Now + seconds) * 1000 : 0;
            await SetScenePointAsync(b, pointId, status, readyTime, owner);
            return $"Point {pointId}: {status}, readyTime {readyTime}, owner {owner}.";
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
    }
}
