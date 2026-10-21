using Microsoft.Extensions.Logging.Abstractions;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Micro;

namespace Ro3Emu.Tests;

/// <summary>
/// The account transition of a connection lives in its registry (SessionRegistry.SignInAsync, MicroSessions.SignIn):
/// a sign-in binds and indexes the account; another account on the same connection leaves no key of the first one,
/// and a game connection with a role in the world keeps its account. No client data needed.
/// </summary>
public sealed class SessionIdentityTests
{
    private static SessionRegistry Registry() => new(NullLogger<SessionRegistry>.Instance);
    private static GameSession Game() => new(null!, null!);
    private static Role RoleOf(string account) => new(100001, account, 1, "Hero", 1, 1000, 1, 0, 0, 0, 10111, 0, 0, 0, 0);

    [Fact]
    public async Task AFirstSignInBindsAndRegistersTheAccount()
    {
        var registry = Registry();
        var session = Game();
        Assert.True(await registry.SignInAsync(session, "alice", register: true));
        Assert.Equal("alice", session.Account);
        Assert.Same(session, Assert.Single(registry.All()));
        Assert.True(registry.IsWinner(session));
    }

    [Fact]
    public async Task AnotherAccountOnTheSameConnectionLeavesNoKeyOfTheFirst()
    {
        var registry = Registry();
        var session = Game();
        await registry.SignInAsync(session, "alice", register: true);
        Assert.True(await registry.SignInAsync(session, "bob", register: true));
        Assert.Equal("bob", session.Account);
        Assert.Same(session, Assert.Single(registry.All())); // one entry, under bob
        registry.Unregister(session);
        Assert.Empty(registry.All()); // nothing left under alice either

        // alice signs in again elsewhere: a fresh session, not a stale key pointing at bob's.
        var again = Game();
        await registry.SignInAsync(again, "alice", register: true);
        Assert.Same(again, Assert.Single(registry.All()));
        Assert.True(registry.IsWinner(again));
    }

    [Fact]
    public async Task AConnectionWithARoleInTheWorldKeepsItsAccount()
    {
        var registry = Registry();
        var session = Game();
        await registry.SignInAsync(session, "alice", register: true);
        session.Role = RoleOf("alice");
        Assert.False(await registry.SignInAsync(session, "bob", register: true));
        Assert.Equal("alice", session.Account);
        Assert.Same(session, Assert.Single(registry.All()));
        Assert.True(registry.IsWinner(session));
    }

    [Fact]
    public async Task TheSameAccountAgainKeepsItsRegistration()
    {
        var registry = Registry();
        var session = Game();
        await registry.SignInAsync(session, "alice", register: true);
        session.Role = RoleOf("alice");
        Assert.True(await registry.SignInAsync(session, "alice", register: false)); // SwitchToAccount: back to the role screen
        Assert.Same(session, Assert.Single(registry.All()));
    }

    [Fact]
    public void AMicroConnectionThatChangesAccountStopsReceivingTheFirstOnesPushes()
    {
        var micro = new MicroSessions(NullLogger<MicroSessions>.Instance);
        var session = new MicroSession(null!, 1);
        micro.SignIn(session, "alice");
        Assert.Same(session, micro.For("alice"));
        micro.SignIn(session, "bob");
        Assert.Equal("bob", session.Account);
        Assert.Same(session, micro.For("bob"));
        Assert.Null(micro.For("alice"));
    }

    [Fact]
    public void ANewerMicroConnectionOfTheAccountTakesItsPushesAndTheOlderOneCannotDropIt()
    {
        var micro = new MicroSessions(NullLogger<MicroSessions>.Instance);
        var older = new MicroSession(null!, 1);
        var newer = new MicroSession(null!, 2);
        micro.SignIn(older, "alice");
        micro.SignIn(newer, "alice");
        Assert.Same(newer, micro.For("alice"));
        micro.SignIn(older, "bob"); // the older connection moves on: alice's key is the newer one's, it stays
        Assert.Same(newer, micro.For("alice"));
        micro.Remove(older);
        Assert.Same(newer, micro.For("alice"));
    }
}
