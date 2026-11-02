using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Tests;

/// <summary>WarzoneTaskConfig, a territory battle's personal score per action.</summary>
public sealed class WarzoneTaskTests
{
    private static readonly Lazy<GameData> Exported = new(() => new GameData(
        Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") }), NullLogger<GameData>.Instance));

    [ClientDataFact]
    public void TheCaptureLastHitAndTheListedKillsScoreAsTheTableSays()
    {
        var data = Exported.Value;
        Assert.Equal(50, WarzoneTaskConfig.ScoreOf(data, WarzoneTaskConfig.CaptureLastHit, 0));
        Assert.Equal(20, WarzoneTaskConfig.ScoreOf(data, WarzoneTaskConfig.Kill, 5000001));
        Assert.Equal(10, WarzoneTaskConfig.ScoreOf(data, WarzoneTaskConfig.Kill, 5000002));
        Assert.Equal(0, WarzoneTaskConfig.ScoreOf(data, WarzoneTaskConfig.Kill, 4940017));
    }
}
