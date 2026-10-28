using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed class NavGridPathTests
{
    [Fact]
    public void AStarRoutesThroughTheOnlyWallGap()
    {
        var blocked = new HashSet<(int X, int Z)>();
        for (int z = 0; z < 7; z++)
            if (z != 5) blocked.Add((4, z));
        var nav = Grid(9, 7, blocked);

        Assert.False(nav.CanWalk(1.5f, 0, 1.5f, 7.5f, 1.5f));
        Assert.True(nav.TryFindPath(1.5f, 0, 1.5f, 7.5f, 1.5f, out var path));
        Assert.Contains(path, p => (int)MathF.Floor(p.X) == 4 && (int)MathF.Floor(p.Z) == 5);
        Assert.All(path, p => Assert.True(nav.IsWalkable(p.X, p.Y, p.Z)));
        for (int i = 1; i < path.Count; i++)
            Assert.True(nav.CanWalk(path[i - 1].X, path[i - 1].Y, path[i - 1].Z, path[i].X, path[i].Z));
    }

    [Fact]
    public void NextWaypointIsBoundedAndDoesNotCrossTheWall()
    {
        var blocked = new HashSet<(int X, int Z)>();
        for (int z = 0; z < 7; z++)
            if (z != 5) blocked.Add((4, z));
        var nav = Grid(9, 7, blocked);

        Assert.True(nav.TryFindNextWaypoint(1.5f, 0, 1.5f, 7.5f, 1.5f, 3f, 0, out var next));
        float distance = MathF.Sqrt(MathF.Pow(next.X - 1.5f, 2) + MathF.Pow(next.Z - 1.5f, 2));
        Assert.InRange(distance, 0.1f, 3.001f);
        Assert.True(nav.CanWalk(1.5f, 0, 1.5f, next.X, next.Z));
        Assert.True(next.X < 4 || next.Z >= 5);
    }

    [Fact]
    public void AStarRejectsASealedBarrierAndDiagonalCornerCutting()
    {
        var wall = Enumerable.Range(0, 5).Select(z => (X: 2, Z: z)).ToHashSet();
        var sealedGrid = Grid(5, 5, wall);
        Assert.False(sealedGrid.TryFindPath(0.5f, 0, 2.5f, 4.5f, 2.5f, out _));

        var cornerGrid = Grid(2, 2, new HashSet<(int X, int Z)> { (1, 0), (0, 1) });
        Assert.False(cornerGrid.TryFindPath(0.5f, 0, 0.5f, 1.5f, 1.5f, out _));
    }

    [Fact]
    public void AStarKeepsTheSelectedHeightLayer()
    {
        var extras = new Dictionary<(int X, int Z), short>();
        for (int x = 0; x < 5; x++) extras[(x, 1)] = 1000;
        var nav = Grid(5, 3, new HashSet<(int X, int Z)>(), extras);

        Assert.True(nav.TryFindPath(0.5f, 10, 1.5f, 4.5f, 1.5f, out var path));
        Assert.All(path, p => Assert.InRange(p.Y, 9.99f, 10.01f));
    }

    private static NavGrid Grid(int width, int height, IReadOnlySet<(int X, int Z)> blocked,
        IReadOnlyDictionary<(int X, int Z), short>? extraHeightsCm = null)
    {
        int blockSize = Math.Max(width, height);
        int normalPos = 64;
        int normalLength = blockSize * blockSize * 4;
        var extras = extraHeightsCm?.ToArray() ?? [];
        int otherPos = normalPos + normalLength;
        byte[] bytes = new byte[otherPos + extras.Length * 8];
        WriteInt32(bytes, 20, width);
        WriteInt32(bytes, 24, height);
        WriteInt32(bytes, 28, blockSize);
        WriteInt32(bytes, 48, normalPos);
        WriteInt32(bytes, 56, otherPos);
        WriteInt32(bytes, 60, extras.Length);
        for (int z = 0; z < height; z++)
            for (int x = 0; x < width; x++)
            {
                int p = normalPos + (z * blockSize + x) * 4;
                bytes[p] = blocked.Contains((x, z)) ? (byte)0 : (byte)0x80;
            }
        for (int i = 0; i < extras.Length; i++)
        {
            int p = otherPos + i * 8;
            bytes[p] = 0x80;
            WriteInt16(bytes, p + 2, extras[i].Value);
            WriteUInt16(bytes, p + 4, (ushort)extras[i].Key.X);
            WriteUInt16(bytes, p + 6, (ushort)extras[i].Key.Z);
        }
        return NavGrid.FromBytes(bytes);
    }

    private static void WriteInt16(byte[] bytes, int offset, short value) =>
        BitConverter.GetBytes(value).CopyTo(bytes, offset);

    private static void WriteUInt16(byte[] bytes, int offset, ushort value) =>
        BitConverter.GetBytes(value).CopyTo(bytes, offset);

    private static void WriteInt32(byte[] bytes, int offset, int value) =>
        BitConverter.GetBytes(value).CopyTo(bytes, offset);
}
