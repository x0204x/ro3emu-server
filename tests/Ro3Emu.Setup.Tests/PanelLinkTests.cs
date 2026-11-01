using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Ro3Emu.Launcher;

namespace Ro3Emu.Setup.Tests;

/// <summary>The web panel link: the launcher's Web panel entry per mode, RO3Emu.exe panel, and launcher.json's panelUrl.</summary>
public class PanelLinkTests
{
    private const string Local = "http://127.0.0.1:5381";

    [Fact]
    public void TheOperatorsPanelUrlWinsInEitherMode()
    {
        var expected = new PanelLink.Entry(PanelLink.Open, "https://playnusky.com:8443", null);
        Assert.Equal(expected, PanelLink.ForLauncher("https://playnusky.com:8443", ro3emuMode: false, false, null, false, null, false));
        Assert.Equal(expected, PanelLink.ForLauncher(" https://playnusky.com:8443 ", ro3emuMode: true, joined: true, null, false, Local, true));
    }

    [Fact]
    public void AnOperatorsLauncherWithoutPanelUrlHasNoEntry()
    {
        Assert.Null(PanelLink.ForLauncher(null, ro3emuMode: false, false, null, false, Local, true));
        Assert.Null(PanelLink.ForLauncher("ftp://panel.example.com", ro3emuMode: false, false, null, false, Local, true));
    }

    [Fact]
    public void LocalPlayOpensTheLocalPanelOrStartsTheServerFirst()
    {
        Assert.Equal(new PanelLink.Entry(PanelLink.Open, Local, null),
            PanelLink.ForLauncher(null, ro3emuMode: true, joined: false, null, false, Local, localRunning: true));
        var stopped = PanelLink.ForLauncher(null, ro3emuMode: true, joined: false, null, false, Local, localRunning: false);
        Assert.Equal((PanelLink.Start, Local), (stopped?.State, stopped?.Url));
        Assert.Equal(PanelLink.Unavailable, PanelLink.ForLauncher(null, ro3emuMode: true, false, null, false, localUrl: null, false)?.State);
    }

    [Fact]
    public void AJoinedServerShowsItsPanelOnlyWhenItAnswered()
    {
        Assert.Equal(new PanelLink.Entry(PanelLink.Open, "http://203.0.113.10:5381", null),
            PanelLink.ForLauncher(null, ro3emuMode: true, joined: true, "http://203.0.113.10:5381", false, Local, false));
        Assert.Equal(PanelLink.Checking, PanelLink.ForLauncher(null, ro3emuMode: true, joined: true, null, joinProbing: true, Local, false)?.State);
        var none = PanelLink.ForLauncher(null, ro3emuMode: true, joined: true, null, joinProbing: false, Local, true);
        Assert.Equal((PanelLink.Unavailable, (string?)null), (none?.State, none?.Url));
        Assert.Contains("has not shared its web panel", none?.Hint);
    }

