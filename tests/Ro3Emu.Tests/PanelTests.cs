using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Http;

namespace Ro3Emu.Tests;

/// <summary>
/// The web panel (Ro3Emu.Panel, docs/guide/panel.md) on its own port of a running server: logins with the game's
/// accounts, CSRF, the admin area, bans that kick, sign-up and password changes, and the pages. Accounts are invented.
/// </summary>
public sealed class PanelTests : IAsyncLifetime
{
    private const string Admin = "panel-admin";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private SyntheticHost _host = null!;
    private string _panel = "";

    public async Task InitializeAsync() => (_host, _panel) = await StartAsync(o => o.GmAccounts = [Admin]);

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    internal static async Task<(SyntheticHost Host, string Panel)> StartAsync(Action<ServerOptions> configure)
    {
        int port = TestPorts.FreePort();
        var host = await SyntheticHost.StartAsync(o =>
        {
            o.Panel.Enabled = true;
            o.Panel.Port = port;
            configure(o);
        });
        return (host, $"http://127.0.0.1:{port}");
    }

    [Fact]
    public async Task PagesRenderWithTheirSecurityHeaders()
    {
        using var browser = new PanelBrowser(_panel);
        var home = await browser.Http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Equal("text/html", home.Content.Headers.ContentType?.MediaType);
        Assert.Contains("/app.js", await home.Content.ReadAsStringAsync());
        Assert.StartsWith("default-src 'none'; script-src 'self'", home.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", home.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", home.Headers.GetValues("X-Content-Type-Options").Single());
        foreach (string page in new[] { "/online", "/rankings", "/news", "/login", "/register", "/account", "/admin/accounts/x" })
            Assert.Equal(HttpStatusCode.OK, (await browser.Http.GetAsync(page)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.Http.GetAsync("/no-such-page")).StatusCode);
        Assert.Equal("text/css", (await browser.Http.GetAsync("/app.css")).Content.Headers.ContentType?.MediaType);
        Assert.Equal("font/woff2", (await browser.Http.GetAsync("/fonts/young-serif-400-latin.woff2")).Content.Headers.ContentType?.MediaType);

        var status = await browser.GetJsonAsync("/api/status");
        Assert.Equal("RO3Emu", status.GetProperty("name").GetString());
        Assert.Equal(0, status.GetProperty("players").GetInt32());
        Assert.Equal(0, status.GetProperty("characters").GetInt32());
        Assert.Equal(JsonValueKind.Number, status.GetProperty("accounts").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.Http.GetAsync("/api/nothing")).StatusCode);
    }

    [Fact]
    public async Task ThePanelPortServesOnlyThePanel()
    {
        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(_panel + "/ro3emu/status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(_host.Http + "/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(_host.Http + "/ro3emu/status")).StatusCode);
    }

    [Fact]
    public async Task AGameAccountLogsInAndAWrongPasswordIsRefused()
    {
        Assert.Equal(0, await SdkLoginAsync(_host, "panel-player", "pw-game"));
        using var browser = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.LoginAsync("panel-player", "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.LoginAsync("panel-nobody", "pw-game")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.Http.GetAsync("/api/account")).StatusCode);

        // Any letter case finds the account; the session carries its stored name.
        var login = await browser.LoginAsync("Panel-Player", "pw-game");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("panel-player", session.GetProperty("account").GetString());
        Assert.False(session.GetProperty("admin").GetBoolean());
        string cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith(PanelCookie, StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("panel-player", (await browser.GetJsonAsync("/api/account")).GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/api/logout", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.Http.GetAsync("/api/account")).StatusCode);
    }

    private const string PanelCookie = "ro3p_session=";

    [Fact]
    public async Task APostWithoutTheSessionsCsrfTokenIsRefused()
    {
        Assert.Equal(0, await SdkLoginAsync(_host, "panel-csrf", "pw-game"));
        using var browser = new PanelBrowser(_panel);
        await browser.SessionAsync();
        string visitorToken = browser.Csrf;
        browser.Csrf = "";
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.LoginAsync("panel-csrf", "pw-game")).StatusCode);
        browser.Csrf = "not-the-token";
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.LoginAsync("panel-csrf", "pw-game")).StatusCode);
        browser.Csrf = visitorToken;
        Assert.Equal(HttpStatusCode.OK, (await browser.LoginAsync("panel-csrf", "pw-game")).StatusCode);

        // The visitor's token does not carry over to the session, and another site's page cannot post.
        browser.Csrf = visitorToken;
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsync("/api/account/password", new { current = "pw-game", next = "changed-1" })).StatusCode);
        await browser.SessionAsync();
        using var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/logout") { Content = JsonContent.Create(new { }) };
        foreign.Headers.Add("X-CSRF-Token", browser.Csrf);
        foreign.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.Http.SendAsync(foreign)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.Http.GetAsync("/api/account")).StatusCode);
    }

    [Fact]
    public async Task TheAdminAreaIsOnlyForListedGmAccounts()
    {
        Assert.Equal(0, await SdkLoginAsync(_host, "panel-plain", "pw-game"));
        Assert.Equal(0, await SdkLoginAsync(_host, Admin, "pw-admin"));
        using var anonymous = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Http.GetAsync("/api/admin/dashboard")).StatusCode);
        using var player = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await player.LoginAsync("panel-plain", "pw-game")).StatusCode);
        foreach (string path in new[] { "/api/admin/dashboard", "/api/admin/accounts", "/api/admin/accounts/panel-plain", "/api/admin/audit" })
            Assert.Equal(HttpStatusCode.Forbidden, (await player.Http.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await player.PostAsync("/api/admin/accounts/" + Admin + "/ban", new { reason = "no", hours = 1 })).StatusCode);

        using var admin = new PanelBrowser(_panel);
        Assert.True((await (await admin.LoginAsync(Admin, "pw-admin")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("admin").GetBoolean());
        var dashboard = await admin.GetJsonAsync("/api/admin/dashboard");
        Assert.True(dashboard.GetProperty("accounts").GetInt32() >= 2);
        Assert.Equal("Accepting", dashboard.GetProperty("phase").GetString());
        var found = await admin.GetJsonAsync("/api/admin/accounts?q=PLAIN");
        Assert.Equal("panel-plain", Assert.Single(found.GetProperty("accounts").EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task WithAnEmptyGmListNobodyIsAdminAndLoginsAreThrottled()
    {
        var (host, panel) = await StartAsync(o =>
        {
            o.GmOpenWhenEmpty = true; // GM for everyone in game; the panel still has no admin
            o.LoginLimitIncludesLoopback = true;
            o.LoginAttemptsPerMinute = 3;
        });
        await using var _ = host;
        Assert.Equal(0, await SdkLoginAsync(host, "panel-anyone", "pw-game"));
        using var browser = new PanelBrowser(panel);
        var login = await browser.LoginAsync("panel-anyone", "pw-game");
        Assert.False((await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("admin").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.Http.GetAsync("/api/admin/dashboard")).StatusCode);

        // The server's credential budget (CredentialLimiter), shared with the game's login: the third check of the
        // minute is the last, then 429 before any hashing.
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.LoginAsync("panel-anyone", "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await browser.LoginAsync("panel-anyone", "pw-game")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await browser.RegisterAsync("panel-another", "secret-1")).StatusCode);
    }

    [Fact]
    public async Task ABanKicksTheOnlinePlayerBlocksEveryLoginAndIsAudited()
    {
        Assert.Equal(0, await SdkLoginAsync(_host, Admin, "pw-admin"));
        await using var bot = await _host.InWorldAsync("Banned");
        using var playerPanel = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await playerPanel.LoginAsync("banned", "pw")).StatusCode);
        using var admin = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await admin.LoginAsync(Admin, "pw-admin")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/accounts/banned/ban", new { reason = "", hours = 24 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/accounts/" + Admin + "/ban", new { reason = "self", hours = 24 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/admin/accounts/panel-ghost/ban", new { reason = "x", hours = 24 })).StatusCode);

        var ban = await admin.PostAsync("/api/admin/accounts/banned/ban", new { reason = "botting", hours = 24 });
        Assert.Equal(HttpStatusCode.OK, ban.StatusCode);
        Assert.Equal(1, (await ban.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("kicked").GetInt32());
        await bot.Inbox.WaitAsync(e => e.Kind == BotEventKind.Disconnect, Wait);
        Assert.Equal(401, await SdkLoginAsync(_host, "banned", "pw"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await playerPanel.Http.GetAsync("/api/account")).StatusCode);
        await playerPanel.SessionAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await playerPanel.LoginAsync("banned", "pw")).StatusCode);
        Assert.True(await _host.Service<AccountStore>().IsBannedAsync("banned"));

        var detail = await admin.GetJsonAsync("/api/admin/accounts/banned");
        Assert.Equal("botting", detail.GetProperty("account").GetProperty("ban").GetProperty("reason").GetString());
        Assert.Equal(Admin, detail.GetProperty("account").GetProperty("ban").GetProperty("by").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/admin/accounts/banned/unban", new { reason = "appeal" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/admin/accounts/banned/unban", new { reason = "" })).StatusCode);
        Assert.Equal(0, await SdkLoginAsync(_host, "banned", "pw"));

        var audit = (await admin.GetJsonAsync("/api/admin/audit")).GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(["unban", "ban"], audit.Select(e => e.GetProperty("action").GetString()));
        Assert.All(audit, e => Assert.Equal(Admin, e.GetProperty("admin").GetString()));
        Assert.All(audit, e => Assert.Equal("banned", e.GetProperty("target").GetString()));
        Assert.EndsWith("botting", audit[1].GetProperty("detail").GetString());
    }

    [Fact]
    public async Task SignUpCreatesTheAccountTheGameLogsInWith()
    {
        using var browser = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.RegisterAsync("   ", "secret-1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.RegisterAsync("panel-new", "short")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.RegisterAsync("panel\u0001new", "secret-1")).StatusCode);
        var created = await browser.RegisterAsync("  panel-new  ", "secret-1");
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal("panel-new", (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("account").GetString());
        Assert.Equal("panel-new", (await browser.GetJsonAsync("/api/account")).GetProperty("name").GetString());

        // The same record as a first login in the game: the game signs in with it, and no second account appears.
        Assert.Equal(0, await SdkLoginAsync(_host, "panel-new", "secret-1"));
        Assert.Equal(401, await SdkLoginAsync(_host, "panel-new", "other-pw"));
        using var other = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.Conflict, (await other.RegisterAsync("PANEL-NEW", "secret-2")).StatusCode);
        Assert.Equal(0, await SdkLoginAsync(_host, "panel-game-first", "pw-game"));
        Assert.Equal(HttpStatusCode.Conflict, (await other.RegisterAsync("panel-game-first", "secret-2")).StatusCode);
        Assert.Equal("panel-new", await _host.Service<AccountStore>().VerifiedAccountAsync("Panel-New", "secret-1"));
    }

    [Fact]
    public async Task SignUpCanBeTurnedOff()
    {
        var (host, panel) = await StartAsync(o => o.Panel.Registration = false);
        await using var _ = host;
        using var browser = new PanelBrowser(panel);
        Assert.False((await browser.SessionAsync()).GetProperty("registration").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.RegisterAsync("panel-closed", "secret-1")).StatusCode);
        Assert.Null(await host.Service<AccountStore>().VerifiedAccountAsync("panel-closed", "secret-1"));
    }

    [Fact]
    public async Task APasswordChangeNeedsTheCurrentPasswordAndSignsOutOtherBrowsers()
    {
        Assert.Equal(0, await SdkLoginAsync(_host, "panel-pw", "old-pass"));
        using var first = new PanelBrowser(_panel);
        using var second = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await first.LoginAsync("panel-pw", "old-pass")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.LoginAsync("panel-pw", "old-pass")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await first.PostAsync("/api/account/password", new { current = "wrong", next = "new-pass" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await first.PostAsync("/api/account/password", new { current = "old-pass", next = "new" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.PostAsync("/api/account/password", new { current = "old-pass", next = "new-pass" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.Http.GetAsync("/api/account")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.Http.GetAsync("/api/account")).StatusCode);
        Assert.Equal(401, await SdkLoginAsync(_host, "panel-pw", "old-pass"));
        Assert.Equal(0, await SdkLoginAsync(_host, "panel-pw", "new-pass"));
    }

    [Fact]
    public async Task OnlineRankingsAndTheAccountPageShowTheCharacters()
    {
        Assert.Equal(0, await SdkLoginAsync(_host, Admin, "pw-admin"));
        await using var bot = await _host.InWorldAsync("Walker");
        using var anonymous = new PanelBrowser(_panel);
        var online = Assert.Single((await anonymous.GetJsonAsync("/api/online")).GetProperty("players").EnumerateArray());
        Assert.Equal("Walker", online.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, online.GetProperty("account").ValueKind);
        // The status counts from the loop's census, taken once a second.
        var deadline = DateTime.UtcNow + Wait;
        while ((await anonymous.GetJsonAsync("/api/status")).GetProperty("players").GetInt32() == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Equal(1, (await anonymous.GetJsonAsync("/api/status")).GetProperty("players").GetInt32());
        using var admin = new PanelBrowser(_panel);
        await admin.LoginAsync(Admin, "pw-admin");
        Assert.Equal("walker", Assert.Single((await admin.GetJsonAsync("/api/online")).GetProperty("players").EnumerateArray())
            .GetProperty("account").GetString());

        var boards = (await anonymous.GetJsonAsync("/api/rankings")).GetProperty("boards").EnumerateArray().Select(b => b.GetProperty("id").GetString()).ToList();
        Assert.Contains("level", boards);
        Assert.Contains("power", boards);
        var level = await anonymous.GetJsonAsync("/api/rankings/level");
        Assert.Contains(level.GetProperty("rows").EnumerateArray(), r => r.GetProperty("name").GetString() == "Walker");
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.Http.GetAsync("/api/rankings/nothing")).StatusCode);

        using var player = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await player.LoginAsync("walker", "pw")).StatusCode);
        var character = Assert.Single((await player.GetJsonAsync("/api/account")).GetProperty("characters").EnumerateArray());
        Assert.Equal("Walker", character.GetProperty("name").GetString());
        Assert.True(character.GetProperty("online").GetBoolean());
        Assert.True(character.GetProperty("level").GetInt32() >= 1);
    }

    [Fact]
    public async Task TheItemDatabaseFindsItemsAndShowsTheirStatsDropsAndShops()
    {
        using var anonymous = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.Http.GetAsync("/items/3990001")).StatusCode);
        var found = await anonymous.GetJsonAsync("/api/items?q=synthetic");
        Assert.Equal(["Synthetic Blade", "Synthetic Jellopy"], found.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()));
        Assert.Contains(found.GetProperty("qualities").EnumerateArray(), q => q.GetProperty("name").GetString() == "Purple");
        // Unnamed rows of the fixture are not listed, nor a copy of an item under another id (3990003 is 3990001 but for its
        // sort order) nor a "TBD" row; filters narrow by type, quality and level; an id finds its item.
        Assert.Equal(2, (await anonymous.GetJsonAsync("/api/items")).GetProperty("total").GetInt32());
        Assert.Equal("TBD", Assert.Single((await anonymous.GetJsonAsync("/api/items?q=3990004")).GetProperty("items").EnumerateArray())
            .GetProperty("name").GetString());
        var copy = await anonymous.GetJsonAsync("/api/items/3990003");
        Assert.Equal([3990001L, 3990003L], copy.GetProperty("ids").EnumerateArray().Select(v => v.GetInt64()));
        // One row per name: the purple and the orange Synthetic Blade are one row with their level range; a filter picks
        // the variant it matches.
        var blades = Assert.Single((await anonymous.GetJsonAsync("/api/items?q=blade")).GetProperty("items").EnumerateArray());
        Assert.Equal((2, 30, 40), (blades.GetProperty("variants").GetInt32(), blades.GetProperty("level").GetInt32(), blades.GetProperty("maxLevel").GetInt32()));
        Assert.Equal(3990005, Assert.Single((await anonymous.GetJsonAsync("/api/items?quality=5")).GetProperty("items").EnumerateArray())
            .GetProperty("id").GetInt64());
        Assert.Equal([3990002L, 3990005L], (await anonymous.GetJsonAsync("/api/items/3990005")).GetProperty("variants").EnumerateArray()
            .Select(v => v.GetProperty("id").GetInt64()));
        Assert.Equal("Synthetic Blade", Assert.Single((await anonymous.GetJsonAsync("/api/items?type=5&quality=4&min=20")).GetProperty("items")
            .EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(0, (await anonymous.GetJsonAsync("/api/items?q=synthetic&max=10&type=5")).GetProperty("total").GetInt32());
        Assert.Equal(3990001, Assert.Single((await anonymous.GetJsonAsync("/api/items?q=3990001")).GetProperty("items").EnumerateArray())
            .GetProperty("id").GetInt64());

        var jellopy = await anonymous.GetJsonAsync("/api/items/3990001");
        Assert.Equal("A sticky test drop. Worth 5 Zeny.", jellopy.GetProperty("description").GetString());
        // Without the export's pictures the pages get no address and draw a letter.
        Assert.Equal("", jellopy.GetProperty("item").GetProperty("icon").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.Http.GetAsync("/img/items/item_3990001.png")).StatusCode);
        Assert.Equal("Exchange", jellopy.GetProperty("trade").GetString());
        var sources = jellopy.GetProperty("droppedBy").EnumerateArray().ToList();
        var drop = Assert.Single(sources, d => d.GetProperty("kind").GetString() == "");
        Assert.Equal("Synthetic Poring", drop.GetProperty("monster").GetString());
        // The field MVP's rewards are listed by kind: its first strike award and its rare award roll the same pool.
        Assert.Equal(["First strike", "Rare winner"], sources.Where(d => d.GetProperty("monster").GetString() == "Synthetic Angeling")
            .Select(d => d.GetProperty("kind").GetString()));
        // Slot 1 (50 %, its only row) and slot 2 (100 %, weight 3 of 4): 1 - 0.5 x 0.25 per kill.
        Assert.Equal(0.875, drop.GetProperty("chance").GetDouble(), 6);
        Assert.Equal(3, drop.GetProperty("max").GetInt32());
        var shop = Assert.Single(jellopy.GetProperty("shops").EnumerateArray());
        Assert.Equal("Test Shop", shop.GetProperty("shop").GetString());
        Assert.Equal(25, shop.GetProperty("price").GetInt64());

        var blade = await anonymous.GetJsonAsync("/api/items/3990002");
        Assert.Equal("Purple", blade.GetProperty("item").GetProperty("qualityName").GetString());
        Assert.Equal("One-Handed Sword", blade.GetProperty("item").GetProperty("subType").GetString());
        Assert.Equal("Not tradeable", blade.GetProperty("trade").GetString());
        var stat = Assert.Single(blade.GetProperty("stats").EnumerateArray());
        Assert.Equal(("PATK", "+420-570"), (stat.GetProperty("name").GetString(), stat.GetProperty("value").GetString()));
        Assert.Equal(0.25, Assert.Single(blade.GetProperty("droppedBy").EnumerateArray()).GetProperty("chance").GetDouble(), 6);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.Http.GetAsync("/api/items/1000000")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.Http.GetAsync("/api/items/x")).StatusCode);
    }

    [Fact]
    public async Task TheMonsterDatabaseShowsTheServersHpDropsAndSpawns()
    {
        using var anonymous = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.Http.GetAsync("/monsters/16320002")).StatusCode);
        // One row per name: 16320004 is a copy of 16320002 (without the drop row), 16320005 a level 20 Synthetic Poring. The
        // row is the variant that spawns in the field, with the level range of all of them.
        var found = Assert.Single((await anonymous.GetJsonAsync("/api/monsters?q=poring&element=4&rank=mini")).GetProperty("monsters").EnumerateArray());
        Assert.Equal((16320002, 12, 20, 2), (found.GetProperty("id").GetInt64(), found.GetProperty("level").GetInt32(),
            found.GetProperty("maxLevel").GetInt32(), found.GetProperty("variants").GetInt32()));
        // A level 1 actor with a boss's HP and no field spawn is not listed; its id finds it.
        Assert.Equal(0, (await anonymous.GetJsonAsync("/api/monsters?q=dancer")).GetProperty("total").GetInt32());
        Assert.Equal(16320006, Assert.Single((await anonymous.GetJsonAsync("/api/monsters?q=16320006")).GetProperty("monsters").EnumerateArray())
            .GetProperty("id").GetInt64());

        var poring = await anonymous.GetJsonAsync("/api/monsters/16320002");
        var monster = poring.GetProperty("monster");
        // MonsterService's own numbers: AttriMonsterConfig 900002 HP 2000 x _kAttriScale 15000 / 10000.
        Assert.Equal(3000, monster.GetProperty("hp").GetInt64());
        Assert.Equal(12, monster.GetProperty("level").GetInt32());
        Assert.Equal(("Fire", "Brute", "Medium", "mini"), (monster.GetProperty("elementName").GetString(), monster.GetProperty("race").GetString(),
            monster.GetProperty("size").GetString(), monster.GetProperty("rank").GetString()));
        var stats = poring.GetProperty("stats").EnumerateArray().ToDictionary(s => s.GetProperty("name").GetString()!, s => s.GetProperty("value").GetString());
        Assert.Equal("150", stats["PATK"]);
        Assert.Equal("13", stats["HIT"]); // level + 1 without an attribute of its own
        var drops = poring.GetProperty("drops").EnumerateArray().ToList();
        Assert.Equal(["Synthetic Jellopy", "Synthetic Blade"], drops.Select(d => d.GetProperty("item").GetString()));
        Assert.Equal(0.875, drops[0].GetProperty("chance").GetDouble(), 6);
        var spawn = Assert.Single(poring.GetProperty("spawns").EnumerateArray());
        Assert.Equal(("Synthetic Field", 4, 30), (spawn.GetProperty("map").GetString(), spawn.GetProperty("count").GetInt32(),
            spawn.GetProperty("respawnSeconds").GetInt32()));
        // Its page lists the variants of its name, with where each spawns; the drops are merged and say which variant drops
        // what when not all of them do.
        Assert.Equal([16320002L, 16320004L], poring.GetProperty("ids").EnumerateArray().Select(v => v.GetInt64()));
        var variants = poring.GetProperty("variants").EnumerateArray().ToList();
        Assert.Equal([(16320002L, 12, "Synthetic Field"), (16320005L, 20, "")], variants.Select(v => (v.GetProperty("id").GetInt64(),
            v.GetProperty("level").GetInt32(), string.Join(",", v.GetProperty("maps").EnumerateArray().Select(m => m.GetString())))));
        Assert.All(drops, d => Assert.Equal([16320002L], d.GetProperty("from").EnumerateArray().Select(v => v.GetInt64())));
        Assert.Equal(20, (await anonymous.GetJsonAsync("/api/monsters/16320005")).GetProperty("monster").GetProperty("level").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.Http.GetAsync("/api/monsters/16320001")).StatusCode); // no name

        // A field MVP pays out through MvpService's awards (MVPConfig -> MultiverseGroupConfig -> AwardConfig -> pool), by
        // kind; the rare award comes at _iRareRewardProbability 5000 / 10000 per kill. It spawns on MVPConfig's map.
        var mvp = await anonymous.GetJsonAsync("/api/monsters/16320003");
        Assert.Equal("mvp", mvp.GetProperty("monster").GetProperty("rank").GetString());
        var rewards = mvp.GetProperty("drops").EnumerateArray()
            .ToDictionary(d => d.GetProperty("kind").GetString()!, d => (d.GetProperty("item").GetString(), d.GetProperty("chance").GetDouble()));
        Assert.Equal(["First strike", "Rare winner"], rewards.Keys.Order());
        Assert.Equal(("Synthetic Jellopy", 1.0), rewards["First strike"]);
        Assert.Equal(0.5, rewards["Rare winner"].Item2, 6);
        Assert.Equal(("Synthetic Field", 3600), (Assert.Single(mvp.GetProperty("spawns").EnumerateArray()).GetProperty("map").GetString(),
            mvp.GetProperty("spawns")[0].GetProperty("respawnSeconds").GetInt32()));
        Assert.Equal(1, (await anonymous.GetJsonAsync("/api/monsters?rank=mvp")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task TheMapsListTheirMonstersOnceAndTheirLevels()
    {
        using var anonymous = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.Http.GetAsync("/maps/10112")).StatusCode);
        // 10111 has no name: not a map the panel lists.
        var map = Assert.Single((await anonymous.GetJsonAsync("/api/maps")).GetProperty("maps").EnumerateArray());
        Assert.Equal(("Synthetic Field", 12, 35, 2, ""), (map.GetProperty("name").GetString(), map.GetProperty("minLevel").GetInt32(),
            map.GetProperty("maxLevel").GetInt32(), map.GetProperty("monsters").GetInt32(), map.GetProperty("image").GetString()));
        var detail = await anonymous.GetJsonAsync("/api/maps/10112");
        Assert.Equal(["Synthetic Poring", "Synthetic Angeling"], detail.GetProperty("monsters").EnumerateArray()
            .Select(m => m.GetProperty("monster").GetProperty("name").GetString()));
        Assert.Equal(0, detail.GetProperty("players").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.Http.GetAsync("/api/maps/10111")).StatusCode);
    }

    /// <summary>
    /// The export's pictures (Ro3Emu:Panel:ImageDir, here a folder of invented files): an address with the source's hash for
    /// the ones index.json lists, served as PNG for a year; nothing outside the export's own folders and names.
    /// </summary>
    [Fact]
    public async Task PicturesOfTheExportAreServedWithTheirVersionAndMissingOnesAreNot()
    {
        var images = Directory.CreateTempSubdirectory("ro3emu-panel-img-");
        try
        {
            Directory.CreateDirectory(Path.Combine(images.FullName, "items"));
            Directory.CreateDirectory(Path.Combine(images.FullName, "monsters"));
            byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3];
            File.WriteAllBytes(Path.Combine(images.FullName, "items", "item_3990001.png"), png);
            File.WriteAllBytes(Path.Combine(images.FullName, "monsters", "headicon_monster_synthetic.png"), png);
            File.WriteAllText(Path.Combine(images.FullName, "secret.txt"), "no");
            File.WriteAllText(Path.Combine(images.FullName, "index.json"),
                """{"items/item_3990001.png":{"Hash":"1234abcd5678"},"monsters/headicon_monster_synthetic.png":{"Hash":"99","Atlas":"x.spriteatlasv2","AtlasHash":"777777"}}""");
            var (host, panel) = await StartAsync(o => o.Panel.ImageDir = images.FullName);
            await using var _ = host;
            using var anonymous = new PanelBrowser(panel);

            var jellopy = (await anonymous.GetJsonAsync("/api/items/3990001")).GetProperty("item");
            Assert.Equal("/img/items/item_3990001.png?v=1234abcd", jellopy.GetProperty("icon").GetString());
            Assert.Equal("", (await anonymous.GetJsonAsync("/api/items/3990002")).GetProperty("item").GetProperty("icon").GetString());
            Assert.Equal("/img/monsters/headicon_monster_synthetic.png?v=997777",
                (await anonymous.GetJsonAsync("/api/monsters/16320002")).GetProperty("monster").GetProperty("icon").GetString());

            var image = await anonymous.Http.GetAsync(jellopy.GetProperty("icon").GetString());
            Assert.Equal(HttpStatusCode.OK, image.StatusCode);
            Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
            Assert.Equal(png, await image.Content.ReadAsByteArrayAsync());
            Assert.Contains("immutable", image.Headers.CacheControl?.ToString());
            Assert.Equal("nosniff", image.Headers.GetValues("X-Content-Type-Options").Single());
            foreach (string path in new[] { "/img/items/item_3990002.png", "/img/secret.txt", "/img/items/../secret.txt", "/img/items/%2e%2e%2fsecret.txt",
                "/img/other/item_3990001.png", "/img/items/item_3990001.PNG" })
                Assert.Equal(HttpStatusCode.NotFound, (await anonymous.Http.GetAsync(path)).StatusCode);
        }
        finally { images.Delete(recursive: true); }
    }

    [Fact]
    public async Task TheEconomyLogIsForAdminsAndFiltersByAccountCharacterItemAndDay()
    {
        Assert.Equal(0, await SdkLoginAsync(_host, Admin, "pw-admin"));
        await using (var bot = await _host.InWorldAsync("Trader")) { }
        using var admin = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await admin.LoginAsync(Admin, "pw-admin")).StatusCode);
        // Entering the world may itself record a starter kit; the rows below come on top.
        int accountBefore = (await admin.GetJsonAsync("/api/admin/economy?account=trader")).GetProperty("total").GetInt32();
        var zenyBefore = await admin.GetJsonAsync("/api/admin/economy?asset=1");
        long role;
        var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_host.DataDir, "ro3emu.db"), Pooling = false, DefaultTimeout = 10 };
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(connection.ToString()))
        {
            db.Open();
            using (var read = db.CreateCommand())
            {
                read.CommandText = "SELECT id FROM role WHERE name='Trader'";
                role = (long)read.ExecuteScalar()!;
            }
            // Two receipts of the trader's account, one a day earlier; the second pays 25 Zeny (item 1) for 1 Jellopy.
            long day = DateTimeOffset.Parse("2026-10-20T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
            var legs = new (string Op, long At, long Asset, long Delta, int Reason)[]
            {
                ("panel-op-1", day, 3990001, 3, 2), ("panel-op-2", day + 86_400_000, 1, -25, 10), ("panel-op-2", day + 86_400_000, 3990001, 1, 9),
            };
            int seq = 1_000_000;
            foreach (var op in legs.Select(l => (l.Op, l.At)).Distinct())
            {
                using var insert = db.CreateCommand();
                insert.CommandText = "INSERT INTO operation_receipt(operation_id,realm_id,commit_seq,actor_account,actor_role,command_kind," +
                    "request_fingerprint,intent_hash,intent_payload,source_key,recorded_utc_ms,effective_utc_ms,outcome,child_receipts," +
                    "before_revisions,after_revisions,leg_count) VALUES($op,'test',$seq,'trader',$role,'npc-shop-buy','','',X'',$op,$at,$at,X'',X'','','',2)";
                insert.Parameters.AddWithValue("$op", op.Op);
                insert.Parameters.AddWithValue("$seq", ++seq);
                insert.Parameters.AddWithValue("$role", role);
                insert.Parameters.AddWithValue("$at", op.At);
                insert.ExecuteNonQuery();
            }
            int leg = 0;
            foreach (var l in legs)
            {
                using var insert = db.CreateCommand();
                insert.CommandText = "INSERT INTO economy_entry(commit_seq,operation_id,leg_no,recorded_utc_ms,effective_utc_ms,owner_kind,owner_id," +
                    "compartment_kind,compartment_key,instance_key,asset_kind,asset_id,asset_epoch,delta,reason_code,leg_kind,edge_no,source_key) " +
                    "VALUES(0,$op,$leg,$at,$at,1,$owner,1,'bag','',$kind,$asset,'',$delta,$reason,1,0,$op)";
                insert.Parameters.AddWithValue("$op", l.Op);
                insert.Parameters.AddWithValue("$leg", ++leg);
                insert.Parameters.AddWithValue("$at", l.At);
                insert.Parameters.AddWithValue("$owner", role.ToString(System.Globalization.CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$kind", l.Asset == 1 ? 2 : 1);
                insert.Parameters.AddWithValue("$asset", l.Asset);
                insert.Parameters.AddWithValue("$delta", l.Delta);
                insert.Parameters.AddWithValue("$reason", l.Reason);
                insert.ExecuteNonQuery();
            }
        }

        using var player = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await player.LoginAsync("trader", "pw")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await player.Http.GetAsync("/api/admin/economy")).StatusCode);
        using var anonymous = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Http.GetAsync("/api/admin/economy")).StatusCode);

        var mine = await admin.GetJsonAsync("/api/admin/economy?account=TRADER");
        Assert.Equal(accountBefore + 3, mine.GetProperty("total").GetInt32());
        var newest = mine.GetProperty("entries")[0];
        Assert.Equal(("Trader", "NPC shop goods", "Synthetic Jellopy", 1L), (newest.GetProperty("owner").GetString(),
            newest.GetProperty("reason").GetString(), newest.GetProperty("asset").GetString(), newest.GetProperty("delta").GetInt64()));

        var jellopy = await admin.GetJsonAsync("/api/admin/economy?character=trader&asset=jellopy");
        Assert.Equal((2, 4L, 0L), (jellopy.GetProperty("total").GetInt32(), jellopy.GetProperty("in").GetInt64(), jellopy.GetProperty("out").GetInt64()));
        var zeny = await admin.GetJsonAsync("/api/admin/economy?asset=1");
        Assert.Equal((zenyBefore.GetProperty("total").GetInt32() + 1, zenyBefore.GetProperty("out").GetInt64() - 25),
            (zeny.GetProperty("total").GetInt32(), zeny.GetProperty("out").GetInt64()));
        var firstDay = await admin.GetJsonAsync("/api/admin/economy?account=trader&from=2026-10-20&to=2026-10-20");
        Assert.Equal("Monster drop", Assert.Single(firstDay.GetProperty("entries").EnumerateArray()).GetProperty("reason").GetString());
        Assert.Equal(0, (await admin.GetJsonAsync("/api/admin/economy?account=nobody")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await admin.GetJsonAsync("/api/admin/economy?asset=no-such-item")).GetProperty("total").GetInt32());
    }

    internal static async Task<int> SdkLoginAsync(SyntheticHost host, string identity, string credential)
    {
        using var client = new HttpClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["identity"] = identity, ["credential"] = credential });
        var body = await (await client.PostAsync(host.Http + SdkEndpoints.LoginPath, form)).Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("code").GetInt32();
    }

    /// <summary>A browser on the panel: its cookies, and the CSRF token of its latest /api/session.</summary>
    internal sealed class PanelBrowser : IDisposable
    {
        private readonly HttpClientHandler _handler = new() { CookieContainer = new CookieContainer(), UseCookies = true };

        public PanelBrowser(string panel) => Http = new HttpClient(_handler) { BaseAddress = new Uri(panel) };

        public HttpClient Http { get; }
        /// <summary>The token the next POST sends; a test may overwrite it.</summary>
        public string Csrf { get; set; } = "";
        private bool _visited;

        public async Task<JsonElement> GetJsonAsync(string path)
        {
            var response = await Http.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        public async Task<JsonElement> SessionAsync()
        {
            var session = await GetJsonAsync("/api/session");
            Csrf = session.GetProperty("csrf").GetString() ?? "";
            _visited = true;
            return session;
        }

        public async Task<HttpResponseMessage> PostAsync(string path, object body)
        {
            if (!_visited) await SessionAsync();
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-CSRF-Token", Csrf);
            var response = await Http.SendAsync(request);
            // A login or sign-up answers with the new session and its token; the body stays readable for the test.
            string text = await response.Content.ReadAsStringAsync();
            response.Content = new StringContent(text, System.Text.Encoding.UTF8, "application/json");
            if (response.IsSuccessStatusCode && path is "/api/login" or "/api/register")
                Csrf = JsonDocument.Parse(text).RootElement.GetProperty("csrf").GetString() ?? "";
            return response;
        }

        public Task<HttpResponseMessage> LoginAsync(string account, string password) => PostAsync("/api/login", new { account, password });

        public Task<HttpResponseMessage> RegisterAsync(string account, string password) => PostAsync("/api/register", new { account, password });

        public void Dispose()
        {
            Http.Dispose();
            _handler.Dispose();
        }
    }
}
