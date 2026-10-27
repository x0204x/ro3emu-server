using System.Text.Json;
using Ro3Emu.Server;
using Ro3Emu.Server.Http;

namespace Ro3Emu.Tests;

/// <summary>The Genesis client formats its server clock with the civil offset advertised by the selected cluster.</summary>
public sealed class ServerListTimezoneTests
{
    private sealed class FixedClock : TimeProvider
    {
        private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
            "RO3 Test UTC+8", TimeSpan.FromHours(8), "RO3 Test UTC+8", "RO3 Test UTC+8");

        public override DateTimeOffset GetUtcNow() => new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => Zone;
    }

    [Fact]
    public async Task ServerListAdvertisesTheRealmCivilTimezone()
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-server-timezone-" + Guid.NewGuid().ToString("N"));
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        var app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = SyntheticData.Dir;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        }, new FixedClock());

        await app.StartAsync();
        try
        {
            using var http = new HttpClient();
            string response = await http.GetStringAsync($"http://127.0.0.1:{httpPort}{SdkEndpoints.ServerListPath}");
            const string prefix = "RO3_RECOVERY_JSON:";
            Assert.StartsWith(prefix, response);

            using var body = JsonDocument.Parse(response[prefix.Length..]);
            string extText = body.RootElement.GetProperty("data").GetProperty("clusters")[0].GetProperty("ext").GetString()!;
            using var ext = JsonDocument.Parse(extText);
            Assert.Equal(28_800, ext.RootElement.GetProperty("utc_offset_seconds").GetInt32());
            Assert.Equal("RO3 Test UTC+8", ext.RootElement.GetProperty("tz").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }
}
