using System.Diagnostics;
using System.Text.Json;
using Ro3Emu.Server.Maps;
using Xunit.Abstractions;

namespace Ro3Emu.Tests;

/// <summary>
/// NavGrid.FindPath on Prontera's real nav grid: from every PronteraDefensePointConfig point the invaders reach the
/// heart (the default born point) in legs of at most 12 m, each one a straight walk from the end of the one before.
/// </summary>
public sealed class PronteraDefenseRouteTests(ITestOutputHelper output)
{
    private const float MaxLeg = 12f;

    [ClientDataFact]
    public void EveryDefensePointHasARouteToTheHeartInCheckedLegs()
    {
        string data = Path.Combine(ClientData.RepoRoot, "data");
        var nav = NavGrid.Load(Path.Combine(data, "nav", "sc_prontera_001_dynamicmapdata.bytes"));
        using var scene = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "sceneeditor", "sc_prontera_001_sceneeditor.json")));
        using var points = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "config", "PronteraDefensePointConfig.json")));
        var born = scene.RootElement.GetProperty("m_kBornPoints")[0].GetProperty("position");
        float heartX = born.GetProperty("x").GetSingle(), heartZ = born.GetProperty("z").GetSingle();
        var all = scene.RootElement.GetProperty("m_kAllPoints");
        int routes = 0;
        var clock = Stopwatch.StartNew();
        foreach (var row in points.RootElement.EnumerateArray().Where(r => r.GetProperty("_iMapId").GetInt64() == 10100))
        {
            string id = row.GetProperty("_iPointId")[0].GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var at = all.GetProperty(id).GetProperty("position");
            float x = at.GetProperty("x").GetSingle(), z = at.GetProperty("z").GetSingle();
            float y = nav.WalkHeight(x, z, at.GetProperty("y").GetSingle()) ?? throw new InvalidOperationException($"point {id} off the grid");
            var route = nav.FindPath(x, y, z, heartX, heartZ, MaxLeg);
            Assert.True(route is { Count: > 0 }, $"no route from point {id}");
            var from = (X: x, Y: y, Z: z);
            foreach (var leg in route)
            {
                float dx = leg.X - from.X, dz = leg.Z - from.Z;
                Assert.True(dx * dx + dz * dz <= MaxLeg * MaxLeg + 0.01f, $"point {id}: a leg longer than {MaxLeg} m");
                Assert.True(nav.CanWalk(from.X, from.Y, from.Z, leg.X, leg.Z), $"point {id}: leg to {leg.X:0.0},{leg.Z:0.0} blocked");
                from = leg;
            }
            Assert.Equal((heartX, heartZ), (route[^1].X, route[^1].Z));
            routes++;
        }
        Assert.Equal(26, routes); // points 1-10 and 21-36
        output.WriteLine($"{routes} routes in {clock.ElapsedMilliseconds} ms");
    }
}
