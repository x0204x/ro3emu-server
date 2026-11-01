using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Ro3Emu.Server.Updates;
using static Ro3Emu.Tests.PanelTests;

namespace Ro3Emu.Tests;

/// <summary>
/// The Updates section of the panel's Server control against a running server: the routes are the Admins' alone, a
/// source build only checks, and a release folder downloads, checks, stages and plans an install from a local releases
/// feed (a loopback stand-in for GitHub; nothing leaves the machine). The install's last step runs against a release
/// folder in the test's temp directory. Accounts and releases are invented.
/// </summary>
public sealed class PanelUpdateTests : IAsyncLifetime
{
    private const string Admin = "pupdate-admin", Moderator = "pupdate-mod", Repository = "test/ro3emu";
    private static readonly UpdatePlatform Here = UpdatePlatform.Of(UpdatePlatform.Current()) ?? UpdatePlatform.Linux;
    private readonly string _dir = UpdateFixtures.TempDir("panel");
    private WebApplication _feed = null!;
    private string _feedUrl = "";
    private readonly Dictionary<string, byte[]> _downloads = new(StringComparer.Ordinal);
    private string _releases = "[]";

    public async Task InitializeAsync()
    {
        int port = TestPorts.FreePort();
        _feedUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(_feedUrl);
        _feed = builder.Build();
        _feed.Run(async context =>
        {
            string path = context.Request.Path.Value ?? "";
            if (path == "/repos/" + Repository + "/releases")
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(_releases);
            }
            else if (path.StartsWith("/dl/", StringComparison.Ordinal) && _downloads.TryGetValue(path[4..], out var bytes))
                await context.Response.Body.WriteAsync(bytes);
            else context.Response.StatusCode = StatusCodes.Status404NotFound;
        });
        await _feed.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _feed.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A release on the feed with this platform's package; a wrong digest makes the download fail its check.</summary>
    private void Publish(string tag, bool goodDigest = true)
    {
        byte[] package = UpdateFixtures.Package(Here, UpdateFixtures.ReleaseFiles(Here, tag.TrimStart('v') + "+feedf00"));
        _downloads[tag + "-" + Here.Asset] = package;
        string digest = "sha256:" + (goodDigest ? UpdateFixtures.Sha256(package) : new string('a', 64));
        var list = JsonSerializer.Deserialize<List<JsonElement>>(_releases)!.Select(e => e.GetRawText()).ToList();
        string one = UpdateFixtures.ReleasesJson((tag, false, false, [(Here.Asset, _feedUrl + "/dl/" + tag + "-" + Here.Asset, package.Length, digest)]));
        list.Add(JsonSerializer.Deserialize<List<JsonElement>>(one)![0].GetRawText());
        _releases = "[" + string.Join(",", list) + "]";
    }

    private async Task<(SyntheticHost Host, string Panel)> StartAsync(bool release)
    {
        string install = Path.Combine(_dir, "install");
        if (release) UpdateFixtures.WriteFolder(install, UpdateFixtures.ReleaseFiles(Here, "1.0.0"));
        return await PanelTests.StartAsync(o =>
        {
            o.GmAccounts = [Admin];
            o.Panel.Roles = new(StringComparer.Ordinal) { [Moderator] = "Moderator" };
            o.Updates.Check = true;
            o.Updates.ApiUrl = _feedUrl;
            o.Updates.Repository = Repository;
            if (!release) return;
            o.Updates.Hosting = "systemd";
            o.Updates.InstallDir = install;
        });
    }

