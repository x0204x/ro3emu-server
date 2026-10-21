using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Realm passes over sessions: a role refusing new mutations skips only itself.</summary>
public sealed class RoleFanoutTests
{
    [Fact]
    public async Task ACanceledRoleIsSkippedAndLaterSessionsStillRunInOrder()
    {
        var quitting = new GameSession(null!, null!);
        var second = new GameSession(null!, null!);
        var third = new GameSession(null!, null!);
        var ran = new List<GameSession>();
        await RoleFanout.EachAsync([quitting, second, third], s =>
        {
            if (s == quitting) throw new OperationCanceledException("Role is not admitting new mutations.");
            ran.Add(s);
            return Task.CompletedTask;
        });
        Assert.Equal([second, third], ran);
    }

    [Fact]
    public async Task OtherFailuresStillPropagate()
    {
        var a = new GameSession(null!, null!);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RoleFanout.EachAsync([a], _ => throw new InvalidOperationException("real bug")));
    }
}
