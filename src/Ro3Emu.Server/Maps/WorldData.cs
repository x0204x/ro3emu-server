using System.Text.Json;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Maps;

/// <summary>A server-chosen spot on a map: position and facing in degrees.</summary>
public readonly record struct MapPoint(float X, float Y, float Z, float Face);

/// <summary>NpcTag of the scene-editor kNpcList (enum_NpcTagType): what a world NPC entry is.</summary>
public enum WorldNpcKind { Npc = 2, Portal = 26, Kafra = 28 }

/// <summary>
/// A unit the server spawns on a map: town NPC, portal or Kafra, at the scene-editor
/// position verbatim (the client finds a portal's trigger by hashing that position).
/// </summary>
public sealed record WorldNpc(long Uid, long SceneEntry, WorldNpcKind Kind, int NpcId, string Name, int Camp, float X, float Y, float Z, float Face, JsonElement Entry)
{
    /// <summary>Portal: target map and travel point (TravelTargetInfoArray[0]).</summary>
    public (long Map, int TravelPoint)? Target =>
        Kind == WorldNpcKind.Portal && Entry.TryGetProperty("TravelTargetInfoArray", out var targets) && targets.ValueKind == JsonValueKind.Array && targets.GetArrayLength() > 0
            ? (GameData.Int(targets[0], "MapID"), (int)GameData.Int(targets[0], "TravelPointID"))
            : null;

    /// <summary>Where a player lands when this portal / Kafra point is the destination: position + FootholdPos, face FootholdPosRot.</summary>
    public MapPoint Arrival => new(
        X + Float(Entry, "FootholdPos_x"), Y + Float(Entry, "FootholdPos_y"), Z + Float(Entry, "FootholdPos_z"), Float(Entry, "FootholdPosRot"));

    internal static float Float(JsonElement e, string field) =>
        e.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : 0f;
}

/// <summary>A monster spawn point (scene-editor m_kWorldMonsterSpawnPoints).</summary>
public sealed record SpawnPoint(int Id, float X, float Y, float Z, float Face, int ConfigId);

/// <summary>
/// A monster spawn group (m_kWorldMonsterSpawnGroups): a mixed pool (Limits per NPCConfig id,
/// Total alive) or fixed points (each point's own config id, Total alive at once), respawning RespawnSeconds after a
/// corpse is removed.
/// </summary>
public sealed record SpawnGroup(int Id, bool Pool, int Total, int RespawnSeconds, IReadOnlyList<(int NpcId, int Min, int Max)> Limits, IReadOnlyList<SpawnPoint> Points);

/// <summary>
/// A scene-editor trigger region (m_kRegins): centre, footprint and its action string ("op|arg|..."). The client reports
/// entering it with 31209 only when its first action is not client-hosted (LC_System_Task.lua:2257-2321).
/// </summary>
public sealed record MapRegion(long Id, float X, float Y, float Z, float Width, float Length, string Actions,
    int Shape = 2, float Direction = 0)
{
    /// <summary>
    /// LC_AreaHelper.CheckInArea's footprint (the y test is not applied): shapeType 1 is a circle of radius length,
    /// 2 a rectangle, length along x and width along z once the offset is turned by Quaternion.Euler(0, -direction, 0).
    /// The decompiled rectangle test shows only the length half; the width half is (inferred).
    /// </summary>
    public bool Contains(float x, float z)
    {
        float dx = x - X, dz = z - Z;
        if (Shape == 1) return dx * dx + dz * dz < Length * Length;
        double turn = -Direction * Math.PI / 180;
        double lx = dx * Math.Cos(turn) + dz * Math.Sin(turn), lz = -dx * Math.Sin(turn) + dz * Math.Cos(turn);
        return Math.Abs(lx) < Length / 2 && Math.Abs(lz) < Width / 2;
    }
}

/// <summary>A scene-editor point linked to a RefreshInfo-backed life collection group.</summary>
public sealed record GatheringSpawnPoint(int Id, int GroupId, int RefreshId, MapPoint Position,
    IReadOnlyList<(int NpcId, int Weight)> Candidates, int MinimumPopulation, int TargetPopulation, int RefreshParameter);

