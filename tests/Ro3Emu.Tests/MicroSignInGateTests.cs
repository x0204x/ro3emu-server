using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Micro;

namespace Ro3Emu.Tests;

/// <summary>
/// The micro gate before an account is bound to the connection: only LoginService.verifySdk and PhonestGate.bind
/// reach their service; every other call is refused with "not verified" before it is dispatched.
/// </summary>
public sealed class MicroSignInGateTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-micro-" + Guid.NewGuid().ToString("N"));
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

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private static string Message(Dictionary<object, object?> response) =>
        (string)((object[])response["Ret"]!)[1]!;

    [Fact]
    public async Task AnUnverifiedConnectionReachesNoOtherService()
    {
        await using var bot = new BotClient(_http, _pem);
        await bot.LoginAsync("unbound", "pw");
        await bot.ReconnectMicroAsync();

        // A method no service has would answer "<service>.probe not implemented" once dispatched.
        foreach (string service in _app.Services.GetRequiredService<MicroServiceRouter>().ServiceNames)
            Assert.Equal("not verified", Message(await bot.MicroAsync(service, "probe", [])));
        Assert.Equal("not verified", Message(await bot.MicroAsync("LoginService", "getRoleInfo", [])));
    }

    [Fact]
    public async Task TheTicketCheckAndTheGateBindStillReachTheirServices()
    {
        await using var bot = new BotClient(_http, _pem);
        await bot.LoginAsync("unbound", "pw");
        await bot.ReconnectMicroAsync();

        Assert.Equal("invalid ticket", Message(await bot.MicroAsync("LoginService", "verifySdk",
            new Dictionary<string, object?> { ["Ticket"] = "wrong" })));
        Assert.False(await bot.BindGateAsync("nobody", new byte[16]));
        Assert.Equal("bind refused", Message(await bot.MicroAsync("PhonestGate", "bind",
            new Dictionary<string, object?> { ["Id"] = "nobody", ["Auth"] = new byte[16] })));
    }
}
