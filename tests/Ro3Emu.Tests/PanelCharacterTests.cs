using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using static Ro3Emu.Tests.PanelTests;

namespace Ro3Emu.Tests;

/// <summary>
/// The panel's character sheet and mailbox viewer (read only, through the server) and its white-label branding.
/// Accounts and values are invented.
/// </summary>
public sealed class PanelCharacterTests : IAsyncLifetime
{
    private const string Admin = "sheet-admin", Player = "sheeter";
    private SyntheticHost _host = null!;
    private string _panel = "";

    public async Task InitializeAsync() => (_host, _panel) = await StartAsync(o => o.GmAccounts = [Admin]);

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    /// <summary>The admin's GM console, run as the player's character.</summary>
    private static async Task Gm(PanelBrowser admin, string line) =>
        Assert.True((await (await admin.PostAsync("/api/admin/gm", new { character = "Sheeter", line })).Content
            .ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("ran").GetBoolean());

    private GameSession Session() => _host.Service<SessionRegistry>().All().Single(s => s.Role?.Name == "Sheeter");

    [Fact]
    public async Task TheSheetShowsWhatTheServerKeepsToItsOwnerAndAdminsOnly()
    {
        Assert.Equal(0, await SdkLoginAsync(_host, Admin, "pw-admin"));
        using var admin = new PanelBrowser(_panel);
        await admin.LoginAsync(Admin, "pw-admin");
        await using (var bot = await _host.InWorldAsync("Sheeter"))
        {
            await Gm(admin, "!item 3990001 3");
            await Gm(admin, "!item 3990002 1");
            await Gm(admin, "!zeny 500");
            long id = 0;
            // Worn in the session only: the sheet of a character in the game is its live state.
            await _host.Service<GameLoop>().InvokeAsync(() =>
            {
                var session = Session();
                id = session.Role!.Id;
                var blade = session.Items.Single(i => i.Entry == 3990002);
                blade.Grid = 1;
                blade.Enchantments[1] = new ItemEnchantment { Attr = { [103] = 10 } };
                session.EquipLevels.Strength[1] = 4;
                return Task.CompletedTask;
            });

            var sheet = (await admin.GetJsonAsync($"/api/characters/{id}")).GetProperty("sheet");
            Assert.True(sheet.GetProperty("live").GetBoolean());
            Assert.Equal(Player, sheet.GetProperty("account").GetString());
            var worn = Assert.Single(sheet.GetProperty("equipment").EnumerateArray());
            Assert.Equal(("Main hand", "Synthetic Blade", 4), (worn.GetProperty("slot").GetString(),
                worn.GetProperty("item").GetProperty("name").GetString(), worn.GetProperty("strengthen").GetInt32()));
            var enchant = Assert.Single(worn.GetProperty("enchantments").EnumerateArray());
            Assert.Equal(("PATK", "+10"), (enchant.GetProperty("name").GetString(), enchant.GetProperty("value").GetString()));
            var bagged = sheet.GetProperty("bags").EnumerateArray().SelectMany(b => b.GetProperty("items").EnumerateArray());
            Assert.Contains(bagged, i => i.GetProperty("itemId").GetInt64() == 3990001 && i.GetProperty("count").GetInt32() == 3);
            Assert.Contains(sheet.GetProperty("currencies").EnumerateArray(), c => c.GetProperty("itemId").GetInt64() == 1 && c.GetProperty("amount").GetInt64() >= 500);
            Assert.Equal(["STR", "AGI", "VIT", "INT", "DEX", "LUK"], sheet.GetProperty("stats").EnumerateArray().Select(s => s.GetProperty("name").GetString()));

            // The owner sees it; another player gets the same "not found" as for a character that does not exist.
            using var owner = new PanelBrowser(_panel);
            Assert.Equal(HttpStatusCode.OK, (await owner.LoginAsync(Player, "pw")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.Http.GetAsync($"/api/characters/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.Http.GetAsync($"/api/admin/characters/{id}/mail")).StatusCode);
            using var nosy = new PanelBrowser(_panel);
            Assert.Equal(HttpStatusCode.OK, (await nosy.PostAsync("/api/register", new { account = "nosy", password = "pw-nosy-1" })).StatusCode);
            await nosy.LoginAsync("nosy", "pw-nosy-1");
            Assert.Equal(HttpStatusCode.NotFound, (await nosy.Http.GetAsync($"/api/characters/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.Http.GetAsync("/api/characters/999999")).StatusCode);
            using var anonymous = new PanelBrowser(_panel);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Http.GetAsync($"/api/characters/{id}")).StatusCode);

            // The mailbox, newest first, as MailService stored it.
            var target = new { kind = "character", name = "Sheeter", onlineOnly = false, minLevel = 0 };
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/admin/mail",
                new { target, title = "Older", text = "First", attachments = new[] { new { item = 3990001, count = 2 } } })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/admin/mail", new { target, title = "Newer", text = "Second" })).StatusCode);
            var box = await admin.GetJsonAsync($"/api/admin/characters/{id}/mail");
            Assert.Equal(2, box.GetProperty("total").GetInt32());
            var letters = box.GetProperty("mails").EnumerateArray().ToList();
            Assert.Equal(["Newer", "Older"], letters.Select(l => l.GetProperty("title").GetString()));
            var attachment = Assert.Single(letters[1].GetProperty("attachments").EnumerateArray());
            Assert.Equal(("Synthetic Jellopy", 2L), (attachment.GetProperty("name").GetString(), attachment.GetProperty("count").GetInt64()));
            Assert.False(letters[1].GetProperty("claimed").GetBoolean());
        }

        // Out of the game: the saved state, without what only the session held.
        using var later = new PanelBrowser(_panel);
        await later.LoginAsync(Admin, "pw-admin");
        long role = (await _host.Service<RoleStore>().ForAccountAsync(Player)).Single().Id;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        System.Text.Json.JsonElement saved;
        while ((saved = (await later.GetJsonAsync($"/api/characters/{role}")).GetProperty("sheet")).GetProperty("live").GetBoolean() && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.False(saved.GetProperty("live").GetBoolean());
        Assert.Contains(saved.GetProperty("bags").EnumerateArray().SelectMany(b => b.GetProperty("items").EnumerateArray()),
            i => i.GetProperty("itemId").GetInt64() == 3990001);
    }

    [Fact]
    public async Task BrandingNamesTheServerAndServesOnlyALogoInsideTheDataFolder()
    {
        var (host, panel) = await StartAsync(o =>
        {
            o.Panel.Branding.ServerName = "  Test Realm  ";
            o.Panel.Branding.Tagline = "Rates 5x";
            o.Panel.Branding.FooterText = "A test server.";
            o.Panel.Branding.AccentColor = "#123abc";
            o.Panel.Branding.LogoPath = "brand/logo.png";
        });
        await using var _ = host;
        string dataDir = host.Service<IOptions<ServerOptions>>().Value.DataDir;
        Directory.CreateDirectory(Path.Combine(dataDir, "brand"));
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 9];
        File.WriteAllBytes(Path.Combine(dataDir, "brand", "logo.png"), png);

        using var browser = new PanelBrowser(panel);
        var server = (await browser.SessionAsync()).GetProperty("server");
        Assert.Equal(("Test Realm", "Rates 5x", "A test server.", "#123abc"), (server.GetProperty("name").GetString(),
            server.GetProperty("tagline").GetString(), server.GetProperty("footer").GetString(), server.GetProperty("accent").GetString()));
        Assert.Equal("Test Realm", (await browser.GetJsonAsync("/api/status")).GetProperty("name").GetString());
        string logo = server.GetProperty("logo").GetString()!;
        Assert.StartsWith("/brand/logo?v=", logo);
        var image = await browser.Http.GetAsync(logo);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await image.Content.ReadAsByteArrayAsync());

        // A colour that is not #rrggbb and a logo outside the data folder are left out.
        var (other, otherPanel) = await StartAsync(o =>
        {
            o.Panel.Branding.AccentColor = "red;background:url(x)";
            o.Panel.Branding.LogoPath = "../outside.png";
        });
        await using var __ = other;
        using var plain = new PanelBrowser(otherPanel);
        var defaults = (await plain.SessionAsync()).GetProperty("server");
        Assert.Equal("RO3Emu", defaults.GetProperty("name").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, defaults.GetProperty("accent").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, defaults.GetProperty("logo").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await plain.Http.GetAsync("/brand/logo")).StatusCode);
    }
}
