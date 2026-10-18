using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>The synthetic game data (Fixtures/GameData) boots a whole server without the client's export.</summary>
public sealed class SyntheticFixtureTests
{
    [Fact]
    public async Task ANewRoleEntersTheFixturesTownWithItsStarterKit()
    {
        // The preload parses every fixture table, so a malformed file fails here and not in some later test.
        await using var host = await SyntheticHost.StartAsync(o => o.PreloadGameData = true);
        Assert.Equal(SyntheticData.Dir, host.Service<IOptions<ServerOptions>>().Value.GameDataDir);
        await using var bot = await host.InWorldAsync("Fixture");

        var session = await host.Service<GameLoop>().InvokeAsync(() =>
            Task.FromResult(host.Service<SessionRegistry>().All().Single(s => s.Role != null)));
        Assert.Equal(SyntheticData.Town, session.SceneEntry);
        Assert.NotNull(host.Service<WorldData>().Map(SyntheticData.Town)?.Born);
        // The kit CharacterService gives a new role: its items exist in the fixture's ItemConfig.
        Assert.Contains(session.Items, i => i.Entry == 1000000);
        Assert.Equal(1000, session.Progress!.Currency[1]);
    }
}
