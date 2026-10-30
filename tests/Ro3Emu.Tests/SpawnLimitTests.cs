using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Ro3Emu:SpawnLimits: a cap on what one spawn pool group creates of one NPC on one map.</summary>
public sealed class SpawnLimitTests
{
    private const int GreatestGeneral = 5000070;
    private const long PayonForest = 10220, DeepForest = 10219;

    [Theory]
    [InlineData(400, -1, 400)] // no rule: the client's number
    [InlineData(400, 300, 300)]
    [InlineData(250, 300, 250)] // a group that rolls fewer keeps its count
    [InlineData(400, 0, 0)]
    public void ACapOnlyLowersTheRolledCount(int rolled, int limit, int expected) =>
        Assert.Equal(expected, MonsterService.LimitedCount(rolled, limit));

    private static async Task<int> GeneralsAsync(IReadOnlyList<SpawnLimitRule> rules, long scene)
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-spawnlimit-" + Guid.NewGuid().ToString("N"));
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        var app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.SpawnLimits = [.. rules];
        });
        await app.StartAsync();
        try
        {
            var monsters = app.Services.GetRequiredService<MonsterService>();
            var world = app.Services.GetRequiredService<World>();
            var key = RuntimeMapKey.Public(scene);
            await app.Services.GetRequiredService<GameLoop>().InvokeAsync(() => monsters.EnsureSpawnedAsync(key));
            return world.AllUnits().OfType<MonsterUnit>().Count(m => m.RuntimeMap == key && m.Template.NpcId == GreatestGeneral);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    // The client's own pool groups: Payon Forest 400 Greatest Generals, Deep Forest 425 to 435.
    [ClientDataFact]
    public async Task WithoutARuleThePoolGroupSpawnsTheClientsNumber() =>
        Assert.Equal(400, await GeneralsAsync([], PayonForest));

    [ClientDataFact]
    public async Task ARuleCapsTheGeneralsOfPayonForestAt300() =>
        Assert.Equal(300, await GeneralsAsync([new() { Scene = PayonForest, Npc = GreatestGeneral, Max = 300 }], PayonForest));

    [ClientDataFact]
    public async Task ARuleCapsTheGeneralsOfDeepForestAt400AndLeavesTheOtherMapAlone()
    {
        var rules = new SpawnLimitRule[] { new() { Scene = DeepForest, Npc = GreatestGeneral, Max = 400 } };
        Assert.Equal(400, await GeneralsAsync(rules, DeepForest));
        Assert.Equal(400, await GeneralsAsync(rules, PayonForest)); // a rule is for its own map
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
