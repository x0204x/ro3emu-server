using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Diagnostics;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The HTTP endpoints that report sessions read them where they change: /ro3emu/status and /ro3emu/metrics from the
/// loop's census, /ro3emu/online on the loop.
/// </summary>
public sealed class HttpStateTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-http-" + Guid.NewGuid().ToString("N"));
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

    private async Task<JsonElement> Get(HttpClient client, string path) =>
        JsonSerializer.Deserialize<JsonElement>(await client.GetStringAsync(_http + path));

    [Fact]
    public async Task StatusMetricsAndOnlineReportARoleInTheWorld()
    {
        await using var walker = new BotClient(_http, _pem);
        await walker.LoginAsync("walker", "pw");
        await walker.EnterGateAsync();
        await walker.EnterWorldAsync((await walker.CreateRoleAsync("Walker")).RoleBaseInfo.Id);
        await using var lobby = new BotClient(_http, _pem);
        await lobby.LoginAsync("lobby", "pw");
        await lobby.EnterGateAsync();

        // The census is taken once a second; wait for one taken after both sessions were registered.
        var census = _app.Services.GetRequiredService<SessionCensus>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (census.Current is not { Login: 1, Roles.Count: 1 } && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        using var client = new HttpClient();
        Assert.Equal(1, (await Get(client, "/ro3emu/status")).GetProperty("players").GetInt32());
        var sessions = (await Get(client, "/ro3emu/metrics")).GetProperty("sessions");
        Assert.Equal(1, sessions.GetProperty("login").GetInt32());
        Assert.Equal(1, sessions.GetProperty("inWorld").GetInt32() + sessions.GetProperty("loading").GetInt32());
        // The metrics carry the census they were read from, not a fresh pass over the live sessions.
        Assert.Equal(census.Current.CapturedUtc, sessions.GetProperty("capturedUtc").GetDateTimeOffset());

        var online = (await Get(client, "/ro3emu/online")).EnumerateArray().ToDictionary(
            s => s.GetProperty("account").GetString()!, s => s.GetProperty("role"));
        Assert.Equal("Walker", online["walker"].GetString());
        Assert.Equal(JsonValueKind.Null, online["lobby"].ValueKind);
    }

    [Fact]
    public async Task TheCensusChangesOnlyWhenTheLoopTakesIt()
    {
        var registry = new SessionRegistry(NullLogger<SessionRegistry>.Instance);
        var session = new GameSession(null!, null!) { Account = "census", SceneEntry = 10100 };
        await registry.RegisterAsync(session);
        var census = new SessionCensus(registry);
        Assert.Null(census.Current.CapturedUtc);

        census.Tick(new GameTime(TimeSpan.Zero, TimeSpan.Zero));
        Assert.Equal(1, census.Current.Login);
        session.Role = new Role(1, "census", 1, "Census", 1, 1000, 1, 1, 0, 0, 10100, 0, 0, 0, 0);

        // Within the interval the published snapshot keeps what the loop last saw.
        census.Tick(new GameTime(SessionCensus.Interval / 2, SessionCensus.Interval / 2));
        Assert.Equal(1, census.Current.Login);
        Assert.Empty(census.Current.Roles);

        census.Tick(new GameTime(SessionCensus.Interval, SessionCensus.Interval / 2));
        Assert.Equal(0, census.Current.Login);
        Assert.Single(census.Current.Roles);
    }
}
