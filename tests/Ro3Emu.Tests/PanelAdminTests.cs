using System.Net;
using System.Text.Json;
using Ro3Emu.Bot;
using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using static Ro3Emu.Tests.PanelTests;

namespace Ro3Emu.Tests;

/// <summary>
/// The panel's staff roles, account security (login history, sessions, two-factor authentication), report inbox,
/// character actions and server control against a running server on the invented fixture. Accounts are invented.
/// </summary>
public sealed class PanelAdminTests : IAsyncLifetime
{
    private const string Admin = "padmin-admin", Moderator = "padmin-mod";
    /// <summary>An item of the fixture's bag (100 is its base EXP item, which a grant turns into EXP).</summary>
    private const int Bagged = 1000000;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private SyntheticHost _host = null!;
    private string _panel = "";

    public async Task InitializeAsync() => (_host, _panel) = await StartAsync(o =>
    {
        o.GmAccounts = [Admin];
        o.Panel.Roles = new(StringComparer.Ordinal) { [Moderator] = "Moderator" };
    });

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private async Task<PanelBrowser> SignedInAsync(string account, string password = "pw")
    {
        Assert.Equal(0, await SdkLoginAsync(_host, account, password));
        var browser = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await browser.LoginAsync(account, password)).StatusCode);
        return browser;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> request)
    {
        var response = await request;
        string text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<List<string>> AuditActionsAsync(PanelBrowser admin) =>
        [.. (await admin.GetJsonAsync("/api/admin/audit")).GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("action").GetString()!)];

    // ---------------------------------------------------------------- TOTP

    [Fact]
    public void TotpMatchesTheRfc6238TestVectors()
    {
        // RFC 6238 appendix B, SHA-1 key "12345678901234567890": the last 6 of its 8-digit codes.
        byte[] key = "12345678901234567890"u8.ToArray();
        foreach (var (seconds, code) in new[] { (59L, "287082"), (1111111109L, "081804"), (1111111111L, "050471"),
            (1234567890L, "005924"), (2000000000L, "279037") })
            Assert.Equal(code, Totp.Code(key, Totp.TimeStep(DateTimeOffset.FromUnixTimeSeconds(seconds))));
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.ToBase32(key));
        Assert.Equal(key, Totp.FromBase32("gezd gnbv gy3t qojq gezd gnbv gy3t qojq"));

        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);
        long step = Totp.TimeStep(now);
        Assert.Equal(step, Totp.Match(key, "050 471", now, 0));
        Assert.Equal(step - 1, Totp.Match(key, Totp.Code(key, step - 1), now, 0));
        Assert.Null(Totp.Match(key, "050471", now, step));
        Assert.Null(Totp.Match(key, Totp.Code(key, step + 2), now, 0));
        Assert.Null(Totp.Match(key, "05047x", now, 0));
    }

    // ---------------------------------------------------------------- roles

    [Fact]
    public async Task EveryRouteChecksItsRoleAndAnAdminGivesRoles()
    {
        await SdkLoginAsync(_host, "padmin-helper", "pw");
        using var moderator = await SignedInAsync(Moderator);
        var session = await moderator.SessionAsync();
        Assert.Equal("moderator", session.GetProperty("role").GetString());
        Assert.True(session.GetProperty("admin").GetBoolean());
        foreach (string path in new[] { "/api/admin/dashboard", "/api/admin/accounts", "/api/admin/audit", "/api/admin/reports" })
            Assert.Equal(HttpStatusCode.OK, (await moderator.Http.GetAsync(path)).StatusCode);
        foreach (string path in new[] { "/api/admin/news", "/api/admin/economy", "/api/admin/roles", "/api/admin/server" })
            Assert.Equal(HttpStatusCode.Forbidden, (await moderator.Http.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await moderator.PostAsync("/api/admin/broadcasts", new { text = "hi" })).StatusCode);

        using var admin = await SignedInAsync(Admin);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/admin/roles", new { account = Moderator, role = "admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/admin/roles", new { account = "padmin-ghost", role = "gm" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/roles", new { account = "padmin-helper", role = "king" })).StatusCode);
        await OkAsync(admin.PostAsync("/api/admin/roles", new { account = "PADMIN-HELPER", role = "gm" }));

        using var gm = await SignedInAsync("padmin-helper");
        Assert.Equal("gm", (await gm.SessionAsync()).GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.OK, (await gm.Http.GetAsync("/api/admin/news")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await gm.Http.GetAsync("/api/admin/roles")).StatusCode);

        var staff = (await admin.GetJsonAsync("/api/admin/roles")).GetProperty("staff").EnumerateArray()
            .ToDictionary(s => s.GetProperty("account").GetString()!, s => (s.GetProperty("role").GetString(), s.GetProperty("source").GetString()));
        Assert.Equal(("admin", "gm-accounts"), staff[Admin]);
        Assert.Equal(("moderator", "config"), staff[Moderator]);
        Assert.Equal(("gm", "panel"), staff["padmin-helper"]);

        await OkAsync(admin.PostAsync("/api/admin/roles", new { account = "padmin-helper", role = "none" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await gm.Http.GetAsync("/api/admin/news")).StatusCode);
        Assert.Equal(["role", "role"], await AuditActionsAsync(admin));
    }

    // ---------------------------------------------------------------- account security

    private static string CurrentCode(string base32) => Totp.Code(Totp.FromBase32(base32)!, Totp.TimeStep(DateTimeOffset.UtcNow));

    /// <summary>Turns 2FA on: the secret, the code that confirmed it (spent) and the recovery codes.</summary>
    private static async Task<(string Secret, string Spent, List<string> Recovery)> EnableTwoFactorAsync(PanelBrowser browser)
    {
        var begun = await OkAsync(browser.PostAsync("/api/account/2fa/begin", new { }));
        string secret = begun.GetProperty("secret").GetString()!;
        // The QR code of the otpauth URI: its side with the quiet zone, and the dark modules' path.
        Assert.True(begun.GetProperty("qr").GetProperty("size").GetInt32() >= 21 + 8);
        Assert.StartsWith("M", begun.GetProperty("qr").GetProperty("path").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/api/account/2fa/enable", new { code = "abcdef" })).StatusCode);
        string code = CurrentCode(secret);
        var enabled = await OkAsync(browser.PostAsync("/api/account/2fa/enable", new { code }));
        return (secret, code, [.. enabled.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!)]);
    }

    [Fact]
    public async Task TwoFactorGuardsTheLoginAndEachCodeWorksOnce()
    {
        using var player = await SignedInAsync("padmin-twofa");
        var (_, spent, recovery) = await EnableTwoFactorAsync(player);
        Assert.Equal(10, recovery.Count);
        Assert.Equal(HttpStatusCode.Conflict, (await player.PostAsync("/api/account/2fa/begin", new { })).StatusCode);

        using var browser = new PanelBrowser(_panel);
        var first = await OkAsync(browser.LoginAsync("padmin-twofa", "pw"));
        Assert.True(first.GetProperty("twoFactor").GetBoolean());
        Assert.False(first.TryGetProperty("account", out _));
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.Http.GetAsync("/api/account")).StatusCode);
        string challenge = first.GetProperty("challenge").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/api/login/2fa", new { challenge, code = "12345" })).StatusCode);
        // The code that turned 2FA on is spent: a replay is refused.
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/api/login/2fa", new { challenge, code = spent })).StatusCode);
        var signed = await OkAsync(browser.PostAsync("/api/login/2fa", new { challenge, code = recovery[0] }));
        Assert.Equal("padmin-twofa", signed.GetProperty("account").GetString());
        await browser.SessionAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/api/login/2fa", new { challenge, code = recovery[1] })).StatusCode);

        // A recovery code is good once; the others still work.
        using var again = new PanelBrowser(_panel);
        string second = (await OkAsync(again.LoginAsync("padmin-twofa", "pw"))).GetProperty("challenge").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await again.PostAsync("/api/login/2fa", new { challenge = second, code = recovery[0] })).StatusCode);
        await OkAsync(again.PostAsync("/api/login/2fa", new { challenge = second, code = recovery[1].ToLowerInvariant().Replace("-", "") }));
        Assert.Equal(8, (await again.GetJsonAsync("/api/account/security")).GetProperty("twoFactor").GetProperty("recoveryLeft").GetInt32());

        using var admin = await SignedInAsync(Admin);
        await OkAsync(admin.PostAsync("/api/admin/accounts/padmin-twofa/2fa-reset", new { }));
        Assert.Equal(HttpStatusCode.Unauthorized, (await again.Http.GetAsync("/api/account")).StatusCode);
        Assert.Contains("2fa-reset", await AuditActionsAsync(admin));
        using var plain = new PanelBrowser(_panel);
        Assert.Equal("padmin-twofa", (await OkAsync(plain.LoginAsync("padmin-twofa", "pw"))).GetProperty("account").GetString());
    }

    [Fact]
    public async Task WrongCodesRunOutForTheAccount()
    {
        using var player = await SignedInAsync("padmin-guess");
        await EnableTwoFactorAsync(player);
        // Not a current code, and not one of this account's recovery codes.
        string wrong = "ZZZZZ-ZZZZZ";
        var answers = new List<HttpStatusCode>();
        for (int login = 0; login < 3; login++)
        {
            using var browser = new PanelBrowser(_panel);
            string challenge = (await OkAsync(browser.LoginAsync("padmin-guess", "pw"))).GetProperty("challenge").GetString()!;
            for (int i = 0; i < 5; i++) answers.Add((await browser.PostAsync("/api/login/2fa", new { challenge, code = wrong })).StatusCode);
        }
        Assert.Contains(HttpStatusCode.TooManyRequests, answers);
        Assert.True(answers.Count(a => a == HttpStatusCode.Unauthorized) <= 10);
    }

    [Fact]
    public async Task LoginsAreRecordedAndSessionsCanBeEnded()
    {
        using var first = await SignedInAsync("padmin-sessions");
        using var second = new PanelBrowser(_panel);
        await OkAsync(second.LoginAsync("padmin-sessions", "pw"));
        var security = await first.GetJsonAsync("/api/account/security");
        Assert.Equal(2, security.GetProperty("sessions").GetArrayLength());
        var sources = security.GetProperty("logins").EnumerateArray().Select(l => l.GetProperty("source").GetString()).ToList();
        Assert.Equal(["panel", "panel", "game"], sources);
        Assert.All(security.GetProperty("logins").EnumerateArray(), l => Assert.Equal(JsonValueKind.Null, l.GetProperty("address").ValueKind));

        Assert.Equal(1, (await OkAsync(first.PostAsync("/api/account/sessions/end-others", new { }))).GetProperty("ended").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.Http.GetAsync("/api/account")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.Http.GetAsync("/api/account")).StatusCode);

        using var moderator = await SignedInAsync(Moderator);
        Assert.Equal(3, (await moderator.GetJsonAsync("/api/admin/accounts/padmin-sessions/logins")).GetProperty("logins").GetArrayLength());

        // An Admin's "log out of the panel" is audited, its target the account.
        using var admin = await SignedInAsync(Admin);
        Assert.Equal(1, (await OkAsync(admin.PostAsync("/api/admin/accounts/padmin-sessions/logout", new { }))).GetProperty("ended").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.Http.GetAsync("/api/account")).StatusCode);
        var entry = (await admin.GetJsonAsync("/api/admin/audit")).GetProperty("entries")[0];
        Assert.Equal(("panel-logout", "1 panel session ended"), (entry.GetProperty("action").GetString(), entry.GetProperty("detail").GetString()));
        Assert.Equal(("account", "padmin-sessions"), (entry.GetProperty("subject").GetProperty("kind").GetString(), entry.GetProperty("subject").GetProperty("key").GetString()));
    }

    [Fact]
    public async Task AdminsMustTurnOnTwoFactorWhenItIsRequired()
    {
        await using var host = await SyntheticHost.StartAsync(o =>
        {
            o.GmAccounts = [Admin];
            o.Panel.Enabled = true;
            o.Panel.Port = TestPorts.FreePort();
            o.Panel.RequireAdmin2FA = true;
        });
        string panel = $"http://127.0.0.1:{host.Service<Microsoft.Extensions.Options.IOptions<Ro3Emu.Server.ServerOptions>>().Value.Panel.Port}";
        Assert.Equal(0, await SdkLoginAsync(host, Admin, "pw"));
        using var admin = new PanelBrowser(panel);
        var session = await OkAsync(admin.LoginAsync(Admin, "pw"));
        Assert.False(session.GetProperty("admin").GetBoolean());
        Assert.True(session.GetProperty("twoFactorRequired").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Http.GetAsync("/api/admin/dashboard")).StatusCode);
        await EnableTwoFactorAsync(admin);
        Assert.Equal("admin", (await admin.SessionAsync()).GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.Http.GetAsync("/api/admin/dashboard")).StatusCode);
    }

    // ---------------------------------------------------------------- report inbox

    [Fact]
    public async Task ReportsGetAStatusAnAssigneeAndNotes()
    {
        var store = _host.Service<FeedbackService>().Store;
        store.Append(new FeedbackReport { Time = DateTimeOffset.UtcNow, Source = "chat", Account = "padmin-reporter", RoleName = "Reporter", Level = 12, Map = "Town", Text = "The door is stuck", Context = ["a log line"] });
        long id = store.Append(new FeedbackReport { Time = DateTimeOffset.UtcNow, Source = "button", Account = "padmin-reporter", RoleName = "Reporter", Text = "Second" });
        using var moderator = await SignedInAsync(Moderator);
        var page = await moderator.GetJsonAsync("/api/admin/reports");
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.Equal(id, page.GetProperty("reports")[0].GetProperty("id").GetInt64());
        Assert.Equal("new", page.GetProperty("reports")[0].GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await moderator.PostAsync($"/api/admin/reports/{id}", new { status = "lost" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await moderator.PostAsync($"/api/admin/reports/{id}", new { assignee = "padmin-nobody" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await moderator.PostAsync("/api/admin/reports/999", new { status = "done" })).StatusCode);
        await OkAsync(moderator.PostAsync($"/api/admin/reports/{id}", new { status = "in-progress", assignee = Moderator.ToUpperInvariant(), note = "Looking at it" }));
        await OkAsync(moderator.PostAsync($"/api/admin/reports/{id}", new { status = "done" }));

        var report = await moderator.GetJsonAsync($"/api/admin/reports/{id}");
        Assert.Equal("done", report.GetProperty("status").GetString());
        Assert.Equal(Moderator, report.GetProperty("assignee").GetString());
        Assert.Equal("Looking at it", Assert.Single(report.GetProperty("notes").EnumerateArray()).GetProperty("text").GetString());
        Assert.Equal("a log line", (await moderator.GetJsonAsync($"/api/admin/reports/{id - 1}")).GetProperty("context")[0].GetString());
        var done = await moderator.GetJsonAsync("/api/admin/reports?status=done");
        Assert.Equal(id, Assert.Single(done.GetProperty("reports").EnumerateArray()).GetProperty("id").GetInt64());
        Assert.Equal(1, done.GetProperty("counts").GetProperty("new").GetInt32());
        Assert.Equal(["report", "report"], await AuditActionsAsync(moderator));
    }

    // ---------------------------------------------------------------- character actions

    private long RoleId(string account) => Assert.Single(_host.Service<RoleStore>().ForAccount(account)).Id;

    [Fact]
    public async Task GmsGiveItemsAndKickCharactersInTheGame()
    {
        await using var bot = await _host.InWorldAsync("Holder");
        long id = RoleId("holder");
        using var moderator = await SignedInAsync(Moderator);
        Assert.Equal(HttpStatusCode.Forbidden, (await moderator.PostAsync($"/api/admin/characters/{id}/kick", new { })).StatusCode);
        using var admin = await SignedInAsync(Admin);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/admin/characters/{id}/items", new { item = 987654, count = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/admin/characters/{id}/items", new { item = Bagged, count = 0 })).StatusCode);
        async Task<int> HeldAsync() => (await admin.GetJsonAsync($"/api/characters/{id}")).GetProperty("sheet").GetProperty("bags").EnumerateArray()
            .SelectMany(b => b.GetProperty("items").EnumerateArray()).Where(i => i.GetProperty("itemId").GetInt64() == Bagged)
            .Sum(i => i.GetProperty("count").GetInt32());
        int before = await HeldAsync();
        await OkAsync(admin.PostAsync($"/api/admin/characters/{id}/items", new { item = Bagged, count = 3 }));
        Assert.Equal(before + 3, await HeldAsync());
        // Taking more than the character holds takes nothing and says how much it holds.
        var tooMany = await admin.PostAsync($"/api/admin/characters/{id}/items", new { item = Bagged, count = -(before + 4) });
        Assert.Equal(HttpStatusCode.Conflict, tooMany.StatusCode);
        Assert.Contains("holds only " + (before + 3), await tooMany.Content.ReadAsStringAsync());
        Assert.Equal(before + 3, await HeldAsync());
        var taken = await OkAsync(admin.PostAsync($"/api/admin/characters/{id}/items", new { item = Bagged, count = -2 }));
        Assert.StartsWith("Removed 2 ", taken.GetProperty("message").GetString());
        Assert.Equal(before + 1, await HeldAsync());

        await OkAsync(admin.PostAsync($"/api/admin/characters/{id}/kick", new { reason = "test" }));
        await bot.Inbox.WaitAsync(e => e.Kind == BotEventKind.Disconnect, Wait);
        Assert.Equal(["char-kick", "char-item", "char-item"], await AuditActionsAsync(admin));
        // A character action's target is the character (its id, for the link to its page) under its name.
        var subject = (await admin.GetJsonAsync("/api/admin/audit")).GetProperty("entries")[0].GetProperty("subject");
        Assert.Equal(("character", id.ToString(System.Globalization.CultureInfo.InvariantCulture), "Holder"),
            (subject.GetProperty("kind").GetString(), subject.GetProperty("key").GetString(), subject.GetProperty("label").GetString()));
    }

    /// <summary>Retries an offline action while the logout of the character still holds it.</summary>
    private static async Task<JsonElement> OfflineAsync(PanelBrowser admin, string path, object body)
    {
        var until = DateTime.UtcNow + Wait;
        while (true)
        {
            var response = await admin.PostAsync(path, body);
            string text = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.OK) return JsonDocument.Parse(text).RootElement;
            Assert.True(response.StatusCode == HttpStatusCode.Conflict && text.Contains("in the game") && DateTime.UtcNow < until, text);
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task OfflineCharactersAreRenamedTeleportedAndRestored()
    {
        long id;
        await using (var bot = await _host.InWorldAsync("Wanderer")) id = RoleId("wanderer");
        await SdkLoginAsync(_host, "padmin-other", "pw");
        using var admin = await SignedInAsync(Admin);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/admin/characters/{id}/rename", new { name = "X" })).StatusCode);
        await OfflineAsync(admin, $"/api/admin/characters/{id}/rename", new { name = "Strider" });
        Assert.Equal("Strider", _host.Service<RoleStore>().Get(id)!.Name);

        await OfflineAsync(admin, $"/api/admin/characters/{id}/teleport", new { map = 10111, x = 12.5f, z = -4f });
        var moved = _host.Service<RoleStore>().Get(id)!;
        Assert.Equal((10111L, 12.5f, -4f), (moved.SceneEntry, moved.X, moved.Z));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/admin/characters/{id}/teleport", new { map = 99999 })).StatusCode);

        using (var client = new HttpClient())
        using (var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["identity"] = "wanderer", ["credential"] = "pw", ["role"] = "Strider" }))
            Assert.Contains("deleted", await (await client.PostAsync(_host.Http + "/ro3emu/account/delete-role", form)).Content.ReadAsStringAsync());
        var deleted = Assert.Single((await admin.GetJsonAsync("/api/admin/accounts/wanderer/deleted")).GetProperty("characters").EnumerateArray());
        Assert.Equal("Strider", deleted.GetProperty("name").GetString());
        Assert.True(deleted.GetProperty("nameFree").GetBoolean());
        await OfflineAsync(admin, $"/api/admin/characters/{id}/restore", new { });
        Assert.Equal("Strider", _host.Service<RoleStore>().Get(id)!.Name);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/admin/characters/{id}/restore", new { })).StatusCode);

        // The restored character plays again.
        await using var back = await _host.InWorldAsync("Wanderer", create: false);
        Assert.Equal(["char-restore", "char-teleport", "char-rename"], await AuditActionsAsync(admin));
        Assert.All((await admin.GetJsonAsync("/api/admin/audit")).GetProperty("entries").EnumerateArray(), e =>
            Assert.Equal(("character", id.ToString(System.Globalization.CultureInfo.InvariantCulture), "Strider"), (e.GetProperty("subject").GetProperty("kind").GetString(),
                e.GetProperty("subject").GetProperty("key").GetString(), e.GetProperty("subject").GetProperty("label").GetString())));
    }

    [Fact]
    public void AuditTargetsLinkToWhatTheRowIsAbout()
    {
        Assert.Equal(new PanelAuditTarget("character", "100001", "QaBotOne"), PanelAuditTarget.Of("char-kick", "QaBotOne (100001)", "kicked"));
        // Rows written before the "Name (id)" target of a rename or restore: the name comes from the detail.
        Assert.Equal(new PanelAuditTarget("character", "100001", "NewName"), PanelAuditTarget.Of("char-rename", "role:100001", "Old => NewName"));
        Assert.Equal(new PanelAuditTarget("character", "100001", "Back"), PanelAuditTarget.Of("char-restore", "role:100001", "Back of qa1"));
        Assert.Equal(new PanelAuditTarget("character", "7", "Character 7"), PanelAuditTarget.Of("char-rename", "role:7", ""));
        Assert.Equal("account", PanelAuditTarget.Of("ban", "qa1", "").Kind);
        Assert.Equal(new PanelAuditTarget("report", "12", "Report #12"), PanelAuditTarget.Of("report", "report:12", ""));
        Assert.Equal(new PanelAuditTarget("character-name", "Alice", "Alice"), PanelAuditTarget.Of("mail", "character:Alice (online)", ""));
        Assert.Equal(new PanelAuditTarget("account", "qa1", "qa1"), PanelAuditTarget.Of("mail", "account:qa1", ""));
        Assert.Equal("none", PanelAuditTarget.Of("mail", "everyone from level 10", "").Kind);
        Assert.Equal("server", PanelAuditTarget.Of("schedule", "server", "").Kind);
        Assert.Equal(new PanelAuditTarget("character-name", "Alice", "Alice"), PanelAuditTarget.Of("gm", "Alice", "!where"));
    }

    // ---------------------------------------------------------------- server control

    [Fact]
    public async Task MaintenanceLetsOnlyStaffIntoTheGame()
    {
        using var admin = await SignedInAsync(Admin);
        await OkAsync(admin.PostAsync("/api/admin/server/maintenance", new { on = true, note = "patch" }));
        Assert.True((await admin.GetJsonAsync("/api/admin/server")).GetProperty("maintenance").GetProperty("on").GetBoolean());

        await using var player = _host.NewBot();
        await player.LoginAsync("padmin-player", "pw");
        var refused = await Assert.ThrowsAnyAsync<Exception>(player.EnterGateAsync);
        Assert.Contains("ClusterShutdown", refused.Message);
        await using (var staff = await _host.InWorldAsync(Moderator)) { }

        await OkAsync(admin.PostAsync("/api/admin/server/maintenance", new { on = false, note = "" }));
        await using var later = await _host.InWorldAsync("padmin-player");
        Assert.Equal(["maintenance", "maintenance"], await AuditActionsAsync(admin));
    }

    [Fact]
    public async Task BackupsAreMadeListedDownloadedAndARestoreIsPlanned()
    {
        using var admin = await SignedInAsync(Admin);
        string name = (await OkAsync(admin.PostAsync("/api/admin/backups", new { }))).GetProperty("name").GetString()!;
        var state = await admin.GetJsonAsync("/api/admin/server");
        Assert.Contains(name, state.GetProperty("backups").EnumerateArray().Select(b => b.GetProperty("name").GetString()));

        var download = await admin.Http.GetAsync("/api/admin/backups/" + name);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString((await download.Content.ReadAsByteArrayAsync())[..16]));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Http.GetAsync("/api/admin/backups/..%2Fro3emu.db")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Http.GetAsync("/api/admin/backups/ro3emu-nothing.db")).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/admin/server/schedule",
            new { kind = "restore", minutes = 30, backup = "ro3emu-missing.db" })).StatusCode);
        await OkAsync(admin.PostAsync("/api/admin/server/schedule", new { kind = "restore", minutes = 30, reason = "rollback", backup = name }));
        var planned = (await admin.GetJsonAsync("/api/admin/server")).GetProperty("scheduled");
        Assert.Equal(("restore", name), (planned.GetProperty("kind").GetString(), planned.GetProperty("backup").GetString()));
        await OkAsync(admin.PostAsync("/api/admin/server/cancel", new { }));
        Assert.Equal(JsonValueKind.Null, (await admin.GetJsonAsync("/api/admin/server")).GetProperty("scheduled").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/admin/server/cancel", new { })).StatusCode);
        Assert.Equal(["schedule", "schedule", "backup", "backup"], await AuditActionsAsync(admin));
    }

    [Fact]
    public async Task ACountdownWarnsThePlayersInTheGame()
    {
        await using var bot = await _host.InWorldAsync("Listener");
        using var admin = await SignedInAsync(Admin);
        await OkAsync(admin.PostAsync("/api/admin/server/schedule", new { kind = "restart", minutes = 10, reason = "Patch day." }));
        await bot.Inbox.WaitAsync(e => e.IsGame(GmCommands.BroadcastNotify)
            && System.Text.Encoding.UTF8.GetString(e.Body).Contains("The server restarts in 10 minutes. Patch day."), Wait);
        await OkAsync(admin.PostAsync("/api/admin/server/cancel", new { }));
    }

    [Fact]
    public void ARequestedRestoreRunsOnceAtTheNextStart()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ro3emu-restore-request-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            DatabaseRestore.Request(dir, Path.Combine(dir, "backups", "ro3emu-gone.db"));
            using var output = new StringWriter();
            DatabaseRestore.RunRequested(new Ro3Emu.Server.ServerOptions { DataDir = dir }, output);
            Assert.False(File.Exists(Path.Combine(dir, DatabaseRestore.RequestName)));
            Assert.Contains("not restored ro3emu-gone.db", File.ReadAllText(Path.Combine(dir, DatabaseRestore.ResultName)));
            Assert.False(File.Exists(Path.Combine(dir, "ro3emu.db")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