/// <summary>Everything the server knows about one map: points, spawned units and the walkability grid.</summary>
public sealed class MapWorld
{
    public required long SceneEntry { get; init; }
    public MapPoint? Born { get; init; }
    public MapPoint? Revive { get; init; }
    public IReadOnlyList<WorldNpc> Npcs { get; init; } = [];
    public IReadOnlyDictionary<long, MapPoint> Points { get; init; } = new Dictionary<long, MapPoint>();
    public NavGrid? Nav { get; init; }
    public IReadOnlyList<SpawnGroup> SpawnGroups { get; init; } = [];
    public IReadOnlyList<GatheringSpawnPoint> GatheringPoints { get; init; } = [];
    public IReadOnlyDictionary<long, MapRegion> Regions { get; init; } = new Dictionary<long, MapRegion>();

    /// <summary>Dungeon m_kGroups joined to m_kMonsterSpawnPoints; templates/timing belong to PetFloorConfig.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<SpawnPoint>> DungeonGroups { get; init; } = new Dictionary<int, IReadOnlyList<SpawnPoint>>();

    public WorldNpc? Portal(int travelPointId) =>
        Npcs.FirstOrDefault(n => n.Kind == WorldNpcKind.Portal && GameData.Int(n.Entry, "TravelPointID") == travelPointId);

    /// <summary>Kafra teleport arrival: the map's Kafra point flagged IsKaPuLaPos (+ foothold), moved onto walkable ground.</summary>
    public MapPoint? KafraArrival =>
        Npcs.FirstOrDefault(n => n.Kind == WorldNpcKind.Kafra && n.Entry.TryGetProperty("IsKaPuLaPos", out var k) && k.ValueKind == JsonValueKind.True)?.Arrival
            is { } arrival ? Standable(arrival) : null;

    /// <summary>The point with y moved onto the walkable layer nearest to it (the client takes y as sent).</summary>
    public MapPoint Grounded(MapPoint p) =>
        Nav?.WalkHeight(p.X, p.Z, p.Y) is { } h ? p with { Y = h } : p;

    public bool IsWalkable(float x, float y, float z) => Nav?.IsWalkable(x, y, z) ?? true;

    /// <summary>
    /// A point a player can stand on: the point itself when walkable at its height, else its cell's walkable layer, else
    /// the nearest walkable cell centre within <paramref name="radius"/> m (ring by ring, the layer nearest to y). Scene
    /// editor arrivals can sit inside scenery (#bug-reports: the Kafra warp to Southern Payon lands in a rock). Without
    /// a nav grid, or with no walkable cell near, the point is returned unchanged (inferred).
    /// </summary>
    public MapPoint Standable(MapPoint p, int radius = 8)
    {
        if (Nav is not { } nav || nav.IsWalkable(p.X, p.Y, p.Z)) return p;
        if (nav.WalkHeight(p.X, p.Z, p.Y) is { } h && MathF.Abs(h - p.Y) <= NavGrid.WalkHeightTolerance) return p with { Y = h };
        int cx = (int)MathF.Floor(p.X), cz = (int)MathF.Floor(p.Z);
        for (int r = 1; r <= radius; r++)
        {
            MapPoint? best = null;
            float bestDistance = float.MaxValue;
            for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                    float x = cx + dx + 0.5f, z = cz + dz + 0.5f;
                    if (nav.WalkHeight(x, z, p.Y) is not { } y) continue;
                    float distance = (x - p.X) * (x - p.X) + (z - p.Z) * (z - p.Z) + (y - p.Y) * (y - p.Y);
                    if (distance < bestDistance) { bestDistance = distance; best = p with { X = x, Y = y, Z = z }; }
                }
            if (best is { } found) return found;
        }
        return p;
    }

}

/// <summary>
/// The client's world data: scene-editor tables (data/sceneeditor, MapConfig._kResSceneEditorName)
/// and nav grids (data/nav, MapResource._kNavDataName -> *_dynamicmapdata.bytes), loaded per map on first use.
/// </summary>
public sealed partial class WorldData(IOptions<ServerOptions> options, GameData data, ILogger<WorldData> log)
{
    /// <summary>NPC uids: 1&lt;&lt;40 | sceneEntry&lt;&lt;16 | index, far above role ids.</summary>
    private const long NpcUidBase = 1L << 40;

    /// <summary>NPCConfig._iNPCSubType of the "flying trigger" air portals, skipped until flying exists.</summary>
    private const int SubTypeAirTeleport = 300;

    private readonly Dictionary<long, MapWorld?> _maps = new();
    private readonly object _sync = new();
    private HashSet<long>? _kafraDestinations;

    private string Dir(string sub) => Path.Combine(options.Value.GameDataDir ?? options.Value.DataDir, sub);

