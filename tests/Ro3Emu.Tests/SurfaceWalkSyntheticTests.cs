using System.Text.Json.Nodes;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed class SurfaceWalkSyntheticTests
{
    /// <summary>自造地面格全部不可走；只有配置指定的表面場景可徒步，其他場景仍拒絕。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyTheConfiguredSurfaceSceneAllowsWalkingOffTheGroundGrid(bool surface)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ro3emu-surface-" + Guid.NewGuid().ToString("N"));
        try
        {
            PrepareData(directory, surface);
            await using var host = await SyntheticHost.StartAsync(o => { o.GameDataDir = directory; o.WalkCheck = "enforce"; });
            await using var bot = await host.InWorldAsync("Walker");
            var session = host.Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
            var scene = host.Service<SceneService>();
            var loop = host.Service<GameLoop>();
            Assert.Equal(surface, scene.MovesOnSurface(session.SceneEntry));
            Assert.Null(scene.StoryMovementSpeed?.Invoke(session));
            Assert.NotNull(host.Service<WorldData>().Map(session.SceneEntry)!.Nav);
            var start = await loop.InvokeAsync(() => Task.FromResult(session.Position.Clone()));
            await bot.WalkAsync(start.X + .5f, start.Y, start.Z, stop: true);
            await bot.HeartbeatAsync();
            Assert.Equal(start.X + (surface ? .5f : 0),
                await loop.InvokeAsync(() => Task.FromResult(session.Position.X)), 3);
            Assert.Equal(surface ? 0 : 1, host.Service<CheatMonitor>().LoggedCount(bot.Account!, CheatMonitor.Walk));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static void PrepareData(string directory, bool surface)
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
        // 一格空地面格，角色出生區沒有地面導航；不複製任何客戶端資料。
        byte[] nav = new byte[68];
        foreach (var (offset, value) in new[] { (20, 1), (24, 1), (28, 1), (48, 64), (56, 68) })
            BitConverter.GetBytes(value).CopyTo(nav, offset);
        Directory.CreateDirectory(Path.Combine(directory, "nav"));
        File.WriteAllBytes(Path.Combine(directory, "nav", "synthetic_surface_dynamicmapdata.bytes"), nav);
    }
}
