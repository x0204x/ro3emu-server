using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// #bug-reports "when you warp to southern payon with kafra you get stuck in the stone": every Kafra destination's
/// arrival is walkable on its nav grid (MapWorld.Standable), and a walkable point is never moved.
/// </summary>
public sealed class KafraArrivalTests
{
    private static WorldData World()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ServerOptions
        { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") });
        return new WorldData(options, new GameData(options, NullLogger<GameData>.Instance), NullLogger<WorldData>.Instance);
    }

    private static IEnumerable<long> Destinations()
    {
        var path = DataFiles.Find(Path.Combine(ClientData.RepoRoot, "data", "sceneeditor"), "MapLinkData.json")!;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return [.. doc.RootElement.GetProperty("Capra").EnumerateObject().Select(p => long.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture))];
    }

    [ClientDataFact]
    public void EveryKafraArrivalIsWalkable()
    {
        var world = World();
        var stuck = new List<long>();
        foreach (long scene in Destinations())
            if (world.Map(scene) is { Nav: not null, KafraArrival: { } arrival } map && !map.IsWalkable(arrival.X, arrival.Y, arrival.Z))
                stuck.Add(scene);
        Assert.Empty(stuck);
    }

    [ClientDataFact]
    public void AWalkablePointStaysWhereItIs()
    {
        var world = World();
        var map = Destinations().Select(world.Map).First(m => m?.Nav != null && m.Born is { } b && m.IsWalkable(b.X, b.Y, b.Z))!;
        Assert.Equal(map.Born!.Value, map.Standable(map.Born.Value));
    }
}