    /// <summary>The map, or null for an unknown scene entry.</summary>
    public MapWorld? Map(long sceneEntry)
    {
        lock (_sync)
        {
            if (!_maps.TryGetValue(sceneEntry, out var map)) _maps[sceneEntry] = map = Load(sceneEntry);
            return map;
        }
    }

    private readonly Dictionary<long, (int NpcId, int Point)[]> _pointNpcs = new();

    /// <summary>
    /// Server NPCs a service places on scene-editor monster spawn points whose npcId is 0 (AdventureGroupService: the
    /// creation instance's saplings and mentor). They join the map's NPC list like scene-editor server NPCs.
    /// </summary>
    public void PlacePointNpcs(long sceneEntry, IReadOnlyList<(int NpcId, int Point)> npcs)
    {
        lock (_sync)
        {
            _pointNpcs[sceneEntry] = npcs.ToArray();
            _maps.Remove(sceneEntry);
        }
    }

    /// <summary>The NPC behind a uid (the uid encodes its map).</summary>
    public WorldNpc? Npc(long uid)
    {
        if (uid < NpcUidBase) return null;
        long scene = (uid - NpcUidBase) >> 16;
        int index = (int)(uid & 0xFFFF);
        return Map(scene)?.Npcs is { } npcs && index < npcs.Count ? npcs[index] : null;
    }

