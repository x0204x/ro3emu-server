namespace Ro3Emu.Server.Maps;

/// <summary>
/// The client's walkability grid of one map (<c>*_dynamicmapdata.bytes</c>): 1 m cells, world x,z =
/// cell index, each cell walkable or not with a height; a few maps carry extra height layers. Little-endian, uncompressed:
/// a 12 x int32 header, a block table (30x30-cell blocks), 4 bytes per cell (byte0 bit7 walkable, int16 height in cm)
/// and 8-byte extra layers {cell, u16 x, u16 z}.
/// </summary>
public sealed class NavGrid
{
    /// <summary>The client accepts a walkable layer within this height of the queried y (SceneSpaceDynamic._isStaticWalkable).</summary>
    public const float WalkHeightTolerance = 2f;

    private const int HeaderSize = 48;
    private readonly byte[] _data;
    private readonly int _blockSize;
    private readonly int _blocksX;

    public readonly record struct PathPoint(float X, float Y, float Z);

    private readonly record struct PathNode(int X, int Z, int HeightCm)
    {
        public float Height => HeightCm * 0.01f;
    }

    public int Width { get; }
    public int Height { get; }

    private NavGrid(byte[] data)
    {
        _data = data;
        Width = BitConverter.ToInt32(data, 20);
        Height = BitConverter.ToInt32(data, 24);
        _blockSize = BitConverter.ToInt32(data, 28);
        _blocksX = (Width + _blockSize - 1) / _blockSize;
    }

    public static NavGrid Load(string path) => new(File.ReadAllBytes(path));

    /// <summary>Test-only entry point for an invented navigation grid (the real server loads publisher data from disk).</summary>
    internal static NavGrid FromBytes(byte[] data) => new(data);

    /// <summary>Height layers of cell floor(x), floor(z): (walkable, height in m).</summary>
    public IEnumerable<(bool Walkable, float Height)> Layers(float x, float z)
    {
        int cx = (int)MathF.Floor(x), cz = (int)MathF.Floor(z);
        if (cx < 0 || cz < 0 || cx >= Width || cz >= Height) yield break;
        int block = cz / _blockSize * _blocksX + cx / _blockSize;
        int entry = HeaderSize + block * 16;
        int normalPos = BitConverter.ToInt32(_data, entry);
        int otherPos = BitConverter.ToInt32(_data, entry + 8);
        int otherCount = BitConverter.ToInt32(_data, entry + 12);
        int cell = normalPos + (cz % _blockSize * _blockSize + cx % _blockSize) * 4;
        yield return ((_data[cell] & 0x80) != 0, BitConverter.ToInt16(_data, cell + 2) * 0.01f);
        for (int k = 0; k < otherCount; k++)
        {
            int p = otherPos + k * 8;
            if (BitConverter.ToUInt16(_data, p + 4) == cx && BitConverter.ToUInt16(_data, p + 6) == cz)
                yield return ((_data[p] & 0x80) != 0, BitConverter.ToInt16(_data, p + 2) * 0.01f);
        }
    }

    public bool IsWalkable(float x, float y, float z) =>
        Layers(x, z).Any(l => l.Walkable && MathF.Abs(l.Height - y) <= WalkHeightTolerance);

    /// <summary>Height of the walkable layer nearest to <paramref name="yHint"/>, or null when the cell is not walkable.</summary>
    public float? WalkHeight(float x, float z, float yHint)
    {
        float? best = null;
        foreach (var (walkable, h) in Layers(x, z))
            if (walkable && (best == null || MathF.Abs(h - yHint) < MathF.Abs(best.Value - yHint))) best = h;
        return best;
    }

