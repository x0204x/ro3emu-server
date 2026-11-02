using System.Net;
using System.Text.Json;
using Ro3Emu.Server;
using Ro3Emu.Server.Http;

namespace Ro3Emu.Tests;

/// <summary>Credential checks over HTTP have a per-address budget (each one is a PBKDF2 hash and may register).</summary>
public sealed class LoginLimitTests
{
    /// <summary>
    /// The budget is a fixed window per wall-clock minute (CredentialLimiter): on the system clock the attempts of a
    /// loaded run, each a PBKDF2 hash, could straddle a minute boundary and start a fresh budget mid-test.
    /// </summary>
    private sealed class FrozenClock : TimeProvider
    {
        private readonly DateTimeOffset _now = new(2026, 10, 15, 12, 0, 30, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static async Task<List<HttpStatusCode>> Logins(bool limitLoopback, int attempts, string? forwardedFor = null,
        Action<ServerOptions>? configure = null)
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
            configure?.Invoke(o);
        }, new FrozenClock());
        await app.StartAsync();
        try
        {
            using var client = new HttpClient();
            if (forwardedFor != null) client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
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
            Assert.Equal(HttpStatusCode.BadRequest, wrongContentType.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// A proxy on the same machine connects from loopback too. A forwarded request is a player behind it, not a local
    /// tool, so it gets the budget: an untrusted proxy's players share the proxy's address, a trusted one's their own.
    /// </summary>
    [Fact]
    public async Task AForwardedRequestFromLoopbackIsLimited()
    {
        var codes = await Logins(limitLoopback: false, attempts: 5, forwardedFor: "203.0.113.7");
        Assert.All(codes.Take(3), c => Assert.Equal(HttpStatusCode.OK, c));
        Assert.All(codes.Skip(3), c => Assert.Equal(HttpStatusCode.TooManyRequests, c));
    }

    [Fact]
    public async Task BehindATrustedProxyTheBudgetIsTheForwardedClients()
    {
        var codes = await Logins(limitLoopback: false, attempts: 4, forwardedFor: "203.0.113.8",
            configure: o => o.Panel.TrustedProxies = ["127.0.0.1"]);
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[3]);
    }

    /// <summary>One subscriber holds a whole IPv6 /64, so all of it shares one budget, also for gift codes.</summary>
    [Fact]
    public void AnIpv6NetworkSharesOneBudget()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ServerOptions { LoginAttemptsPerMinute = 2, GiftCodeAttemptsPerMinute = 2 });
        var logins = new CredentialLimiter(options, new FrozenClock());
        var gifts = new GiftCodeLimiter(options, new FrozenClock());
        Ro3Emu.Panel.RequestClient From(string address) => new(IPAddress.Parse(address), false);
        foreach (Func<Ro3Emu.Panel.RequestClient, bool> budget in new Func<Ro3Emu.Panel.RequestClient, bool>[] { logins.TryAcquire, c => gifts.TryAcquire(c, null) })
        {
            Assert.True(budget(From("2001:db8:1:1::1")));
            Assert.True(budget(From("2001:db8:1:1::2")));
            Assert.False(budget(From("2001:db8:1:1:ffff::3")));
            Assert.True(budget(From("2001:db8:1:2::1"))); // another /64
        }
    }

    [Fact]
    public async Task ATunnelEndsTheLoopbackExemption()
    {
        // frp or ssh -R: strangers arrive on loopback with no header, but the client is told a public address.
        var codes = await Logins(limitLoopback: false, attempts: 4, configure: o => { o.PublicHost = "192.0.2.10"; o.BindAddress = "127.0.0.1"; });
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[3]);
    }

    /// <summary>A body that is not a form, or a form far past what these calls send, is a bad request, not a 500 with a stack.</summary>
    [Theory]
    [InlineData(SdkEndpoints.LoginPath)]
    [InlineData("/ro3emu/account/delete-role")]
    public async Task TheCredentialFormsRefuseOtherBodies(string path)
    {
        await using var host = await SyntheticHost.StartAsync();
        using var client = new HttpClient();
        var plain = await client.PostAsync(host.Http + path, new StringContent("identity=a&credential=b", System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        var huge = new FormUrlEncodedContent(new Dictionary<string, string> { ["identity"] = "a", ["credential"] = new('x', 1024 * 1024) });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(host.Http + path, huge)).StatusCode);
    }

    /// <summary>The login history names the player behind a trusted proxy, not the proxy.</summary>
    [Fact]
    public async Task AGameLoginBehindATrustedProxyRecordsThePlayersAddress()
    {
        await using var host = await SyntheticHost.StartAsync(o =>
        {
            o.Panel.TrustedProxies = ["127.0.0.1"];
            o.Panel.LoginAddresses = true;
        });
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.30");
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["identity"] = "behind-proxy", ["credential"] = "pw-game" });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(host.Http + SdkEndpoints.LoginPath, form)).StatusCode);
        var logins = await host.Service<Ro3Emu.Panel.IPanelOperations>().LoginsAsync("behind-proxy", 5);
        Assert.Equal("203.0.113.30", Assert.Single(logins).Address);
    }

    [Fact]
    public async Task AConfiguredProxyEndsTheLoopbackExemption()
    {
        // A proxy that sends no forwarding header still announces itself in the panel options.
        var codes = await Logins(limitLoopback: false, attempts: 4, configure: o => o.Panel.PublicUrl = "https://panel.example.com");
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[3]);
    }

    [Theory]
    [InlineData("127.0.0.1", false, false, false)]
    [InlineData("0.0.0.0", false, false, true)]
    [InlineData("127.0.0.1", true, false, true)]
    [InlineData("127.0.0.1", false, true, true)]
    public void ThePanelIsPublicFacingOnAPublicBindOrBehindAProxy(string bind, bool trustedProxy, bool publicUrl, bool expected)
    {
        var panel = new Ro3Emu.Panel.PanelOptions
        {
            TrustedProxies = trustedProxy ? ["127.0.0.1"] : [],
            PublicUrl = publicUrl ? "https://panel.example.com" : null,
        };
        Assert.Equal(expected, panel.PublicFacing(IPAddress.Parse(bind)));
    }
}