    /// <summary>Maps a Kafra can send players to (MapLinkData.Capra).</summary>
    public bool IsKafraDestination(long sceneEntry)
    {
        lock (_sync)
        {
            if (_kafraDestinations == null)
            {
                _kafraDestinations = new();
                if (DataFiles.Find(Dir("sceneeditor"), "MapLinkData.json") is { } path)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                    if (doc.RootElement.TryGetProperty("Capra", out var capra) && capra.ValueKind == JsonValueKind.Object)
                        foreach (var p in capra.EnumerateObject())
                            if (long.TryParse(p.Name, out var id)) _kafraDestinations.Add(id);
                }
                else log.LogWarning("No scene editor MapLinkData.json in {Dir}: every Kafra teleport is refused; run tools/ConfigExport with Config\\SceneEditor",
                    Dir("sceneeditor"));
            }
            return _kafraDestinations.Contains(sceneEntry);
        }
    }

    private MapWorld? Load(long sceneEntry)
    {
        if ((data.Row("MapConfig", sceneEntry) ?? data.Row("DungeonConfig", sceneEntry)) is not { } row) return null;
        var map = new MapWorld { SceneEntry = sceneEntry, Nav = LoadNav(row) };
        if (!row.TryGetProperty("_kResSceneEditorName", out var seName) || seName.GetString() is not { Length: > 0 } name) return map;
        if (DataFiles.Find(Dir("sceneeditor"), name + ".json") is not { } path)
        {
            log.LogWarning("No scene editor {Path} for map {Scene}; run tools/ConfigExport with Config\\SceneEditor",
                Path.Combine(Dir("sceneeditor"), name + ".json"), sceneEntry);
            return map;
        }
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var se = doc.RootElement.Clone();

        MapPoint? Point(JsonElement p, string faceField) =>
            p.TryGetProperty("position", out var pos)
                ? new MapPoint(WorldNpc.Float(pos, "x"), WorldNpc.Float(pos, "y"), WorldNpc.Float(pos, "z"), WorldNpc.Float(p, faceField))
                : null;

        MapPoint? born = null, revive = null;
        if (se.TryGetProperty("m_kBornPoints", out var borns) && borns.ValueKind == JsonValueKind.Array)
        {
            var list = borns.EnumerateArray().ToList();
            var chosen = list.FirstOrDefault(b => b.TryGetProperty("isDefault", out var d) && d.ValueKind == JsonValueKind.True);
            if (chosen.ValueKind == JsonValueKind.Undefined && list.Count > 0) chosen = list[0];
            if (chosen.ValueKind != JsonValueKind.Undefined) born = Point(chosen, "direction");
        }
        if (se.TryGetProperty("m_kResurrectionPointList", out var revives) && revives.ValueKind == JsonValueKind.Array && revives.GetArrayLength() > 0)
            revive = Point(revives[0], "direction");

        var points = new Dictionary<long, MapPoint>();
        if (se.TryGetProperty("m_kAllPoints", out var all) && all.ValueKind == JsonValueKind.Object)
            foreach (var p in all.EnumerateObject())
                if (long.TryParse(p.Name, out var id) && Point(p.Value, "direction") is { } point) points[id] = point;

        var npcs = new List<WorldNpc>();
        if (se.TryGetProperty("kNpcList", out var list2) && list2.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in list2.EnumerateObject())
            {
                var e = p.Value;
                var kind = (WorldNpcKind)GameData.Int(e, "NpcTag");
                bool server = kind switch
                {
                    WorldNpcKind.Npc => e.TryGetProperty("IsServerNpc", out var s) && s.ValueKind == JsonValueKind.True,
                    WorldNpcKind.Portal or WorldNpcKind.Kafra => true,
                    _ => false,
                };
                int npcId = (int)GameData.Int(e, "NpcId");
                if (!server || data.Row("NPCConfig", npcId) is not { } npcRow) continue;
                if (kind == WorldNpcKind.Portal && GameData.Int(npcRow, "_iNPCSubType") == SubTypeAirTeleport) continue;
                float face = kind == WorldNpcKind.Npc ? WorldNpc.Float(e, "FaceDirection") : WorldNpc.Float(e, "eulerAnglesY");
                float x = WorldNpc.Float(e, "x"), y = WorldNpc.Float(e, "y"), z = WorldNpc.Float(e, "z");
                // Kafras (tag 28) often carry a placeholder height (e.g. 10514: y -4.958 on ground -8.5). The client sets the
                // NPC at the y we send and gates interaction with a 3D distance (LC_InteractManager._calAllUnitInDistance,
                // NPCConfig _iInteractDistance 2), so an off-ground Kafra can never be used and cross-map auto-navigation dead-
                // ends at it. Ground Kafras on the nav mesh. Portals keep their editor y: the client keys teleport triggers
                // by floor(y*10) (LC_InteractManager.lua:612-644).
                if (kind == WorldNpcKind.Kafra && map.Nav?.WalkHeight(x, z, y) is { } ground) y = ground;
                npcs.Add(new WorldNpc(NpcUidBase | sceneEntry << 16 | (long)npcs.Count, sceneEntry, kind, npcId,
                    data.ClientTextOf(npcRow, "_kName"), (int)GameData.Int(npcRow, "_iCamp"), x, y, z, face, e));
            }
        }
        if (_pointNpcs.TryGetValue(sceneEntry, out var placed)
            && se.TryGetProperty("m_kMonsterSpawnPoints", out var spawnPoints) && spawnPoints.ValueKind == JsonValueKind.Object)
            foreach (var (npcId, pointId) in placed)
            {
                if (!spawnPoints.TryGetProperty(pointId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var p)
                    || !p.TryGetProperty("position", out var pos) || data.Row("NPCConfig", npcId) is not { } npcRow) continue;
                npcs.Add(new WorldNpc(NpcUidBase | sceneEntry << 16 | (long)npcs.Count, sceneEntry, WorldNpcKind.Npc, npcId,
                    data.ClientTextOf(npcRow, "_kName"), (int)GameData.Int(npcRow, "_iCamp"), WorldNpc.Float(pos, "x"),
                    WorldNpc.Float(pos, "y"), WorldNpc.Float(pos, "z"), WorldNpc.Float(p, "direction"), p));
            }
        var spawnGroups = LoadSpawnGroups(se);
        log.LogInformation("Map {Scene} {Name}: {Npcs} NPC/portal/Kafra, {Groups} spawn groups, born {Born}, nav {W}x{H}",
            sceneEntry, data.MapName(sceneEntry), npcs.Count, spawnGroups.Count, born, map.Nav?.Width, map.Nav?.Height);
        return new MapWorld
        {
            SceneEntry = sceneEntry, Born = born, Revive = revive, Npcs = npcs, Points = points, Nav = map.Nav, SpawnGroups = spawnGroups,
            GatheringPoints = LoadGatheringPoints(se, sceneEntry), DungeonGroups = LoadDungeonGroups(se), Regions = LoadRegions(se)
        };
    }

    private static Dictionary<long, MapRegion> LoadRegions(JsonElement se)
    {
        var regions = new Dictionary<long, MapRegion>();
        if (!se.TryGetProperty("m_kRegins", out var rs) || rs.ValueKind != JsonValueKind.Object) return regions;
        foreach (var r in rs.EnumerateObject())
        {
            var e = r.Value;
            if (!long.TryParse(r.Name, out long id) || !e.TryGetProperty("center", out var c)) continue;
            regions[id] = new MapRegion(id, WorldNpc.Float(c, "x"), WorldNpc.Float(c, "y"), WorldNpc.Float(c, "z"),
                WorldNpc.Float(e, "width"), WorldNpc.Float(e, "length"),
                e.TryGetProperty("actions", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "",
                (int)GameData.Int(e, "shapeType"), WorldNpc.Float(e, "direction"));
        }
        return regions;
    }

    /// <summary>use DungeonConfig's dg01 editor, retaining every member point.</summary>
    private static Dictionary<int, IReadOnlyList<SpawnPoint>> LoadDungeonGroups(JsonElement se)
    {
        var groups = new Dictionary<int, IReadOnlyList<SpawnPoint>>();
        if (!se.TryGetProperty("m_kGroups", out var gs) || gs.ValueKind != JsonValueKind.Object
            || !se.TryGetProperty("m_kMonsterSpawnPoints", out var ps) || ps.ValueKind != JsonValueKind.Object) return groups;
        foreach (var g in gs.EnumerateObject())
        {
            if (!g.Value.TryGetProperty("itemIdList", out var ids) || ids.ValueKind != JsonValueKind.Array) continue;
            var points = new List<SpawnPoint>();
            foreach (var id in ids.EnumerateArray())
            {
                if (!ps.TryGetProperty(id.ToString(), out var p) || !p.TryGetProperty("position", out var pos)) continue;
                points.Add(new SpawnPoint((int)GameData.Int(p, "Id"), WorldNpc.Float(pos, "x"), WorldNpc.Float(pos, "y"),
                    WorldNpc.Float(pos, "z"), WorldNpc.Float(p, "direction"), (int)GameData.Int(p, "npcId")));
            }
            groups[(int)GameData.Int(g.Value, "Id")] = points;
        }
        return groups;
    }

    private static List<SpawnGroup> LoadSpawnGroups(JsonElement se)
    {
        var points = new Dictionary<int, List<SpawnPoint>>();
        if (se.TryGetProperty("m_kWorldMonsterSpawnPoints", out var ps) && ps.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in ps.EnumerateObject())
            {
                var e = p.Value;
                int group = (int)GameData.Int(e, "monsterGroupId");
                int config = 0;
                if (e.TryGetProperty("m_kConfigIds", out var ids) && ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() > 0 && GameData.Number(ids[0]) is { } c)
                    config = (int)c;
                if (!e.TryGetProperty("position", out var pos)) continue;
                if (!points.TryGetValue(group, out var list)) points[group] = list = new();
                list.Add(new SpawnPoint((int)GameData.Int(e, "Id"), WorldNpc.Float(pos, "x"), WorldNpc.Float(pos, "y"), WorldNpc.Float(pos, "z"),
                    WorldNpc.Float(e, "direction"), config));
            }
        }
        var groups = new List<SpawnGroup>();
        if (se.TryGetProperty("m_kWorldMonsterSpawnGroups", out var gs) && gs.ValueKind == JsonValueKind.Object)
        {
            foreach (var g in gs.EnumerateObject())
            {
                var e = g.Value;
                int id = (int)GameData.Int(e, "Id");
                bool pool = e.TryGetProperty("m_bUseMaxMinRefreshId", out var u) && u.ValueKind == JsonValueKind.True;
                int total = e.TryGetProperty("genNumberRandomRange", out var range) ? (int)WorldNpc.Float(range, "x") : 0;
                int respawn = e.TryGetProperty("reSpawnTime", out var rt) ? (int)WorldNpc.Float(rt, "x") : 5;
                var limits = new List<(int, int, int)>();
                if (e.TryGetProperty("m_kMonsterRefreshLimitDatas", out var ls) && ls.ValueKind == JsonValueKind.Array)
                    foreach (var l in ls.EnumerateArray())
                        limits.Add(((int)GameData.Int(l, "m_kConfigId"), (int)GameData.Int(l, "m_iLimitCount_min"), (int)GameData.Int(l, "m_iLimitCount_max")));
                groups.Add(new SpawnGroup(id, pool, total, respawn, limits, points.GetValueOrDefault(id) ?? []));
            }
        }
        return groups;
    }

    private NavGrid? LoadNav(JsonElement mapRow)
    {
        long resourceId = GameData.Int(mapRow, "_iMapResourceID");
        if (data.Row("MapResource", resourceId) is not { } res || !res.TryGetProperty("_kNavDataName", out var n) || n.GetString() is not { Length: > 0 } navName)
            return null;
        var file = navName.Contains("_dynamicmapdata.bytes") ? navName : navName.Replace("data.bytes", "_dynamicmapdata.bytes");
        if (DataFiles.Find(Dir("nav"), file) is not { } path) return null;
        try
        {
            return NavGrid.Load(path);
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            log.LogWarning(e, "Bad nav grid {Path}", path);
            return null;
        }
    }
}
