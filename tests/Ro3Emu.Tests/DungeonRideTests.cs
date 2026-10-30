using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>The geometry of a scripted ride (DungeonService.Ride.cs): a route is walked by horizontal distance.</summary>
public class DungeonRideTests
{
    // 5 m along (3, 4), then 6 m along z; the second leg climbs 10 m.
    private static readonly (float X, float Y, float Z)[] Route = [(0, 0, 0), (3, 0, 4), (3, 10, 10)];

    [Fact]
    public void ARouteIsMeasuredHorizontally() => Assert.Equal(11f, DungeonService.RideLength(Route), 3);

    [Theory]
    [InlineData(0f, 0f, 0f, 0f)]
    [InlineData(5f, 3f, 0f, 4f)] // the corner
    [InlineData(8f, 3f, 5f, 7f)] // halfway up the climb: y is 5 of 10
    [InlineData(11f, 3f, 10f, 10f)]
    public void TheRidePassesTheRouteInOrder(float metres, float x, float y, float z)
    {
        var at = DungeonService.RideAt(Route, metres);
        Assert.Equal((x, y, z), (MathF.Round(at.X, 3), MathF.Round(at.Y, 3), MathF.Round(at.Z, 3)));
    }

    [Theory]
    [InlineData(-3f)]
    [InlineData(0f)]
    public void BeforeTheStartIsTheStart(float metres) => Assert.Equal((0f, 0f, 0f), DungeonService.RideAt(Route, metres) is var a ? (a.X, a.Y, a.Z) : default);

    [Fact]
    public void PastTheEndStaysAtTheEnd()
    {
        var at = DungeonService.RideAt(Route, 500);
        Assert.Equal((3f, 10f, 10f), (at.X, at.Y, at.Z));
    }

    [Fact]
    public void TheHeadingFollowsTheLegItIsOn()
    {
        Assert.Equal(DungeonService.RideAt(Route, 1).Face, DungeonService.RideAt(Route, 4).Face);
        Assert.NotEqual(DungeonService.RideAt(Route, 4).Face, DungeonService.RideAt(Route, 8).Face);
    }

    [Fact]
    public void ARouteIsReadWithItsPointsAndABrokenOneIsDropped()
    {
        using var file = JsonDocument.Parse("""
            { "m_kGuidePoints": {
              "1": { "Id": 1, "pointList": [ { "x": 1, "y": 2, "z": 3 }, { "x": 4, "y": 5, "z": 6 } ] },
              "2": { "Id": 2, "pointList": [ { "x": 1, "y": 2, "z": 3 } ] },
              "3": { "Id": 3, "pointList": [ { "x": 1, "y": 2, "z": 3 }, { "x": 4, "z": 6 } ] } } }
            """);
        var paths = SceneEditorGuides.Paths(file.RootElement);
        Assert.Equal([(1f, 2f, 3f), (4f, 5f, 6f)], paths[1]);
        Assert.Equal([1L], paths.Keys); // one point is no route; a point without y would read as 0
    }
}
