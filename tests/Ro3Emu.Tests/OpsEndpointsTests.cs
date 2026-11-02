using System.Net;
using Ro3Emu.Server;
using Ro3Emu.Server.Http;

namespace Ro3Emu.Tests;

/// <summary>
/// The operator's loopback tools (OpsEndpoints, docs/systems/security.md) answer only a local tool that sends the data
/// dir's token: a web page open on the host can POST to 127.0.0.1 too, but it cannot read the token file. Anything else
/// gets the 404 of an unknown path.
/// </summary>
public sealed class OpsEndpointsTests
{
    private static readonly string[] ToolPaths =
        ["/ro3emu/online", "/ro3emu/reports", "/ro3emu/metrics", "/ro3emu/metrics/routes", "/ro3emu/metrics/public-key"];

    [Fact]
    public async Task EveryToolAnswersOnlyTheOperatorToken()
    {
        await using var host = await SyntheticHost.StartAsync();
        using var plain = new HttpClient();
        using var wrong = new HttpClient();
        wrong.DefaultRequestHeaders.Add(OperatorToken.Header, new string('f', 64));
        using var signed = Operator.Client();
        foreach (string path in ToolPaths)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await plain.GetAsync(host.Http + path)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await wrong.GetAsync(host.Http + path)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await signed.GetAsync(host.Http + path)).StatusCode);
        }
        // A page's simple POST (text/plain, no custom header) neither runs a GM command nor stops the server.
        Assert.Equal(HttpStatusCode.NotFound, (await plain.PostAsync(host.Http + "/ro3emu/gm?account=nobody", new StringContent("!lvl 99"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await plain.PostAsync(host.Http + "/ro3emu/shutdown", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await plain.PostAsync(host.Http + "/ro3emu/metrics/reset", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await signed.PostAsync(host.Http + "/ro3emu/gm?account=nobody", new StringContent("!lvl 99"))).StatusCode);
        // The public endpoints stay public.
        Assert.Equal(HttpStatusCode.OK, (await plain.GetAsync(host.Http + "/ro3emu/status")).StatusCode);
    }

    [Theory]
    [InlineData("evil.example", null)] // DNS rebinding: the page's own name resolves to 127.0.0.1
    [InlineData("127.0.0.1", "203.0.113.5")] // a proxy on the same machine forwarded it
    public async Task TheTokenIsNotEnoughFromARebindingPageOrAProxy(string host, string? forwardedFor)
    {
        await using var server = await SyntheticHost.StartAsync();
        using var signed = Operator.Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Http + "/ro3emu/online");
        request.Headers.Host = host;
        if (forwardedFor != null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        Assert.Equal(HttpStatusCode.NotFound, (await signed.SendAsync(request)).StatusCode);

        using var local = new HttpRequestMessage(HttpMethod.Get, server.Http + "/ro3emu/online");
        local.Headers.Host = "localhost";
        Assert.Equal(HttpStatusCode.OK, (await signed.SendAsync(local)).StatusCode);
    }

    [Fact]
    public async Task WithoutAConfiguredTokenTheServerKeepsARandomOneInItsDataDir()
    {
        await using var host = await SyntheticHost.StartAsync(o => o.OperatorToken = null);
        string? token = OperatorToken.Read(host.DataDir);
        Assert.NotNull(token);
        Assert.Matches("^[0-9a-f]{64}$", token);
        Assert.NotEqual(Operator.Token, token);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(OperatorToken.PathIn(host.DataDir)));

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add(OperatorToken.Header, token);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(host.Http + "/ro3emu/online")).StatusCode);
        await host.RestartAsync();
        Assert.Equal(token, OperatorToken.Read(host.DataDir));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(host.Http + "/ro3emu/online")).StatusCode);
    }

    [Fact]
    public void AConfiguredTokenIsWrittenForTheToolsAndAShortOneIsRefused()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ro3emu-operator-" + Guid.NewGuid().ToString("N"));
        try
        {
            var token = OperatorToken.LoadOrCreate(dir, Operator.Token);
            Assert.True(token.Matches(Operator.Token));
            Assert.False(token.Matches(Operator.Token[..^1] + "0"));
            Assert.False(token.Matches(null));
            Assert.Equal(Operator.Token, OperatorToken.Read(dir));
            Assert.Throws<InvalidOperationException>(() => OperatorToken.LoadOrCreate(dir, "short"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task ALoopbackPanelAnswersItsApiOnlyUnderThisMachinesName()
    {
        var (host, panel) = await PanelTests.StartAsync(_ => { });
        await using var _ = host;
        using var http = new HttpClient { BaseAddress = new Uri(panel) };
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/status")).StatusCode);
        using var rebound = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        rebound.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(rebound)).StatusCode);
    }

    [Fact]
    public async Task APanelBehindAProxyAnswersItsPublicName()
    {
        var (host, panel) = await PanelTests.StartAsync(o => o.Panel.PublicUrl = "https://panel.example.com");
        await using var _ = host;
        using var http = new HttpClient { BaseAddress = new Uri(panel) };
        using var proxied = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        proxied.Headers.Host = "panel.example.com";
        proxied.Headers.Add("X-Forwarded-For", "203.0.113.6");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(proxied)).StatusCode);
        using var other = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        other.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(other)).StatusCode);
    }

    [Fact]
    public void TheTestHostsShareOneToken() =>
        Assert.Equal(Operator.Token, Environment.GetEnvironmentVariable("Ro3Emu__" + nameof(ServerOptions.OperatorToken)));
}
