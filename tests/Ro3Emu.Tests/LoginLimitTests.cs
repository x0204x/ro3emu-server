using System.Net;
using System.Text.Json;
using Ro3Emu.Server;
using Ro3Emu.Server.Http;

namespace Ro3Emu.Tests;

/// <summary>Credential checks over HTTP have a per-address budget (each one is a PBKDF2 hash and may register).</summary>
public sealed class LoginLimitTests
{
    private static async Task<List<HttpStatusCode>> Logins(bool limitLoopback, int attempts)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-loginlimit-" + Guid.NewGuid().ToString("N"));
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        var app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = SyntheticData.Dir;
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.LoginAttemptsPerMinute = 3;
            o.LoginLimitIncludesLoopback = limitLoopback;
        });
        await app.StartAsync();
        try
        {
            using var client = new HttpClient();
            var codes = new List<HttpStatusCode>();
            for (int i = 0; i < attempts; i++)
            {
                var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["identity"] = "limited", ["credential"] = "wrong" + i });
                codes.Add((await client.PostAsync($"http://127.0.0.1:{httpPort}{SdkEndpoints.LoginPath}", form)).StatusCode);
            }
            return codes;
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task PastTheBudgetAnAddressGets429WithoutAHash()
    {
        var codes = await Logins(limitLoopback: true, attempts: 5);
        Assert.All(codes.Take(3), c => Assert.Equal(HttpStatusCode.OK, c));
        Assert.All(codes.Skip(3), c => Assert.Equal(HttpStatusCode.TooManyRequests, c));
    }

    [Fact]
    public async Task LoopbackIsExemptByDefault()
    {
        Assert.All(await Logins(limitLoopback: false, attempts: 6), c => Assert.Equal(HttpStatusCode.OK, c));
    }

    [Fact]
    public async Task UnityFormRegistersAndMissingCredentialsReturnSafeJson()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-loginform-" + Guid.NewGuid().ToString("N"));
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        var app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = SyntheticData.Dir;
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
        });
        await app.StartAsync();
        try
        {
            using var client = new HttpClient();
            using var success = await client.PostAsync($"http://127.0.0.1:{httpPort}{SdkEndpoints.LoginPath}",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["identity"] = "unity-form",
                    ["credential"] = "s+e%c&r=et 中文",
                    ["channel_id"] = "15",
                }));
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
            using var successJson = JsonDocument.Parse(await success.Content.ReadAsStringAsync());
            Assert.Equal(0, successJson.RootElement.GetProperty("code").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(successJson.RootElement.GetProperty("data").GetProperty("token").GetString()));

            using var missing = await client.PostAsync($"http://127.0.0.1:{httpPort}{SdkEndpoints.LoginPath}",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["channel_id"] = "15" }));
            Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
            using var missingJson = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
            Assert.Equal(401, missingJson.RootElement.GetProperty("code").GetInt32());
            Assert.Equal("missing credentials", missingJson.RootElement.GetProperty("message").GetString());

            using var wrongContentType = await client.PostAsync($"http://127.0.0.1:{httpPort}{SdkEndpoints.LoginPath}",
                new StringContent("identity=unity-form&credential=secret"));
            Assert.Equal(HttpStatusCode.OK, wrongContentType.StatusCode);
            using var wrongContentTypeJson = JsonDocument.Parse(await wrongContentType.Content.ReadAsStringAsync());
            Assert.Equal(401, wrongContentTypeJson.RootElement.GetProperty("code").GetInt32());
            Assert.Equal("invalid login form", wrongContentTypeJson.RootElement.GetProperty("message").GetString());
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