    private static async Task<PanelBrowser> SignedInAsync(SyntheticHost host, string panel, string account)
    {
        Assert.Equal(0, await SdkLoginAsync(host, account, "pw"));
        var browser = new PanelBrowser(panel);
        Assert.Equal(HttpStatusCode.OK, (await browser.LoginAsync(account, "pw")).StatusCode);
        return browser;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> request)
    {
        var response = await request;
        string text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<string> RefusedAsync(Task<HttpResponseMessage> request)
    {
        var response = await request;
        string text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("error").GetString()!;
    }

    /// <summary>The state once the download has finished, one way or the other.</summary>
    private static async Task<JsonElement> SettledAsync(PanelBrowser admin)
    {
        for (int i = 0; i < 200; i++)
        {
            var state = await admin.GetJsonAsync("/api/admin/updates");
            if (state.GetProperty("job").GetProperty("phase").GetString() is "staged" or "failed" or "idle") return state;
            await Task.Delay(50);
        }
        throw new TimeoutException("The download did not finish.");
    }

    [Fact]
    public async Task OnlyAdminsReachTheUpdatesAndASourceBuildOnlyChecks()
    {
        Publish("v9.9.9");
        var (host, panel) = await StartAsync(release: false);
        await using var _ = host;
        using var moderator = await SignedInAsync(host, panel, Moderator);
        Assert.Equal(HttpStatusCode.Forbidden, (await moderator.Http.GetAsync("/api/admin/updates")).StatusCode);
        foreach (string path in new[] { "check", "download", "cancel", "discard", "install", "rollback" })
            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.PostAsync("/api/admin/updates/" + path, new { tag = "v9.9.9", minutes = 0 })).StatusCode);
        using var anonymous = new PanelBrowser(panel);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Http.GetAsync("/api/admin/updates")).StatusCode);

        using var admin = await SignedInAsync(host, panel, Admin);
        var state = await OkAsync(admin.PostAsync("/api/admin/updates/check", new { }));
        Assert.Equal("source", state.GetProperty("hosting").GetProperty("mode").GetString());
        Assert.False(state.GetProperty("hosting").GetProperty("canDownload").GetBoolean());
        Assert.Equal("v9.9.9", state.GetProperty("newer")[0].GetProperty("tag").GetString());
        Assert.Contains("<script>", state.GetProperty("newer")[0].GetProperty("notes").GetString()); // text; the page shows it as text
        Assert.Contains("source tree", await RefusedAsync(admin.PostAsync("/api/admin/updates/download", new { tag = "v9.9.9" })));
        Assert.Contains("source tree", await RefusedAsync(admin.PostAsync("/api/admin/updates/install", new { minutes = 5 })));
        Assert.Contains("source tree", await RefusedAsync(admin.PostAsync("/api/admin/updates/rollback", new { minutes = 5 })));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/updates/download", new { tag = "../../x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/updates/install", new { minutes = 5000 })).StatusCode);
        // Refusals change nothing, so nothing is audited.
        Assert.DoesNotContain((await admin.GetJsonAsync("/api/admin/audit")).GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("action").GetString() == "update");
    }

    [Fact]
    public async Task AReleaseIsDownloadedCheckedStagedAndInstalledThroughTheRestart()
    {
        Publish("v9.9.8", goodDigest: false);
        Publish("v9.9.9");
        var (host, panel) = await StartAsync(release: true);
        await using var _ = host;
        using var admin = await SignedInAsync(host, panel, Admin);
        var state = await OkAsync(admin.PostAsync("/api/admin/updates/check", new { }));
        Assert.Equal(("systemd", true), (state.GetProperty("hosting").GetProperty("mode").GetString(), state.GetProperty("hosting").GetProperty("canInstall").GetBoolean()));
        Assert.Equal(["v9.9.9", "v9.9.8"], state.GetProperty("newer").EnumerateArray().Select(r => r.GetProperty("tag").GetString()));

        // A package whose bytes are not the ones GitHub's digest names is refused and deleted.
        await OkAsync(admin.PostAsync("/api/admin/updates/download", new { tag = "v9.9.8" }));
        var failed = await SettledAsync(admin);
        Assert.Equal("failed", failed.GetProperty("job").GetProperty("phase").GetString());
        Assert.Contains("SHA-256", failed.GetProperty("job").GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("staged").ValueKind);

        await OkAsync(admin.PostAsync("/api/admin/updates/download", new { tag = "v9.9.9" }));
        var staged = await SettledAsync(admin);
        Assert.Equal("staged", staged.GetProperty("job").GetProperty("phase").GetString());
        Assert.Equal("9.9.9+feedf00", staged.GetProperty("staged").GetProperty("version").GetString());

        // Planned like a restart; nothing else can start meanwhile.
        await OkAsync(admin.PostAsync("/api/admin/updates/install", new { minutes = 30, reason = "Update day." }));
        var planned = (await admin.GetJsonAsync("/api/admin/server")).GetProperty("scheduled");
        Assert.Equal(("update", "v9.9.9"), (planned.GetProperty("kind").GetString(), planned.GetProperty("version").GetString()));
        Assert.Equal("planned", (await admin.GetJsonAsync("/api/admin/updates")).GetProperty("job").GetProperty("phase").GetString());
        Assert.Contains("planned", await RefusedAsync(admin.PostAsync("/api/admin/updates/discard", new { })));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/admin/updates/install", new { minutes = 5 })).StatusCode);
        await OkAsync(admin.PostAsync("/api/admin/server/cancel", new { }));
        Assert.Equal("staged", (await admin.GetJsonAsync("/api/admin/updates")).GetProperty("job").GetProperty("phase").GetString());

        // The last step of the restart, without the stop: backup, swap, the previous version kept.
        var manager = host.Service<UpdateManager>();
        var record = UpdateInstaller.ReadJson<StagedRecord>(new UpdatePaths(host.DataDir).Staged)!;
        Assert.Null(await manager.InstallStepAsync(Admin, record));
        string install = Path.Combine(_dir, "install");
        Assert.Equal("9.9.9+feedf00", BundleVersion.Read(Path.Combine(install, Here.ServerFile)));
        var after = await admin.GetJsonAsync("/api/admin/updates");
        Assert.Equal("installing", after.GetProperty("job").GetProperty("phase").GetString());
        var previous = after.GetProperty("previous");
        Assert.StartsWith("ro3emu-before-9.9.9-", previous.GetProperty("backup").GetString());
        Assert.Equal("install", after.GetProperty("history")[0].GetProperty("kind").GetString());
        Assert.True(File.Exists(new UpdatePaths(host.DataDir).Pending));

        var audit = (await admin.GetJsonAsync("/api/admin/audit")).GetProperty("entries").EnumerateArray()
            .Where(e => e.GetProperty("action").GetString() == "update").Select(e => e.GetProperty("detail").GetString()!).ToList();
        Assert.Contains(audit, d => d.StartsWith("download of v9.9.8 failed", StringComparison.Ordinal));
        Assert.Contains(audit, d => d.StartsWith("install of v9.9.9 planned", StringComparison.Ordinal));
        Assert.Contains(audit, d => d.StartsWith("installing v9.9.9", StringComparison.Ordinal));
        Assert.All((await admin.GetJsonAsync("/api/admin/audit")).GetProperty("entries").EnumerateArray()
            .Where(e => e.GetProperty("action").GetString() == "update"), e => Assert.Equal("server", e.GetProperty("subject").GetProperty("kind").GetString()));
    }
}
