using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using static Ro3Emu.Tests.PanelTests;

namespace Ro3Emu.Tests;

/// <summary>
/// The panel's player-facing pages over the server's own state: public profiles (and the game's privacy choice), guild
/// pages, the territory map, the market, and the admin's item tracking with its duplication check. The synthetic data
/// set is copied and given a small invented territory (GVGWarZoneConfig, SiegeConfig); every value is invented.
/// </summary>
public sealed class PanelPlayersTests : IAsyncLifetime
{
    private const string Admin = "players-admin";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private string _data = "";
    private SyntheticHost _host = null!;
    private string _panel = "";

    public async Task InitializeAsync()
    {
        _data = Path.Combine(Path.GetTempPath(), "ro3emu-panel-players-" + Guid.NewGuid().ToString("N"));
        Copy(SyntheticData.Dir, _data);
        Territory(Path.Combine(_data, "config"));
        (_host, _panel) = await StartAsync(o =>
        {
            o.GmAccounts = [Admin];
            o.GameDataDir = _data;
        });
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_data, recursive: true); } catch (IOException) { } // a file still held: the temp folder keeps it
    }

    private static void Copy(string from, string to)
    {
        foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(dir.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(file, file.Replace(from, to));
    }

    /// <summary>An invented territory: a capital, a region, a stronghold and a field, and two siege cities.</summary>
    private static void Territory(string config)
    {
        var text = JsonNode.Parse(File.ReadAllText(Path.Combine(config, "Localization_en.json")))!.AsObject();
        void Text(long id, string value) => text[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject { ["_iID"] = id, ["_kDes"] = value };
        Text(990100001, "Test Capital"); Text(990100002, "Test Region"); Text(990100003, "Test Stronghold @{1}"); Text(990100004, "Field");
        Text(990100005, "Test Keep"); Text(990100006, "Test Prefecture @{1}");
        File.WriteAllText(Path.Combine(config, "Localization_en.json"), text.ToJsonString());
        File.WriteAllText(Path.Combine(config, "GVGWarZoneConfig.json"), """
            {"1":{"_iID":1,"_iType":1,"_iBelongID":0,"_iDungeonID":0,"_iMapID":10111,"_iName":[990100001]},
             "2":{"_iID":2,"_iType":2,"_iBelongID":1,"_iDungeonID":0,"_iMapID":10111,"_iName":[990100002]},
             "3":{"_iID":3,"_iType":3,"_iBelongID":2,"_iDungeonID":0,"_iMapID":10111,"_iName":[990100003,"1"]},
             "4":{"_iID":4,"_iType":4,"_iBelongID":2,"_iDungeonID":0,"_iMapID":10111,"_iName":[990100004]}}
            """);
        File.WriteAllText(Path.Combine(config, "SiegeConfig.json"), """
            {"1001":{"_iID":1001,"_iCityType":1,"_iBelongArea":1,"_iCityName":[990100005],"_iLosePlayerAward":0,"_iWinPlayerAward":0},
             "1101":{"_iID":1101,"_iCityType":2,"_iBelongArea":1,"_iCityName":[990100006,"1"],"_iLosePlayerAward":0,"_iWinPlayerAward":0}}
            """);
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private static async Task Gm(PanelBrowser admin, string character, string line) =>
        Assert.True((await (await admin.PostAsync("/api/admin/gm", new { character, line })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ran").GetBoolean());

    private async Task<PanelBrowser> AdminAsync()
    {
        Assert.Equal(0, await SdkLoginAsync(_host, Admin, "pw-admin"));
        var admin = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await admin.LoginAsync(Admin, "pw-admin")).StatusCode);
        return admin;
    }

    private static async Task<long> GuildAsync(BotClient founder, PanelBrowser admin, string character, string name)
    {
        await Gm(admin, character, "!lvl 40");
        await founder.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = name, Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 },
        });
        Assert.Equal(0, (await Next(founder, GuildService.Create, GuildCreateResp.Parser)).Result);
        return (await Next(founder, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid > 0)).Guid;
    }

    /// <summary>The territory's committed state, as SiegeTests seeds it.</summary>
    private async Task SeedOwnersAsync(params (int Zone, long Guild)[] owners)
    {
        var wars = _host.Service<TerritoryWarsService>();
        var clock = _host.Service<TerritoryWarsClock>();
        var seeded = new TerritoryWarsService.WorldState { Epoch = $"{clock.SeasonStart}:{clock.PlayBook}" };
        foreach (var (zone, guild) in owners) seeded.Owners[zone] = guild;
        await _host.Service<GameLoop>().InvokeAsync(async () =>
        {
            using var scope = await _host.Service<GameLoop>().Owners!.EnterAsync("panel/territory seed",
                ReservationKey.Entity("realm", nameof(RealmStateKind.TerritoryWars)), ReservationKey.Entity("territory_season", seeded.Epoch));
            var accounts = _host.Service<AccountStore>();
            var transaction = accounts.NewTransaction();
            transaction.Add(new RealmStatePlan(RealmStateKind.TerritoryWars, JsonSerializer.SerializeToUtf8Bytes(seeded)));
            await transaction.CommitAsync(accounts, "panel/territory seed");
            typeof(TerritoryWarsService).GetProperty("state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(wars, seeded);
        });
    }

    /// <summary>Wears a saved item (the fixture's classes wield nothing) and sets its slot's strengthen level, in the database
    /// once the logout saved the character.</summary>
    private async Task WearAsync(string name, long entry, int grid, int strengthen)
    {
        long role = (await _host.Service<RoleStore>().ForAccountAsync(name.ToLowerInvariant())).Single().Id;
        var deadline = DateTime.UtcNow + Wait;
        while ((await _host.Service<AccountStore>().ReadAsync<List<InvItem>>(new ItemReadPlan(role))).All(i => i.Entry != entry) && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_host.DataDir, "ro3emu.db"), Pooling = false, DefaultTimeout = 10 };
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(connection.ToString());
        db.Open();
        using var update = db.CreateCommand();
        update.CommandText = "UPDATE item SET grid=$g WHERE role_id=$r AND entry=$e; UPDATE role SET equip_levels=$l WHERE id=$r";
        update.Parameters.AddWithValue("$g", grid);
        update.Parameters.AddWithValue("$r", role);
        update.Parameters.AddWithValue("$e", entry);
        update.Parameters.AddWithValue("$l", JsonSerializer.SerializeToUtf8Bytes(new EquipLevels { Strength = { [grid] = strengthen } }));
        update.ExecuteNonQuery();
    }

    /// <summary>Polls until the character's saved state passes the check (a logout saves it a moment later).</summary>
    private static async Task<JsonElement> UntilAsync(PanelBrowser browser, string path, Func<JsonElement, bool> done)
    {
        var deadline = DateTime.UtcNow + Wait;
        JsonElement value;
        while (!done(value = await browser.GetJsonAsync(path)) && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.True(done(value), path);
        return value;
    }

    [Fact]
    public async Task ProfilesGuildsAndTheTerritoryMapShowWhatOtherPlayersMaySee()
    {
        using var admin = await AdminAsync();
        long guild;
        await using (var leader = await _host.InWorldAsync("Leader"))
        await using (var shy = await _host.InWorldAsync("Shy"))
        {
            guild = await GuildAsync(leader, admin, "Leader", "PGuild");
            await Gm(admin, "Leader", "!item 3990002 1");
            // The game's one privacy choice: "show detailed info" off.
            await shy.SendGameAsync(MsgId.SaveSetting, new SaveSettingReq
            {
                Type = { SettingType.ShowDetailInfo },
                DbSettingInfo = new DBSettingInfo { ShowDetailInfo = new SettingShowDetailInfo { ShowDetailInfo = ShowDetailInfoType.NoShow } },
            });
            await Next(shy, MsgId.SaveSetting, SaveSettingResp.Parser);
        }
        await SeedOwnersAsync((1, guild), (3, guild));
        await WearAsync("Leader", 3990002, grid: 1, strengthen: 3);

        using var anonymous = new PanelBrowser(_panel);
        var profile = await UntilAsync(anonymous, "/api/profiles/leader", p => p.GetProperty("equipment").GetArrayLength() == 1);
        Assert.Equal(("Leader", false, "PGuild", "Leader"), (profile.GetProperty("name").GetString(), profile.GetProperty("private").GetBoolean(),
            profile.GetProperty("guild").GetProperty("name").GetString(), profile.GetProperty("position").GetString()));
        var worn = profile.GetProperty("equipment")[0];
        Assert.Equal(("Synthetic Blade", "Main hand", 3), (worn.GetProperty("item").GetProperty("name").GetString(), worn.GetProperty("slot").GetString(),
            worn.GetProperty("strengthen").GetInt32()));
        Assert.Contains(profile.GetProperty("ranks").EnumerateArray(), r => r.GetProperty("board").GetString() == "level");
        // Nothing private: no bags, purse, position or account.
        foreach (string hidden in new[] { "bags", "currencies", "account", "map", "x" }) Assert.False(profile.TryGetProperty(hidden, out _), hidden);

        var shyProfile = await UntilAsync(anonymous, "/api/profiles/Shy", p => p.GetProperty("private").GetBoolean());
        Assert.Equal(0, shyProfile.GetProperty("equipment").GetArrayLength());
        Assert.Equal(0, shyProfile.GetProperty("ranks").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.Http.GetAsync("/api/profiles/Nobody")).StatusCode);

        var row = Assert.Single((await anonymous.GetJsonAsync("/api/guilds")).GetProperty("guilds").EnumerateArray());
        Assert.Equal(("PGuild", "Leader", 1, 2), (row.GetProperty("guild").GetProperty("name").GetString(), row.GetProperty("leader").GetString(),
            row.GetProperty("members").GetInt32(), row.GetProperty("territories").GetInt32()));
        var page = await anonymous.GetJsonAsync($"/api/guilds/{guild}");
        Assert.Equal(["Leader"], page.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("name").GetString()));
        Assert.Equal(["Test Capital", "Test Stronghold 1"], page.GetProperty("zones").EnumerateArray().Select(z => z.GetProperty("name").GetString()));
        Assert.Equal(["Test Keep"], page.GetProperty("castles").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.Http.GetAsync("/api/guilds/999999")).StatusCode);

        var map = await anonymous.GetJsonAsync("/api/territory");
        var zones = map.GetProperty("zones").EnumerateArray().ToList();
        Assert.Equal(["Capital", "Region", "Stronghold", "Field"], zones.Select(z => z.GetProperty("kind").GetString()));
        Assert.Equal([guild, 0, guild, 0], zones.Select(z => z.GetProperty("owner") is { ValueKind: JsonValueKind.Object } o ? o.GetProperty("id").GetInt64() : 0));
        var keep = map.GetProperty("castles").EnumerateArray().Single(c => c.GetProperty("city").GetInt32() == 1001);
        Assert.Equal(("Test Keep", "Main city", guild), (keep.GetProperty("name").GetString(), keep.GetProperty("kind").GetString(),
            keep.GetProperty("defender").GetProperty("id").GetInt64()));
        Assert.Equal("Test Prefecture 1", map.GetProperty("castles").EnumerateArray().Single(c => c.GetProperty("city").GetInt32() == 1101).GetProperty("name").GetString());

        foreach (string path in new[] { "/profiles/Leader", "/guilds", $"/guilds/{guild}", "/territory", "/market", "/market/3990001" })
            Assert.Equal(HttpStatusCode.OK, (await anonymous.Http.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task TheMarketShowsOffersSalesAndPricesAndItemTrackingFlagsADuplicate()
    {
        using var admin = await AdminAsync();
        long bagGuid;
        await using (var trader = await _host.InWorldAsync("Merchant"))
            await Gm(admin, "Merchant", "!item 3990001 3");
        long role = (await _host.Service<RoleStore>().ForAccountAsync("merchant")).Single().Id;
        var deadline = DateTime.UtcNow + Wait;
        List<InvItem> saved;
        while ((saved = await _host.Service<AccountStore>().ReadAsync<List<InvItem>>(new ItemReadPlan(role))).All(i => i.Entry != 3990001) && DateTime.UtcNow < deadline) await Task.Delay(100);
        bagGuid = saved.Single(i => i.Entry == 3990001).Guid;

        // The exchange's records as TradeService writes them (market_record JSON): one listing whose escrowed instance
        // carries the guid of the bag's stack (more than the ledger let in), and two sales on different UTC days (two days and one hour back, so never the same day).
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var listing = new TradeService.Listing
        {
            Sku = "sku-1", Spu = "spu-1", SellerId = role, Entry = 3990001, Currency = 1, UnitPrice = 120, Quantity = 2,
            SaleTimeMs = now - 60_000, ExpiresAtMs = now + 86_400_000, Item = new InvItem { Guid = bagGuid, Entry = 3990001, Stack = 2 },
        };
        var sales = new[]
        {
            new TradeService.Sale { Id = "sale-1", Entry = 3990001, Quantity = 4, UnitPrice = 90, TimeMs = now - 2 * 86_400_000, SellerId = role },
            new TradeService.Sale { Id = "sale-2", Entry = 3990001, Quantity = 1, UnitPrice = 110, TimeMs = now - 3_600_000, SellerId = role },
        };
        var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_host.DataDir, "ro3emu.db"), Pooling = false, DefaultTimeout = 10 };
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(connection.ToString()))
        {
            db.Open();
            void Put(string kind, string id, object value)
            {
                using var insert = db.CreateCommand();
                insert.CommandText = "INSERT INTO market_record(kind,id,data) VALUES($k,$i,$d)";
                insert.Parameters.AddWithValue("$k", kind);
                insert.Parameters.AddWithValue("$i", id);
                insert.Parameters.AddWithValue("$d", JsonSerializer.Serialize(value, value.GetType()));
                insert.ExecuteNonQuery();
            }
            Put(TradeService.Listings, listing.Sku, listing);
            foreach (var sale in sales) Put(TradeService.History, sale.Id, sale);
        }

        using var anonymous = new PanelBrowser(_panel);
        var market = await anonymous.GetJsonAsync("/api/market?q=jellopy");
        var offer = Assert.Single(market.GetProperty("offers").EnumerateArray());
        Assert.Equal((3990001L, 1, 2L, 120L, 110L), (offer.GetProperty("item").GetProperty("id").GetInt64(), offer.GetProperty("offers").GetInt32(),
            offer.GetProperty("quantity").GetInt64(), offer.GetProperty("lowest").GetInt64(), offer.GetProperty("lastPrice").GetInt64()));
        var traded = Assert.Single(market.GetProperty("mostTraded").EnumerateArray());
        Assert.Equal((5L, 94L), (traded.GetProperty("quantity").GetInt64(), traded.GetProperty("average").GetInt64()));
        Assert.Equal(0, (await anonymous.GetJsonAsync("/api/market?q=blade")).GetProperty("total").GetInt32());

        var item = await anonymous.GetJsonAsync("/api/market/3990001");
        var listed = Assert.Single(item.GetProperty("offers").EnumerateArray());
        Assert.Equal(("Exchange", ""), (listed.GetProperty("where").GetString(), listed.GetProperty("seller").GetString())); // anonymous, as in the game
        Assert.Equal([110L, 90L], item.GetProperty("sales").EnumerateArray().Select(s => s.GetProperty("unitPrice").GetInt64()));
        Assert.Equal(2, item.GetProperty("daily").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.Http.GetAsync("/api/market/1234")).StatusCode);

        // Item tracking is for admins only.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Http.GetAsync("/api/admin/items/3990001/trace")).StatusCode);
        using var player = new PanelBrowser(_panel);
        Assert.Equal(HttpStatusCode.OK, (await player.LoginAsync("merchant", "pw")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await player.Http.GetAsync("/api/admin/items/dupes")).StatusCode);

        var trace = await admin.GetJsonAsync("/api/admin/items/3990001/trace");
        Assert.Equal(5, trace.GetProperty("held").GetInt64());
        Assert.Equal(["Bag", "Exchange"], trace.GetProperty("holders").EnumerateArray().Select(h => h.GetProperty("place").GetString()).Order());
        Assert.All(trace.GetProperty("holders").EnumerateArray(), h => Assert.Equal("Merchant", h.GetProperty("owner").GetString()));
        var kinds = trace.GetProperty("flags").EnumerateArray().Select(f => f.GetProperty("kind").GetString()).ToList();
        // One seller's bag stack and an exchange listing with its guid is what a partial listing looks like, so the shared
        // instance alone flags nothing (AntiCheatPanelTests has the two-owner case); the count does.
        Assert.DoesNotContain("Instance seen twice", kinds);
        var ledger = trace.GetProperty("ledger");
        // The ledger saw the GM grant of 3; the listing's 2 came from nowhere it recorded.
        Assert.Contains(ledger.GetProperty("entries").GetInt32() > 0 ? "Exceeds ledger" : "Unrecorded", kinds);
        Assert.True(trace.GetProperty("recent").GetArrayLength() <= 25);

        var report = await admin.GetJsonAsync("/api/admin/items/dupes");
        Assert.Contains(report.GetProperty("items").EnumerateArray(), r => r.GetProperty("item").GetProperty("id").GetInt64() == 3990001);
        Assert.Equal(HttpStatusCode.OK, (await admin.Http.GetAsync("/admin/items/3990001")).StatusCode);
    }

    [Fact]
    public async Task ANewCharactersStarterKitIsInTheLedgerAndOlderKitsAreCounted()
    {
        await using (var newcomer = await _host.InWorldAsync("Newcomer")) { }
        var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_host.DataDir, "ro3emu.db"), Pooling = false, DefaultTimeout = 10 };
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(connection.ToString());
        db.Open();
        // The kit's items and Zeny are starter grants (EconomyReason.StarterGrant source legs) of one receipt.
        var granted = new Dictionary<long, long>();
        using (var legs = db.CreateCommand())
        {
            legs.CommandText = "SELECT asset_id,SUM(delta) FROM economy_entry WHERE reason_code=$reason AND leg_kind=$source GROUP BY asset_id";
            legs.Parameters.AddWithValue("$reason", (int)Ro3Emu.Server.Economy.EconomyReason.StarterGrant);
            legs.Parameters.AddWithValue("$source", (int)Ro3Emu.Server.Economy.EconomyLegKind.Source);
            using var rows = legs.ExecuteReader();
            while (rows.Read()) granted[rows.GetInt64(0)] = rows.GetInt64(1);
        }
        Assert.Equal(CharacterService.StarterKit.ToDictionary(k => (long)k.Entry, k => (long)k.Count), granted);
        var accounts = _host.Service<AccountStore>();
        var unrecorded = new PanelUnrecordedStarterKitsRead(CharacterService.StarterKitEconomyKind);
        Assert.Equal(0L, await accounts.ReadAsync<long>(unrecorded));

        // A character that got its kit before kits were recorded (no starter receipt) is counted, so the duplication
        // check explains its kit.
        using (var forget = db.CreateCommand())
        {
            forget.CommandText = "UPDATE operation_receipt SET command_kind='test/older' WHERE command_kind=$kind";
            forget.Parameters.AddWithValue("$kind", CharacterService.StarterKitEconomyKind);
            Assert.Equal(1, forget.ExecuteNonQuery());
        }
        Assert.Equal(1L, await accounts.ReadAsync<long>(unrecorded));
    }

    [Fact]
    public void AnAchievementCategorysLevelReadsAsALevel()
    {
        Assert.Equal("Level 1", Ro3Emu.Server.Http.PanelBackend.LevelName("Growth", "Growth1"));
        Assert.Equal("Level 12", Ro3Emu.Server.Http.PanelBackend.LevelName("Server", "Server 12"));
        Assert.Equal("Master Explorer", Ro3Emu.Server.Http.PanelBackend.LevelName("Growth", "Master Explorer"));
        Assert.Equal("Growth", Ro3Emu.Server.Http.PanelBackend.LevelName("Growth", "Growth"));
    }
}