    /// <summary>
    /// Whether the straight 2D segment from a to b crosses only walkable cells (sampled every half metre, heights
    /// followed cell by cell like the client's direct-walk check).
    /// </summary>
    public bool CanWalk(float ax, float ay, float az, float bx, float bz)
    {
        float dx = bx - ax, dz = bz - az;
        int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(dx * dx + dz * dz) / 0.5f));
        float y = ay;
        int previousX = (int)MathF.Floor(ax), previousZ = (int)MathF.Floor(az);
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps, x = ax + dx * t, z = az + dz * t;
            if (WalkHeight(x, z, y) is not { } h || MathF.Abs(h - y) > WalkHeightTolerance) return false;
            int cellX = (int)MathF.Floor(x), cellZ = (int)MathF.Floor(z);
            if (cellX != previousX && cellZ != previousZ &&
                (!HasDiagonalClearance(cellX, previousZ, y, h) || !HasDiagonalClearance(previousX, cellZ, y, h))) return false;
            previousX = cellX;
            previousZ = cellZ;
            y = h;
        }
        return true;
    }

    /// <summary>
    /// Finds a bounded grid path between two world positions. A direct segment remains the fast path; otherwise A*
    /// expands the client's one-metre cells and preserves the same two-metre layer transition limit as
    /// <see cref="CanWalk"/>. The returned path contains the exact start and end positions.
    /// </summary>
    public bool TryFindPath(float ax, float ay, float az, float bx, float bz, out IReadOnlyList<PathPoint> path,
        int maxVisited = 4096)
    {
        path = [];
        if (maxVisited <= 0 || !InBounds(ax, az) || !InBounds(bx, bz)) return false;
        if (WalkHeight(ax, az, ay) is not { } startHeight || MathF.Abs(startHeight - ay) > WalkHeightTolerance) return false;

        if (CanWalk(ax, ay, az, bx, bz))
        {
            float endHeight = WalkHeight(bx, bz, startHeight) ?? startHeight;
            path = [new PathPoint(ax, ay, az), new PathPoint(bx, endHeight, bz)];
            return true;
        }

        int startX = (int)MathF.Floor(ax), startZ = (int)MathF.Floor(az);
        int goalX = (int)MathF.Floor(bx), goalZ = (int)MathF.Floor(bz);
        var start = Node(startX, startZ, startHeight);
        var open = new PriorityQueue<PathNode, float>();
        var distance = new Dictionary<PathNode, float> { [start] = 0 };
        var previous = new Dictionary<PathNode, PathNode>();
        var closed = new HashSet<PathNode>();
        open.Enqueue(start, Heuristic(startX, startZ, goalX, goalZ));

        PathNode? reached = null;
        while (open.TryDequeue(out var current, out _) && closed.Count < maxVisited)
        {
            if (!closed.Add(current)) continue;
            if (current.X == goalX && current.Z == goalZ)
            {
                reached = current;
                break;
            }

            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nx = current.X + dx, nz = current.Z + dz;
                    if (nx < 0 || nz < 0 || nx >= Width || nz >= Height) continue;
                    foreach (var (walkable, nextHeight) in Layers(nx + 0.5f, nz + 0.5f))
                    {
                        if (!walkable || MathF.Abs(nextHeight - current.Height) > WalkHeightTolerance) continue;
                        if (dx != 0 && dz != 0 &&
                            (!HasDiagonalClearance(current.X + dx, current.Z, current.Height, nextHeight)
                             || !HasDiagonalClearance(current.X, current.Z + dz, current.Height, nextHeight))) continue;

                        var next = Node(nx, nz, nextHeight);
                        if (closed.Contains(next)) continue;
                        float step = dx == 0 || dz == 0 ? 1f : MathF.Sqrt(2f);
                        float candidate = distance[current] + step;
                        if (distance.TryGetValue(next, out float known) && candidate >= known) continue;
                        distance[next] = candidate;
                        previous[next] = current;
                        open.Enqueue(next, candidate + Heuristic(nx, nz, goalX, goalZ));
                    }
                }
        }

        if (reached is not { } end) return false;
        var nodes = new List<PathNode>();
        for (var node = end; ; node = previous[node])
        {
            nodes.Add(node);
            if (node == start) break;
        }
        nodes.Reverse();

        var points = new List<PathPoint>(nodes.Count + 1) { new(ax, ay, az) };
        for (int i = 1; i < nodes.Count; i++)
            points.Add(new PathPoint(nodes[i].X + 0.5f, nodes[i].Height, nodes[i].Z + 0.5f));
        float goalHeight = WalkHeight(bx, bz, end.Height) ?? end.Height;
        if (points.Count > 1 && SamePosition(points[^1], bx, bz)) points[^1] = new PathPoint(bx, goalHeight, bz);
        else points.Add(new PathPoint(bx, goalHeight, bz));
        path = points;
        return true;
    }

    /// <summary>
    /// Returns the next straight, visible segment of a bounded path. It never crosses more than
    /// <paramref name="maxDistance"/> metres and leaves <paramref name="stopDistance"/> metres of route at the end.
    /// </summary>
    public bool TryFindNextWaypoint(float ax, float ay, float az, float bx, float bz, float maxDistance,
        float stopDistance, out PathPoint waypoint, int maxVisited = 4096)
    {
        waypoint = default;
        if (maxDistance <= 0 || !TryFindPath(ax, ay, az, bx, bz, out var path, maxVisited) || path.Count < 2) return false;

        var smoothed = new List<PathPoint> { path[0] };
        int at = 0;
        while (at < path.Count - 1)
        {
            int next = at + 1;
            for (int candidate = path.Count - 1; candidate > at + 1; candidate--)
                if (CanWalk(path[at].X, path[at].Y, path[at].Z, path[candidate].X, path[candidate].Z))
                {
                    next = candidate;
                    break;
                }
            smoothed.Add(path[next]);
            at = next;
        }

        float total = 0;
        for (int i = 1; i < smoothed.Count; i++) total += Distance(smoothed[i - 1], smoothed[i]);
        float allowed = MathF.Min(maxDistance, MathF.Max(0, total - MathF.Max(0, stopDistance)));
        if (allowed < 0.1f) return false;

        var first = smoothed[0];
        var second = smoothed[1];
        float firstLength = Distance(first, second);
        float travel = MathF.Min(allowed, firstLength);
        if (firstLength < 0.001f) return false;
        float t = travel / firstLength;
        waypoint = new PathPoint(first.X + (second.X - first.X) * t, first.Y + (second.Y - first.Y) * t,
            first.Z + (second.Z - first.Z) * t);
        return true;
    }

    private bool InBounds(float x, float z) => x >= 0 && z >= 0 && x < Width && z < Height;

    private bool HasDiagonalClearance(int x, int z, float fromHeight, float toHeight) =>
        Layers(x + 0.5f, z + 0.5f).Any(l => l.Walkable
            && MathF.Abs(l.Height - fromHeight) <= WalkHeightTolerance
            && MathF.Abs(l.Height - toHeight) <= WalkHeightTolerance);

    private static PathNode Node(int x, int z, float height) => new(x, z, (int)MathF.Round(height * 100));
    private static float Heuristic(int x, int z, int goalX, int goalZ)
    {
        int dx = Math.Abs(goalX - x), dz = Math.Abs(goalZ - z);
        return Math.Max(dx, dz) + (MathF.Sqrt(2f) - 1) * Math.Min(dx, dz);
    }

    private static float Distance(PathPoint a, PathPoint b)
    {
        float dx = b.X - a.X, dz = b.Z - a.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static bool SamePosition(PathPoint p, float x, float z) =>
        MathF.Abs(p.X - x) < 0.001f && MathF.Abs(p.Z - z) < 0.001f;
}
