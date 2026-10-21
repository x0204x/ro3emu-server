using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Diagnostics;

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
        Assert.Equal(JsonValueKind.String, sessions.GetProperty("capturedUtc").ValueKind);

        var online = (await Get(client, "/ro3emu/online")).EnumerateArray().ToDictionary(
            s => s.GetProperty("account").GetString()!, s => s.GetProperty("role"));
        Assert.Equal("Walker", online["walker"].GetString());
        Assert.Equal(JsonValueKind.Null, online["lobby"].ValueKind);
    }
}
