using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// A TaskConfig _iAward is a MultiverseGroupConfig id; one that has no such row but names an AwardConfig row is granted
/// as that row (AwardService.ResolveTask, issue #166). Synthetic data: group 9301 routes to award 9401.
/// </summary>
public sealed class AwardTaskRouteTests
{
    private readonly AwardService _awards = new(new GameData(
        Options.Create(new ServerOptions { GameDataDir = SyntheticData.Dir }), NullLogger<GameData>.Instance));

    [Fact]
    public void AGroupIdResolvesThroughItsGroup()
    {
        var plan = _awards.ResolveTask(null!, 9301, null);
        Assert.Equal(9301, plan.GroupId);
        Assert.Equal(9401, plan.AwardId);
    }

    [Fact]
    public void AnAwardIdWithoutAGroupResolvesAsThatAward()
    {
        var plan = _awards.ResolveTask(null!, 9402, null);
        Assert.Equal(0, plan.GroupId);
        Assert.Equal(9402, plan.AwardId);
        Assert.Equal([new AwardGrant(102, 77)], plan.Grants);
    }

    [Fact]
    public void AnIdInNeitherTableIsStillRefused() =>
        Assert.Throws<AwardResolutionException>(() => _awards.ResolveTask(null!, 9999, null));
}