    [Theory]
    [InlineData("https://playnusky.com:8443", "https://playnusky.com:8443")]
    [InlineData("http://panel.example.com/ro3/", "http://panel.example.com/ro3/")]
    [InlineData(" http://127.0.0.1:5381/ ", "http://127.0.0.1:5381/")]
    [InlineData("", null)]
    [InlineData("panel.example.com", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("file:///C:/x.html", null)]
    public void PanelUrlMustBeHttpOrHttps(string url, string? expected)
    {
        Assert.Equal(expected, PanelLink.Configured(url));
        // WPF 入口沿用相同網址規則，不需要引用舊 WebView 控制器。
        Assert.Equal(expected, new LauncherConfig { PanelUrl = url }.ConfiguredPanelUrl);
    }

    [Theory]
    [InlineData(null, "http://127.0.0.1:5381")]
    [InlineData("0.0.0.0", "http://127.0.0.1:5381")]
    [InlineData("127.0.0.1", "http://127.0.0.1:5381")]
    [InlineData("::", "http://[::1]:5381")]
    [InlineData("::1", "http://[::1]:5381")]
    [InlineData("192.168.1.20", "http://192.168.1.20:5381")]
    [InlineData("not an address", "http://127.0.0.1:5381")]
    public void TheLocalPanelIsReachedOnItsBindAddress(string? bind, string expected) =>
        Assert.Equal(expected, PanelLink.Local(new ServerProcess.Ports(), bind));

    [Fact]
    public void TheLocalPanelFollowsThePanelPortAndIsNoneWhenOff()
    {
        Assert.Equal("http://127.0.0.1:5391", PanelLink.Local(Options.Parse(["start", "--panel-port", "5391"]).Ports));
        Assert.Null(PanelLink.Local(Options.Parse(["start", "--no-panel"]).Ports));
    }

    [Fact]
    public void TheBindAddressComesFromTheServersSettingsThenTheEnvironment()
    {
        var dir = Directory.CreateTempSubdirectory("ro3emu-panel-");
        try
        {
            Assert.Null(PanelLink.BindAddress(dir.FullName, "Development", _ => null));
            File.WriteAllText(Path.Combine(dir.FullName, "appsettings.json"), """{ "Ro3Emu": { "BindAddress": "10.0.0.5" } }""");
            Assert.Equal("10.0.0.5", PanelLink.BindAddress(dir.FullName, "Development", _ => null));
            File.WriteAllText(Path.Combine(dir.FullName, "appsettings.Production.json"),
                """{ "Ro3Emu": { "Panel": { "BindAddress": "127.0.0.1" } } } // the proxy setup of docs/guide/panel.md""");
            Assert.Equal("127.0.0.1", PanelLink.BindAddress(dir.FullName, "Production", _ => null));
            Assert.Equal("10.0.0.5", PanelLink.BindAddress(dir.FullName, "Development", _ => null));
            var env = new Dictionary<string, string> { ["Ro3Emu__Panel__BindAddress"] = "192.168.1.20" };
            Assert.Equal("192.168.1.20", PanelLink.BindAddress(dir.FullName, "Production", k => env.GetValueOrDefault(k)));
            File.WriteAllText(Path.Combine(dir.FullName, "appsettings.json"), "{ broken");
            Assert.Null(PanelLink.BindAddress(dir.FullName, "Development", _ => null));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("http://203.0.113.10:5380", new[] { "http://203.0.113.10:5381" })]
    [InlineData("http://203.0.113.10:5390", new[] { "http://203.0.113.10:5391", "http://203.0.113.10:5381" })]
    [InlineData("https://play.example.com:443", new[] { "https://play.example.com:444", "https://play.example.com:5381" })]
    [InlineData("http://[2001:db8::1]:5380", new[] { "http://[2001:db8::1]:5381" })]
    public void AJoinedServersPanelIsLookedForNextToItsPort(string serverUrl, string[] expected) =>
        Assert.Equal(expected, PanelLink.JoinCandidates(serverUrl));

    [Fact]
    public void TheConsoleHasAPanelCommand()
    {
        var o = Options.Parse(["panel", "203.0.113.10", "--panel-port", "5391"]);
        Assert.Equal(("panel", "203.0.113.10", 5391), (o.Command, o.Positional.Single(), o.Ports.Panel));
        Assert.Contains("panel [address]", Program.Usage);
    }

    [Fact]
    public async Task OnlyAnRo3EmuPanelCountsAsAnswering()
    {
        await using var panel = await FakeServer("""{ "name": "RO3Emu", "players": 2, "uptimeSeconds": 12 }""");
        await using var other = await FakeServer("""{ "status": "ok" }""");
        string closed = $"http://127.0.0.1:{FreePort()}";
        Assert.Equal(panel.Url, await PanelLink.FirstAnsweringAsync([closed, other.Url, panel.Url], TimeSpan.FromSeconds(3)));
        Assert.Null(await PanelLink.FirstAnsweringAsync([closed, other.Url], TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task AJoinedServerAnnouncesItsPanelBehindAProxy()
    {
        await using var panel = await FakeServer("""{ "name": "Nusky", "uptimeSeconds": 5 }""");
        await using var game = await FakeServer("{}", announce: panel.Url + "/");
        var candidates = await PanelLink.JoinCandidatesAsync(game.Url, TimeSpan.FromSeconds(3));
        Assert.Equal(panel.Url, candidates[0]);
        Assert.Equal(PanelLink.JoinCandidates(game.Url), candidates.Skip(1));
        Assert.Equal(panel.Url, await PanelLink.FirstAnsweringAsync(candidates, TimeSpan.FromSeconds(3)));

        // A server that announces nothing (404): only the usual places.
        await using var plain = await FakeServer("{}");
        Assert.Equal(PanelLink.JoinCandidates(plain.Url), await PanelLink.JoinCandidatesAsync(plain.Url, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void LauncherJsonReadsPanelUrl()
    {
        var dir = Directory.CreateTempSubdirectory("ro3emu-launcher-");
        try
        {
            string file = Path.Combine(dir.FullName, "launcher.json");
            File.WriteAllText(file, """
                {
                  "repository": "https://patch.example.com/ro3/",
                  // the server's web panel behind the operator's proxy
                  "panelUrl": "https://playnusky.com:8443",
                }
                """);
            var config = LauncherConfig.Load(file);
            Assert.Equal("https://patch.example.com/ro3/", config.Repository);
            Assert.Equal("https://playnusky.com:8443", config.PanelUrl);
            Assert.Equal(config.PanelUrl, config.ConfiguredPanelUrl);
            File.WriteAllText(file, """{ "repository": "https://patch.example.com/ro3/" }""");
            Assert.Null(LauncherConfig.Load(file).PanelUrl);
            Assert.Null(LauncherConfig.Load(file).ConfiguredPanelUrl);
        }
        finally { dir.Delete(recursive: true); }
    }

    private sealed class Fake(WebApplication app) : IAsyncDisposable
    {
        public string Url => app.Urls.First().TrimEnd('/');

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    /// <summary>A loopback HTTP server answering /api/status with <paramref name="status"/>, and /ro3emu/panel with
    /// <paramref name="announce"/> when given.</summary>
    private static async Task<Fake> FakeServer(string status, string? announce = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(async context =>
        {
            if (announce != null && context.Request.Path == "/ro3emu/panel")
            {
                await context.Response.WriteAsJsonAsync(new { url = announce });
                return;
            }
            if (context.Request.Path != "/api/status") { context.Response.StatusCode = 404; return; }
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(status);
        });
        await app.StartAsync();
        return new Fake(app);
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
