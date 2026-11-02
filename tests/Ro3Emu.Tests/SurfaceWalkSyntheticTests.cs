using System.Text.Json.Nodes;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed class SurfaceWalkSyntheticTests
{
    /// <summary>依上游 v1.0.26，起點已在導航格外時，普通及表面場景均不因缺地面格被拉回。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task OffGridStartDoesNotSnapBackInEitherScene(bool surface) =>
        CheckWalkAsync(surface, validStart: false);

    /// <summary>起點確在有效格時，只有配置指定的表面場景可走到地面格外；普通場景必須拒絕。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task OnlySurfaceSceneCanLeaveAValidGroundGrid(bool surface) =>
        CheckWalkAsync(surface, validStart: true);

    private static async Task CheckWalkAsync(bool surface, bool validStart)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ro3emu-surface-" + Guid.NewGuid().ToString("N"));
        try
        {
            PrepareData(directory, surface, validStart);
            await using var host = await SyntheticHost.StartAsync(o => { o.GameDataDir = directory; o.WalkCheck = "enforce"; });
            await using var bot = await host.InWorldAsync("Walker");
            var session = host.Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
            var scene = host.Service<SceneService>();
            var loop = host.Service<GameLoop>();
            Assert.Equal(surface, scene.MovesOnSurface(session.SceneEntry));
            Assert.Null(scene.StoryMovementSpeed?.Invoke(session));
            var nav = host.Service<WorldData>().Map(session.SceneEntry)!.Nav;
            Assert.NotNull(nav);
            var start = await loop.InvokeAsync(() => Task.FromResult(session.Position.Clone()));
            Assert.Equal(validStart, nav.NearestWalkable(start.X, start.Z, start.Y) != null);
            float distance = validStart ? 2f : .5f;
            Assert.Null(nav.NearestWalkable(start.X + distance, start.Z, start.Y));
            bool allowed = surface || !validStart;
            await bot.WalkAsync(start.X + distance, start.Y, start.Z, stop: true);
            await bot.HeartbeatAsync();
            Assert.Equal(start.X + (allowed ? distance : 0),
                await loop.InvokeAsync(() => Task.FromResult(session.Position.X)), 3);
            Assert.Equal(allowed ? 0 : 1, host.Service<CheatMonitor>().LoggedCount(bot.Account!, CheatMonitor.Walk));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static void PrepareData(string directory, bool surface, bool validStart)
    {
        foreach (var file in Directory.GetFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(directory, Path.GetRelativePath(SyntheticData.Dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        string config = Path.Combine(directory, "config");
        string mapPath = Path.Combine(config, "MapConfig.json");
        var maps = JsonNode.Parse(File.ReadAllText(mapPath))!;
        maps["10111"]!["_iMapResourceID"] = 990001;
        File.WriteAllText(mapPath, maps.ToJsonString());
        File.WriteAllText(Path.Combine(config, "MapResource.json"),
            """{"990001":{"_iID":990001,"_kNavDataName":"synthetic_surface_dynamicmapdata.bytes"}}""");
        string publicPath = Path.Combine(config, "PublicParamConfig.json");
        var parameters = JsonNode.Parse(File.ReadAllText(publicPath))!;
        parameters["NoviceDungeonReverseDungeonID"] = surface ? SyntheticData.Town : 990002;
        File.WriteAllText(publicPath, parameters.ToJsonString());
        // 完全自造一個導航區塊；有效起點案僅開放出生格(150,186)，終點與其相距兩格。
        int side = validStart ? 200 : 1;
        byte[] nav = new byte[64 + side * side * 4];
        foreach (var (offset, value) in new[] { (20, side), (24, side), (28, side), (48, 64), (56, nav.Length) })
            BitConverter.GetBytes(value).CopyTo(nav, offset);
        if (validStart) nav[64 + (186 * side + 150) * 4] = 0x80;
        Directory.CreateDirectory(Path.Combine(directory, "nav"));
        File.WriteAllBytes(Path.Combine(directory, "nav", "synthetic_surface_dynamicmapdata.bytes"), nav);
    }
}
