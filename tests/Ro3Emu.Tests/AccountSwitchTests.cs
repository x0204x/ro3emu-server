using MessagePack;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Phonest.Proto;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Micro;

namespace Ro3Emu.Tests;

/// <summary>
/// A second account signing in on a connection that already belongs to one: the first account's registry key goes
/// first, so no key of it keeps pointing at the session; while a role of the first account is in the world, refused.
/// </summary>
public sealed class AccountSwitchTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-switch-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "", _pem = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = SyntheticData.Dir;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
        _pem = File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem"));
    }

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    private Task<T> OnLoop<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    [Fact]
    public async Task AnotherAccountOnTheGameConnectionLeavesNoKeyOfTheFirst()
    {
        await using var alice = new BotClient(_http, _pem);
        await alice.LoginAsync("alice", "pw");
        await alice.EnterGateAsync();
        await using var bob = new BotClient(_http, _pem);
        await bob.LoginAsync("bob", "pw");

        Assert.Equal(ConnectServerResp.Types.RespType.Connected, await alice.ConnectAgainAsync(bob.EngineToken));
        var sessions = await OnLoop(() => Service<SessionRegistry>().All().ToArray());
        var session = Assert.Single(sessions);
        Assert.Equal("bob", session.Account);
    }

    [Fact]
    public async Task AnotherAccountIsRefusedWhileARoleIsInTheWorld()
    {
        await using var alice = new BotClient(_http, _pem);
        await alice.LoginAsync("alice", "pw");
        await alice.EnterGateAsync();
        await alice.EnterWorldAsync((await alice.CreateRoleAsync("Alice")).RoleBaseInfo.Id);
        await using var bob = new BotClient(_http, _pem);
        await bob.LoginAsync("bob", "pw");

        Assert.Equal(ConnectServerResp.Types.RespType.ConnectRefused, await alice.ConnectAgainAsync(bob.EngineToken));
        var session = Assert.Single(await OnLoop(() => Service<SessionRegistry>().All().ToArray()));
        Assert.Equal("alice", session.Account);
        Assert.NotNull(session.Role);
    }

    [Fact]
    public async Task AnotherAccountVerifiedOnTheMicroConnectionTakesTheFirstOnesKeyAway()
    {
        var micro = Service<MicroSessions>();
        var router = Service<MicroServiceRouter>();
        var accounts = Service<AccountStore>();
        await accounts.SignInAsync("alice", "pw");
        await accounts.SignInAsync("bob", "pw");
        var session = new MicroSession(null!, 1);
        async Task Verify(string account) => await router.InvokeAsync(session, "LoginService", "verifySdk",
            MessagePackSerializer.Serialize(new Dictionary<string, object> { ["Ticket"] = await accounts.IssueSdkTokenAsync(account) }));

        await Verify("alice");
        Assert.Same(session, micro.For("alice"));
        await Verify("bob");
        Assert.Equal("bob", session.Account);
        Assert.Same(session, micro.For("bob"));
        Assert.Null(micro.For("alice")); // pushes for alice no longer reach bob's connection
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }
}
