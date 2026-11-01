using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;
using static Ro3Emu.Tests.PanelTests;

namespace Ro3Emu.Tests;

/// <summary>
/// The web panel's Suspicious activity list (docs/systems/anticheat.md) on the invented fixture: refusals counted per
/// account, gains far above the realm's median, and the duplication check's two-owner case. Accounts are invented.
/// </summary>
public sealed class AntiCheatPanelTests : IAsyncLifetime
{
    private const string Admin = "watch-admin", Moderator = "watch-mod";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private SyntheticHost _host = null!;
    private string _panel = "";

    public async Task InitializeAsync() => (_host, _panel) = await StartAsync(o =>
    {
        o.GmAccounts = [Admin];
        o.Panel.Roles = new(StringComparer.Ordinal) { [Moderator] = "Moderator" };
        o.WalkCheck = "enforce";
        o.AntiCheat.Alerts.Refusals = 3;
        o.AntiCheat.Alerts.GainFactor = 10;
        o.AntiCheat.Alerts.ZenyFloor = 100;
        o.AntiCheat.Alerts.ExpFloor = 100;
    });

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private async Task<PanelBrowser> SignedInAsync(string account, string password = "pw")
    {
        Assert.Equal(0, await SdkLoginAsync(_host, account, password));
        var browser = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await browser.LoginAsync(account, password)).StatusCode);
        return browser;
    }

    private GameSession Session(string account) =>
        _host.Service<SessionRegistry>().All().Single(s => s.Account == account && s.Role != null);

    private static IEnumerable<JsonElement> Rows(JsonElement report, string kind) =>
        report.GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("kind").GetString() == kind);

    [Fact]
    public async Task AnAccountWhoseRequestsAreRefusedIsListedForStaff()
    {
        await using (var cheater = await _host.InWorldAsync("Cheater"))
        await using (var honest = await _host.InWorldAsync("Honest"))
        {
            await cheater.HeartbeatAsync();
            var at = await _host.Service<GameLoop>().InvokeAsync(() => Task.FromResult(Session("cheater").Position.Clone()));
            for (int i = 1; i <= 4; i++) await cheater.WalkAsync(at.X + 300 * i, at.Y, at.Z, stop: true);
            await honest.WalkAsync(at.X + 500, at.Y, at.Z, stop: true); // one refusal: below the threshold
            await cheater.HeartbeatAsync();
            await honest.HeartbeatAsync();
        }

        using var moderator = await SignedInAsync(Moderator);
        var report = await moderator.GetJsonAsync("/api/admin/suspicious");
        var row = Assert.Single(Rows(report, "Refused requests"));
        Assert.Equal("cheater", row.GetProperty("account").GetString());
        Assert.Equal("Cheater", row.GetProperty("character").GetString());
        Assert.Equal(4, row.GetProperty("value").GetInt64());
        Assert.Equal(3, row.GetProperty("threshold").GetInt64());
        var walk = Assert.Single(row.GetProperty("counts").EnumerateArray());
        Assert.Equal((CheatMonitor.Walk, 4L), (walk.GetProperty("kind").GetString(), walk.GetProperty("count").GetInt64()));
        Assert.StartsWith("walk: ", row.GetProperty("detail").GetString());
        Assert.Equal(2, report.GetProperty("accounts").GetInt32());
        Assert.False(report.GetProperty("duplication").GetBoolean());
        // A moderator cannot run the duplication check; a narrower window still holds this hour.
        Assert.False((await moderator.GetJsonAsync("/api/admin/suspicious?dupes=1&hours=1")).GetProperty("duplication").GetBoolean());

        // The counts are in the database, so they survive a restart.
        await _host.Service<CheatMonitor>().FlushAsync();
        var stored = await _host.Service<AccountStore>().ReadAsync<List<AntiCheatRefusalRow>>(new AntiCheatRefusalsRead(0, "cheater"));
        Assert.Equal(4, stored.Sum(r => r.Count));

        using var player = await SignedInAsync("honest");
        Assert.Equal(HttpStatusCode.Forbidden, (await player.Http.GetAsync("/api/admin/suspicious")).StatusCode);
        using var anonymous = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Http.GetAsync("/api/admin/suspicious")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.Http.GetAsync("/admin/suspicious")).StatusCode); // the page shell
    }

    [Fact]
    public async Task GainsFarAboveTheMedianAreListed()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var roles = new Dictionary<string, long>();
        foreach (string name in new[] { "Farmer", "Casual", "Weekend" })
        {
            await using var bot = await _host.InWorldAsync(name);
            roles[name] = bot.Roles.Single().Id;
            long exp = name == "Farmer" ? 400 : 20;
            await _host.Service<GameLoop>().InvokeAsync(() => _host.Service<CharacterService>().AddExpAsync(Session(bot.Account!), exp, 0));
        }
        // Ledger source legs as the game writes them: Farmer gains 50 000 Zeny, the others 1 000 and 2 000.
        var legs = new[] { ("Farmer", 50_000L), ("Casual", 1_000L), ("Weekend", 2_000L) };
        int seq = 2_000_000;
        foreach (var (name, zeny) in legs)
        {
            string op = "watch-" + name;
            FixtureSql.Execute(_host.Service<AccountStore>(), "INSERT INTO operation_receipt(operation_id,realm_id,commit_seq,actor_account," +
                "actor_role,command_kind,request_fingerprint,intent_hash,intent_payload,source_key,recorded_utc_ms,effective_utc_ms,outcome," +
                "child_receipts,before_revisions,after_revisions,leg_count) VALUES($op,'test',$seq,$account,$role,'kill','','',X'',$op,$at,$at," +
                "X'',X'','','',1)", ("$op", op), ("$seq", ++seq), ("$account", name.ToLowerInvariant()), ("$role", roles[name]), ("$at", now));
            FixtureSql.Execute(_host.Service<AccountStore>(), "INSERT INTO economy_entry(commit_seq,operation_id,leg_no,recorded_utc_ms," +
                "effective_utc_ms,owner_kind,owner_id,compartment_kind,compartment_key,instance_key,asset_kind,asset_id,asset_epoch,delta," +
                "reason_code,leg_kind,edge_no,source_key) VALUES(0,$op,1,$at,$at,1,$owner,2,'purse','',2,1,'',$delta,2,1,0,$op)",
                ("$op", op), ("$at", now), ("$owner", roles[name].ToString(System.Globalization.CultureInfo.InvariantCulture)), ("$delta", zeny));
        }

        using var admin = await SignedInAsync(Admin, "pw-admin");
        var report = await admin.GetJsonAsync("/api/admin/suspicious");
        // Entering the world may add the starter kit's Zeny to each; the median stays far below the farmer's gain.
        long median = report.GetProperty("thresholds").GetProperty("zenyMedian").GetInt64();
        Assert.InRange(median, 2_000, 3_000);
        var zenyRow = Assert.Single(Rows(report, "Zeny gain"));
        Assert.Equal(roles["Farmer"], zenyRow.GetProperty("characterId").GetInt64());
        Assert.True(zenyRow.GetProperty("value").GetInt64() >= 50_000);
        Assert.Equal(10 * median, zenyRow.GetProperty("threshold").GetInt64());
        var expRow = Assert.Single(Rows(report, "EXP gain"));
        Assert.Equal(roles["Farmer"], expRow.GetProperty("characterId").GetInt64());
        Assert.Equal("farmer", expRow.GetProperty("account").GetString());
    }

    [Fact]
    public async Task OneInstanceHeldByTwoCharactersIsADuplicate()
    {
        using var admin = await SignedInAsync(Admin, "pw-admin");
        long seller, buyer;
        await using (var a = await _host.InWorldAsync("Holder"))
        {
            seller = a.Roles.Single().Id;
            var ran = await (await admin.PostAsync("/api/admin/gm", new { character = "Holder", line = "!item 3990001 3" })).Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(ran.GetProperty("ran").GetBoolean());
        }
        await using (var b = await _host.InWorldAsync("Other")) buyer = b.Roles.Single().Id;
        var deadline = DateTime.UtcNow + Wait;
        List<InvItem> saved;
        while ((saved = await _host.Service<AccountStore>().ReadAsync<List<InvItem>>(new ItemReadPlan(seller))).All(i => i.Entry != 3990001)
            && DateTime.UtcNow < deadline) await Task.Delay(100);
        long guid = saved.Single(i => i.Entry == 3990001).Guid;
        // Another character's exchange listing carrying the holder's bag guid: no partial listing explains it.
        var listing = new TradeService.Listing
        {
            Sku = "dupe-1", Spu = "spu-1", SellerId = buyer, Entry = 3990001, Currency = 1, UnitPrice = 120, Quantity = 1,
            SaleTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ExpiresAtMs = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds(),
            Item = new InvItem { Guid = guid, Entry = 3990001, Stack = 1 },
        };
        FixtureSql.Execute(_host.Service<AccountStore>(), "INSERT INTO market_record(kind,id,data) VALUES($k,$i,$d)",
            ("$k", TradeService.Listings), ("$i", listing.Sku), ("$d", JsonSerializer.Serialize(listing)));

        Assert.Empty(Rows(await admin.GetJsonAsync("/api/admin/suspicious"), "Duplication")); // only on request
        var report = await admin.GetJsonAsync("/api/admin/suspicious?dupes=1");
        Assert.True(report.GetProperty("duplication").GetBoolean());
        var rows = Rows(report, "Duplication").ToList();
        Assert.Contains(rows, r => r.GetProperty("itemId").GetInt64() == 3990001
            && r.GetProperty("detail").GetString()!.StartsWith("Instance seen twice", StringComparison.Ordinal));
    }
}
