using System.Net;
using System.Text.Json;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using static Ro3Emu.Tests.PanelTests;

namespace Ro3Emu.Tests;

/// <summary>
/// The web panel's admin tools against a running server on the invented fixture: mail, gift codes, the GM console, the
/// news editor and broadcasts, each audited. Item 100 is an ItemConfig row of the fixture; names are invented.
/// </summary>
public sealed partial class PanelToolsTests : IAsyncLifetime
{
    private const string Admin = "tools-admin";
    private const int Item = 100;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private SyntheticHost _host = null!;
    private string _panel = "";

    public async Task InitializeAsync() => (_host, _panel) = await StartAsync(o => o.GmAccounts = [Admin]);

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private async Task<PanelBrowser> AdminAsync()
    {
        Assert.Equal(0, await SdkLoginAsync(_host, Admin, "pw-admin"));
        var admin = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await admin.LoginAsync(Admin, "pw-admin")).StatusCode);
        return admin;
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

    [Fact]
    public async Task MailReachesACharacterAndAMassMailWaitsForItsConfirmedCount()
    {
        await using var bot = await _host.InWorldAsync("Mailee");
        using var admin = await AdminAsync();
        var one = new { kind = "character", name = "Mailee", onlineOnly = false, minLevel = 0 };
        var unknown = await admin.PostAsync("/api/admin/mail", new { target = one, title = "Hi", attachments = new[] { new { item = 987654, count = 1 } } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("987654", await unknown.Content.ReadAsStringAsync());

        var sent = await OkAsync(admin.PostAsync("/api/admin/mail",
            new { target = one, title = "Welcome gift", text = "From the team", attachments = new[] { new { item = Item, count = 3 } } }));
        Assert.Equal(1, sent.GetProperty("delivered").GetInt32());
        var mailbox = JsonSerializer.Deserialize<MailService.Mailbox>(_host.Service<RoleStore>().LoadMailbox(RoleId(bot))!)!;
        var letter = Assert.Single(mailbox.Letters, l => l.Title == "Welcome gift");
        Assert.Equal([new MailService.Attachment(Item, 3)], letter.Attachments);

        var everyone = new { kind = "everyone", name = "", onlineOnly = true, minLevel = 0 };
        Assert.Equal(1, (await OkAsync(admin.PostAsync("/api/admin/mail/recipients", new { target = everyone }))).GetProperty("count").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/admin/mail", new { target = everyone, title = "All" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/admin/mail", new { target = everyone, title = "All", confirm = true, expected = 5 })).StatusCode);
        Assert.Equal(1, (await OkAsync(admin.PostAsync("/api/admin/mail", new { target = everyone, title = "All", confirm = true, expected = 1 })))
            .GetProperty("delivered").GetInt32());
        Assert.Equal(["mail", "mail"], await AuditActionsAsync(admin));
    }

    private long RoleId(BotClient bot) => Assert.Single(_host.Service<RoleStore>().ForAccount(bot.Account!)).Id;

    [Fact]
    public async Task GiftCodesAreCreatedListedAndDisabledThroughTheServer()
    {
        using var admin = await AdminAsync();
        var rewards = new[] { new { item = Item, count = 2 } };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/gift-codes", new { codes = "BAD CODE!", rewards })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/gift-codes", new { codes = "PANEL-1", rewards = Array.Empty<object>() })).StatusCode);
        await OkAsync(admin.PostAsync("/api/admin/gift-codes", new { codes = "PANEL-1", rewards, maxUses = 10, perAccount = 1 }));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/admin/gift-codes", new { codes = "panel-1\nPANEL-2", rewards, perAccount = 1 })).StatusCode);
        var batch = await OkAsync(admin.PostAsync("/api/admin/gift-codes", new { generateCount = 3, prefix = "EV-", rewards, perAccount = 1 }));
        Assert.All(batch.GetProperty("created").EnumerateArray(), c => Assert.Matches("^EV-[A-Z2-9]{10}$", c.GetString()!));

        var codes = (await admin.GetJsonAsync("/api/admin/gift-codes")).GetProperty("codes").EnumerateArray().ToList();
        Assert.Equal(4, codes.Count);
        var first = Assert.Single(codes, c => c.GetProperty("code").GetString() == "PANEL-1");
        Assert.Equal("active", first.GetProperty("state").GetString());
        Assert.Equal(0, first.GetProperty("uses").GetInt64());

        await OkAsync(admin.PostAsync("/api/admin/gift-codes/PANEL-1/disable", new { }));
        Assert.True((await _host.Service<AccountStore>().ReadGiftCodeAsync("PANEL-1", "someone"))!.Disabled);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/admin/gift-codes/NOPE-9/disable", new { })).StatusCode);
        Assert.Equal(["gift-code-disable", "gift-code-create", "gift-code-create"], await AuditActionsAsync(admin));
    }

    [Fact]
    public async Task TheGmConsoleRunsForAnOnlineCharacterAndKeepsTheAdminsHistory()
    {
        await using var bot = await _host.InWorldAsync("Walker");
        using var admin = await AdminAsync();
        var where = await OkAsync(admin.PostAsync("/api/admin/gm", new { character = "walker", line = "where" }));
        Assert.True(where.GetProperty("ran").GetBoolean());
        Assert.Equal("Walker", where.GetProperty("character").GetString());
        Assert.StartsWith("Scene 10111", where.GetProperty("replies")[0].GetString());

        var own = await OkAsync(admin.PostAsync("/api/admin/gm", new { line = "!where" }));
        Assert.False(own.GetProperty("ran").GetBoolean());
        Assert.False((await OkAsync(admin.PostAsync("/api/admin/gm", new { character = "Nobody", line = "!where" }))).GetProperty("ran").GetBoolean());

        var history = Assert.Single((await admin.GetJsonAsync("/api/admin/gm/history")).GetProperty("entries").EnumerateArray());
        Assert.Equal("Walker", history.GetProperty("target").GetString());
        Assert.StartsWith("!where => Scene", history.GetProperty("detail").GetString());

        using var player = new PanelBrowser(_panel);
        await player.LoginAsync("walker", "pw");
        Assert.Equal(HttpStatusCode.Forbidden, (await player.PostAsync("/api/admin/gm", new { line = "!where" })).StatusCode);
    }

    [Fact]
    public async Task TheNewsEditorPublishesSchedulesPinsAndDeletes()
    {
        using var admin = await AdminAsync();
        using var visitor = new PanelBrowser(_panel);
        long now = (await OkAsync(admin.PostAsync("/api/admin/news", new { title = "Now", body = "Live", tag = "event" }))).GetProperty("id").GetInt64();
        var later = DateTimeOffset.UtcNow.AddDays(2);
        long future = (await OkAsync(admin.PostAsync("/api/admin/news", new { title = "Later", tag = "patch", published = later }))).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/news", new { title = "Bad", tag = "rumour" })).StatusCode);

        var shown = (await visitor.GetJsonAsync("/api/news")).GetProperty("news").EnumerateArray().Select(n => n.GetProperty("title").GetString()).ToList();
        Assert.Equal(["Now"], shown);
        Assert.Equal(2, (await admin.GetJsonAsync("/api/admin/news")).GetProperty("news").GetArrayLength());

        await OkAsync(admin.PostAsync("/api/admin/news", new { id = future, title = "Later", tag = "patch", published = DateTimeOffset.UtcNow.AddMinutes(-1), pinned = true }));
        var top = (await visitor.GetJsonAsync("/api/news")).GetProperty("news")[0];
        Assert.Equal("Later", top.GetProperty("title").GetString());
        Assert.True(top.GetProperty("pinned").GetBoolean());

        await OkAsync(admin.PostAsync($"/api/admin/news/{now}/delete", new { }));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"/api/admin/news/{now}/delete", new { })).StatusCode);
        Assert.Single((await visitor.GetJsonAsync("/api/news")).GetProperty("news").EnumerateArray());
        Assert.Equal(["news-delete", "news-save", "news-save", "news-save"], await AuditActionsAsync(admin));
    }

    [Fact]
    public async Task ABroadcastReachesThePlayersInTheGameAndItsRepeatsCanBeCancelled()
    {
        await using var bot = await _host.InWorldAsync("Listener");
        using var admin = await AdminAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/admin/broadcasts", new { text = "x", everyMinutes = 0, repeats = 2 })).StatusCode);
        Assert.Equal(1, (await OkAsync(admin.PostAsync("/api/admin/broadcasts", new { text = "Restart in 10 minutes" }))).GetProperty("reached").GetInt32());
        var notice = BroadCastNotify.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(GmCommands.BroadcastNotify), Wait)).Body);
        Assert.Equal("Restart in 10 minutes", notice.Content);

        await OkAsync(admin.PostAsync("/api/admin/broadcasts", new { text = "Event at 20:00", everyMinutes = 30, repeats = 3 }));
        var repeating = Assert.Single((await admin.GetJsonAsync("/api/admin/broadcasts")).GetProperty("broadcasts").EnumerateArray());
        Assert.Equal(3, repeating.GetProperty("remaining").GetInt32());
        await OkAsync(admin.PostAsync($"/api/admin/broadcasts/{repeating.GetProperty("id").GetInt64()}/cancel", new { }));
        Assert.Equal(0, (await admin.GetJsonAsync("/api/admin/broadcasts")).GetProperty("broadcasts").GetArrayLength());
        Assert.Equal(["broadcast", "broadcast", "broadcast"], await AuditActionsAsync(admin));
    }
}
