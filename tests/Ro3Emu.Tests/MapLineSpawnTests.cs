using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Public-line actor ownership and retirement using the authored world data.</summary>
public sealed class MapLineSpawnTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-lines-spawn-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        // Seed before the game ticker can load MVP states, so cooldown behavior is deterministic.
        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(),
            "INSERT INTO mvp_line_cooldown(cfg_id, death_ms) VALUES (1, $death)",
            ("$death", MonsterService.NowMs));
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    [ClientDataFact]
    public Task FieldMonstersHaveIndependentActorsAndRetirementCancelsCorpseWork() => OnLoop(async () =>
    {
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        var world = _app.Services.GetRequiredService<World>();
        var first = RuntimeMapKey.Public(10111);
        var second = RuntimeMapKey.Public(10111, 2);
        await monsters.EnsureSpawnedAsync(first);
        await monsters.EnsureSpawnedAsync(second);
        var one = world.AllUnits().OfType<MonsterUnit>().Where(m => m.RuntimeMap == first && m.Group.Id >= 0).ToArray();
        var two = world.AllUnits().OfType<MonsterUnit>().Where(m => m.RuntimeMap == second && m.Group.Id >= 0).ToArray();
        Assert.NotEmpty(one);
        // Pool groups roll independently, so their population may differ by one or more actors.
        Assert.Contains(two, m => m.Group.Id == one[0].Group.Id);
        Assert.Empty(one.Select(m => m.Uid).Intersect(two.Select(m => m.Uid)));
        Assert.All(two, m => Assert.Equal(second.WorldScene, m.WorldScene));

        await monsters.KillAsync(two[0]);
        Assert.True(monsters.HasPendingWork(second)); // corpse deadline belongs to this line
        await monsters.RetireRuntimeAsync(second);
        Assert.False(monsters.HasPendingWork(second));
        Assert.DoesNotContain(world.AllUnits(), u => u.RuntimeMap == second);
        Assert.Contains(world.AllUnits(), u => u.RuntimeMap == first && u.Uid == one[0].Uid);
    });

    [ClientDataFact]
    public Task GatheringNodesHaveIndependentOccupancyAndRetireWithTheirLine() => OnLoop(async () =>
    {
        var gathering = _app.Services.GetRequiredService<GatheringService>();
        var world = _app.Services.GetRequiredService<World>();
        var first = RuntimeMapKey.Public(10522);
        var second = RuntimeMapKey.Public(10522, 2);
        await gathering.EnsureSpawnedAsync(first);
        await gathering.EnsureSpawnedAsync(second);
        var one = world.AllUnits().OfType<GatheringUnit>().Where(n => n.RuntimeMap == first).ToArray();
        var two = world.AllUnits().OfType<GatheringUnit>().Where(n => n.RuntimeMap == second).ToArray();
        Assert.NotEmpty(one);
        Assert.Equal(one.Length, two.Length);
        Assert.Empty(one.Select(n => n.Uid).Intersect(two.Select(n => n.Uid)));
        await gathering.RetireRuntimeAsync(second);
        Assert.DoesNotContain(world.AllUnits(), u => u.RuntimeMap == second);
        Assert.Contains(world.AllUnits(), u => u.RuntimeMap == first && u.Uid == one[0].Uid);
    });

    [ClientDataFact]
    public Task PrivateSingleSpawnsStayOwnedByTheirRoom() => OnLoop(async () =>
    {
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        var world = _app.Services.GetRequiredService<World>();
        for (int i = 1; i <= 12; i++)
        {
            var runtime = RuntimeMapKey.Private(10111, 5_000_000 + i);
            var unit = await monsters.SpawnSingleAsync(runtime, 5000001, new MapPoint(156, 0, 192, 0));
            Assert.NotNull(unit);
            Assert.Equal(runtime, unit.RuntimeMap);
            Assert.False(monsters.HasPendingWork(runtime));
            await world.RemoveUnitAsync(unit);
            world.RemoveInstance(runtime.WorldScene);
        }
        Assert.DoesNotContain(world.AllUnits(), u => u.RuntimeMap.Kind == RuntimeMapKind.PrivateInstance);
    });

    [ClientDataFact]
    public async Task MvpLineOwnsDistinctBossesAndNewLineInheritsLatestDeathCooldown()
    {
        var mvp = _app.Services.GetRequiredService<MvpService>();
        var world = _app.Services.GetRequiredService<World>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var first = RuntimeMapKey.Public(10214);
        var second = RuntimeMapKey.Public(10214, 2);

        // Angeling was recently killed on a previous line. A new line must not grant an instant fresh boss.
        await OnLoop(() =>
        {
            mvp.ActivateLine(second);
            mvp.Tick(new GameTime(loop.Now, TimeSpan.FromSeconds(1)));
            return Task.CompletedTask;
        });
        await Until(async () => await loop.InvokeAsync(() => Task.FromResult(
            world.AllUnits().OfType<MonsterUnit>().Any(m => m.Template.NpcId == 5020003 && m.RuntimeMap == first)
            && world.AllUnits().OfType<MonsterUnit>().Any(m => m.Template.NpcId == 5020003 && m.RuntimeMap == second))));

        await OnLoop(async () =>
        {
            var bosses = world.AllUnits().OfType<MonsterUnit>().Where(m => m.Template.NpcId == 5020003).ToArray();
            var one = Assert.Single(bosses, m => m.RuntimeMap == first);
            var two = Assert.Single(bosses, m => m.RuntimeMap == second);
            Assert.NotEqual(one.Uid, two.Uid);
            Assert.Equal(second.WorldScene, two.WorldScene);
            Assert.Contains(world.AllUnits(), u => u is MonsterUnit m && m.Template.NpcId == 5020001 && m.RuntimeMap == first);
            Assert.DoesNotContain(world.AllUnits(), u => u is MonsterUnit m && m.Template.NpcId == 5020001 && m.RuntimeMap == second);
            Assert.Contains("Reborn", await mvp.GmAsync(["list", "1", "2"]));
        });
    }

    private static async Task Until(Func<Task<bool>> condition)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < end)
        {
            if (await condition()) return;
            await Task.Delay(10);
        }
        Assert.True(await condition(), "Bounded MVP spawn condition did not complete.");
    }

    private Task OnLoop(Func<Task> work) => _app.Services.GetRequiredService<GameLoop>().InvokeAsync(work);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "data", "config"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("data/config");
    }
}
