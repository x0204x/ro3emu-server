using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Maps;

/// <summary>
/// A server-controlled unit on a map (monster, later summons or moving objects): seen by the players around it
/// through the same AOI messages as other players (31037 bases / leave_aoi, 31081 movement). Lives on the game loop.
/// </summary>
public abstract class WorldUnit
{
    public long Uid { get; init; }
    public long SceneEntry { get; set; }
    public long InstanceId { get; init; }
    public int LineNumber { get; init; } = 1;
    public RuntimeMapKind RuntimeKind { get; init; }
    private RuntimeMapKey? _runtimeMap;
    public RuntimeMapKey RuntimeMap
    {
        get => _runtimeMap ?? (InstanceId == 0 ? RuntimeMapKey.Public(SceneEntry, LineNumber)
            : RuntimeKind == RuntimeMapKind.Event ? RuntimeMapKey.Event(SceneEntry, InstanceId)
            : RuntimeMapKey.Private(SceneEntry, InstanceId));
        init => _runtimeMap = value;
    }
    public long WorldScene => RuntimeMap.WorldScene;
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Face { get; set; }

    /// <summary>The grid cell the unit is filed under (MapInstance).</summary>
    internal (int X, int Z) Cell { get; set; }

    /// <summary>Players that currently show this unit (managed by World).</summary>
    public HashSet<GameSession> Viewers { get; } = new();
    // An in-flight AOI enter must finish before removal publishes its leave.
    internal SemaphoreSlim PresentationGate { get; } = new(1, 1);
    internal bool Removed { get; set; }

    /// <summary>The entity a client creates for it (31037 base).</summary>
    public abstract EntityBase Entity();

    /// <summary>Called by the map's tick (AI, regeneration...).</summary>
    public virtual void Tick(MapInstance map, GameTime time) { }

    public Vec3 Pos => new() { X = X, Y = Y, Z = Z };
}

/// <summary>
/// The simulation of one map: its server units, advanced on the game loop's tick. Created on first use per scene
/// entry (World.Map).
/// </summary>
public sealed class MapInstance(long sceneEntry, World world) : IGameTicker
{
    /// <summary>Grid cell size in m: a 60 m view radius touches at most 5 x 5 cells.</summary>
    public const int CellSize = 32;

    private readonly List<WorldUnit> _units = new();
    // Membership for Contains: every walk's unit refresh asks it per candidate, and a field map holds ~1500 units
    // (as a list scan it was 13% of the loop's work at 500 players, stage G).
    private readonly HashSet<WorldUnit> _members = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(int, int), List<WorldUnit>> _cells = new();

    public long SceneEntry => sceneEntry;
    public World World => world;
    public IReadOnlyList<WorldUnit> Units => _units;
    public bool Contains(WorldUnit unit) => _members.Contains(unit);

    private static (int, int) CellOf(float x, float z) => ((int)MathF.Floor(x / CellSize), (int)MathF.Floor(z / CellSize));

    internal void Add(WorldUnit unit)
    {
        _units.Add(unit);
        _members.Add(unit);
        unit.Cell = CellOf(unit.X, unit.Z);
        if (!_cells.TryGetValue(unit.Cell, out var list)) _cells[unit.Cell] = list = new();
        list.Add(unit);
    }

    internal void Remove(WorldUnit unit)
    {
        _units.Remove(unit);
        _members.Remove(unit);
        if (_cells.TryGetValue(unit.Cell, out var list)) list.Remove(unit);
    }

    /// <summary>Re-files a unit that moved.</summary>
    public void Moved(WorldUnit unit)
    {
        var cell = CellOf(unit.X, unit.Z);
        if (cell == unit.Cell) return;
        if (_cells.TryGetValue(unit.Cell, out var old)) old.Remove(unit);
        unit.Cell = cell;
        if (!_cells.TryGetValue(cell, out var list)) _cells[cell] = list = new();
        list.Add(unit);
    }

    /// <summary>Units in the cells overlapping a square of radius r around (x, z).</summary>
    public IEnumerable<WorldUnit> Near(float x, float z, float r)
    {
        var (x0, z0) = CellOf(x - r, z - r);
        var (x1, z1) = CellOf(x + r, z + r);
        for (int cx = x0; cx <= x1; cx++)
            for (int cz = z0; cz <= z1; cz++)
                if (_cells.TryGetValue((cx, cz), out var list))
                    foreach (var u in list) yield return u;
    }

    /// <summary>Appends the units within <paramref name="r"/> of (x, z) to <paramref name="into"/> without allocating.</summary>
    public void CollectNear(float x, float z, float r, List<WorldUnit> into)
    {
        float r2 = r * r;
        var (x0, z0) = CellOf(x - r, z - r);
        var (x1, z1) = CellOf(x + r, z + r);
        for (int cx = x0; cx <= x1; cx++)
            for (int cz = z0; cz <= z1; cz++)
                if (_cells.TryGetValue((cx, cz), out var list))
                    foreach (var u in list)
                    {
                        float dx = u.X - x, dz = u.Z - z;
                        if (dx * dx + dz * dz <= r2) into.Add(u);
                    }
    }

    public void Tick(GameTime time)
    {
        foreach (var unit in _units.ToArray()) unit.Tick(this, time);
    }
}
