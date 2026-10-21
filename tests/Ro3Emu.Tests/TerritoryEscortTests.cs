using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Two guilds trade escrow and escort one server-owned cart over the game gate.</summary>
public sealed class TerritoryEscortTests : IAsyncLifetime
{
    private sealed class EscortErrors : ILoggerProvider, ILogger
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Messages = new();
        public ILogger CreateLogger(string _) => this;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
            Func<TState, Exception?, string> format)
        { if (level >= LogLevel.Error) Messages.Enqueue(format(state, error) + " " + error); }
        public void Dispose() { }
    }
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-escort-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private sealed class Clock : TimeProvider
    {
        private long _seconds = 1779189400; // TerritoryWarStageConfig 1: escort trading, absolute UTC.
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public void Set(long t) => Interlocked.Exchange(ref _seconds, t);
    }
    private readonly Clock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);
    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All()
        .Single(s => s.Role?.Id == (long)bot.Self!.Uuid);
    private long SqlCount(string sql, string source)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = Service<AccountStore>().DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString());
        db.Open();
        using var query = db.CreateCommand();
        query.CommandText = sql;
        query.Parameters.AddWithValue("$source", source);
        return (long)query.ExecuteScalar()!;
    }

    private int DurablePickedCargo()
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = Service<AccountStore>().DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString());
        db.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT state FROM territory_escort WHERE id=1";
        using var json = System.Text.Json.JsonDocument.Parse((byte[])query.ExecuteScalar()!);
        return json.RootElement.GetProperty("Cargo").EnumerateObject()
            .Count(entry => entry.Value.GetProperty("Picked").GetBoolean());
    }

    public Task InitializeAsync() => StartAsync();
    private async Task StartAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
            o.PlayBook = 101; o.SeasonStart = "2026-05-19";
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private static GuildCreateReq Create(string name) => new()
    {
        Name = name, Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 },
    };

    private async Task<long> Guild(BotClient bot, string name)
    {
        await Gm(bot, "!lvl 21");
        await bot.SendGameAsync(GuildService.Create, Create(name));
        Assert.Equal(0, (await Next(bot, GuildService.Create, GuildCreateResp.Parser)).Result);
        return (await Next(bot, GuildService.Full, GuildFullInfoNtf.Parser, g => g.Guid > 0)).Guid;
    }

    private async Task Warp(BotClient bot, int scene, float? x = null, float? z = null)
    {
        await Gm(bot, x.HasValue && z.HasValue
            ? $"!warp {scene} {x.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)} {z.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : $"!warp {scene}");
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, x => x.SceneEntry == scene);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
        await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, x => x.SceneId == scene);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
    }

    [ClientDataFact]
    public async Task TwoGuildsTradeThenEscortWithConservedGoodsAndReplayRefusals()
    {
        await using var seller = await InWorld("EscortSeller");
        await using var buyer = await InWorld("EscortBuyer");
        await using var outsider = await InWorld("EscortOther");
        long sellId = await Guild(seller, "SellCo");
        long buyId = await Guild(buyer, "BuyCo");
        var store = Service<GuildStore>();
        var gold = store.Get(buyId)!; gold.Money = 2000; Assert.True(store.Save(gold));
        var escort = Service<TerritoryEscortService>();
        await escort.CreditGuildGoodsAsync(sellId, 100, "test:escort:market:initial-stock");
        await seller.SendGameAsync(32287, new GetGuildItemDataReq());
        var itemData = await Next(seller, 32287, GetGuildItemDataResp.Parser);
        Assert.Equal(100, itemData.Info.Items[1258000].Stack);

        await outsider.SendGameAsync(TerritoryEscortService.Sell, new TerritoryWarsEscortGuildSellReq { SellingNum = 20, UnitPrice = 10 });
        Assert.Equal(ErrorCodes.NotInGuild, (await Next(outsider, TerritoryEscortService.Sell, TerritoryWarsEscortGuildSellRes.Parser)).Result);
        await seller.SendGameAsync(TerritoryEscortService.Sell, new TerritoryWarsEscortGuildSellReq { SellingNum = 40, UnitPrice = 10 });
        var listed = await Next(seller, TerritoryEscortService.Sell, TerritoryWarsEscortGuildSellRes.Parser);
        Assert.Equal(0, listed.Result);
        Assert.Equal(60, escort.GetGuildGoods(sellId));
        await seller.SendGameAsync(32287, new GetGuildItemDataReq());
        Assert.Equal(60, (await Next(seller, 32287, GetGuildItemDataResp.Parser)).Info.Items[1258000].Stack);
        await seller.SendGameAsync(TerritoryEscortService.Buy, new TerritoryWarsEscortGuildBuyReq { GoodsUid = listed.GoodsUid });
        Assert.NotEqual(0, (await Next(seller, TerritoryEscortService.Buy, TerritoryWarsEscortGuildBuyRes.Parser)).Result);
        await buyer.SendGameAsync(TerritoryEscortService.Buy, new TerritoryWarsEscortGuildBuyReq { GoodsUid = listed.GoodsUid, Message = "Ready" });
        Assert.Equal(0, (await Next(buyer, TerritoryEscortService.Buy, TerritoryWarsEscortGuildBuyRes.Parser)).Result);
        await buyer.SendGameAsync(TerritoryEscortService.Buy, new TerritoryWarsEscortGuildBuyReq { GoodsUid = listed.GoodsUid });
        Assert.NotEqual(0, (await Next(buyer, TerritoryEscortService.Buy, TerritoryWarsEscortGuildBuyRes.Parser, x => x.Result != 0)).Result);
        await seller.SendGameAsync(TerritoryEscortService.Operate, new TerritoryWarsEscortOperateApplyReq
        { GoodsUid = listed.GoodsUid, TargetGuildId = buyId, IsAccept = true });
        Assert.Equal(0, (await Next(seller, TerritoryEscortService.Operate, TerritoryWarsEscortOperateApplyRes.Parser)).Result);
        Assert.Equal(1600, store.Get(buyId)!.Money);
        Assert.Equal(400, store.Get(sellId)!.Money);
        Assert.Equal(60, escort.GetGuildGoods(sellId));
        Assert.Equal(40, escort.GetGuildGoods(buyId));
        Assert.Equal(1, SqlCount("SELECT count(*) FROM operation_receipt WHERE command_kind=$source AND length(outcome)>0", "event/escort-trade"));
        Assert.Equal(1, SqlCount("SELECT count(*) FROM economy_entry WHERE asset_kind=3 AND asset_id=1 AND delta=-400 AND operation_id IN (SELECT operation_id FROM operation_receipt WHERE command_kind=$source)", "event/escort-trade"));
        Assert.Equal(1, SqlCount("SELECT count(*) FROM economy_entry WHERE asset_kind=3 AND asset_id=1 AND delta=400 AND operation_id IN (SELECT operation_id FROM operation_receipt WHERE command_kind=$source)", "event/escort-trade"));
        Assert.Equal(1, SqlCount("SELECT count(*) FROM economy_entry WHERE asset_kind=4 AND delta=-40 AND operation_id IN (SELECT operation_id FROM operation_receipt WHERE command_kind=$source)", "event/escort-trade"));
        Assert.Equal(1, SqlCount("SELECT count(*) FROM economy_entry WHERE asset_kind=4 AND delta=40 AND operation_id IN (SELECT operation_id FROM operation_receipt WHERE command_kind=$source)", "event/escort-trade"));
        await seller.SendGameAsync(TerritoryEscortService.Operate, new TerritoryWarsEscortOperateApplyReq
        { GoodsUid = listed.GoodsUid, TargetGuildId = buyId, IsAccept = true });
        Assert.NotEqual(0, (await Next(seller, TerritoryEscortService.Operate, TerritoryWarsEscortOperateApplyRes.Parser, x => x.Result != 0)).Result);
        Assert.Equal(1600, store.Get(buyId)!.Money);

        _clock.Set(1779190300); // configured escort playing window.
        await escort.ReconcileAsync();
        await Warp(buyer, 30303, 294.86f, 258.95f);
        await Service<GameLoop>().InvokeAsync(() => { Session(buyer).Dead = true; return Task.CompletedTask; });
        await buyer.SendGameAsync(TerritoryEscortService.Cart, new TerritoryWarsEscortCartReq { CartType = 0, PointId = 3001 });
        Assert.Equal(ErrorCodes.FeatureUnavailable,
            (await Next(buyer, TerritoryEscortService.Cart, TerritoryWarsEscortCartRes.Parser)).Result);
        await Service<GameLoop>().InvokeAsync(() => { Session(buyer).Dead = false; return Task.CompletedTask; });
        await buyer.SendGameAsync(TerritoryEscortService.Cart, new TerritoryWarsEscortCartReq { CartType = 0, PointId = 3001 });
        var cart = await Next(buyer, TerritoryEscortService.Cart, TerritoryWarsEscortCartRes.Parser);
        Assert.Equal(0, cart.Result);
        Assert.Equal(40, cart.TransportNum);
        Assert.Equal(0, escort.GetGuildGoods(buyId));
        await buyer.SendGameAsync(TerritoryEscortService.Cart, new TerritoryWarsEscortCartReq { CartType = 0, PointId = 3001 });
        Assert.NotEqual(0, (await Next(buyer, TerritoryEscortService.Cart, TerritoryWarsEscortCartRes.Parser, x => x.Result != 0)).Result);
        await Warp(buyer, 30312, 273.29f, 392.25f);
        _clock.Set(1779190310);
        await Service<GameLoop>().InvokeAsync(() => { Session(buyer).Dead = true; return Task.CompletedTask; });
        await buyer.SendGameAsync(TerritoryEscortService.Submit, new TerritoryWarsEscortSubmitReq { CartId = cart.CartId, PointId = 12001 });
        await buyer.SendGameAsync(TerritoryEscortService.GuildPlay, new TerritoryWarsEscortGuildPlayInfoReq());
        Assert.Equal(0, (await Next(buyer, TerritoryEscortService.GuildPlay, TerritoryWarsEscortGuildPlayInfoRes.Parser)).PlayInfo.TransportNum);
        await Service<GameLoop>().InvokeAsync(() => { Session(buyer).Dead = false; return Task.CompletedTask; });
        await buyer.SendGameAsync(TerritoryEscortService.Submit, new TerritoryWarsEscortSubmitReq { CartId = cart.CartId, PointId = 12001 });
        Assert.Equal(cart.CartId, (await Next(buyer, TerritoryEscortService.SuccessNtf, TerritoryWarsEscortSuccessNtf.Parser)).CartId);
        Assert.Equal(40, escort.GetGuildEscortScore(buyId, 1));
        Assert.Equal(1, SqlCount("SELECT count(*) FROM operation_receipt WHERE command_kind=$source", "event/escort-cart-submit"));
        await buyer.SendGameAsync(TerritoryEscortService.Submit, new TerritoryWarsEscortSubmitReq { CartId = cart.CartId, PointId = 12001 });
        await buyer.SendGameAsync(TerritoryEscortService.GuildPlay, new TerritoryWarsEscortGuildPlayInfoReq());
        Assert.Equal(40, (await Next(buyer, TerritoryEscortService.GuildPlay, TerritoryWarsEscortGuildPlayInfoRes.Parser)).PlayInfo.TransportNum);
        Assert.Equal(0, escort.GetGuildGoods(buyId));
    }

    [ClientDataFact]
    public async Task HeldProducerCommitPublishesNoGoodsOrReceiptAndOtherRoleContinues()
    {
        await using var leader = await InWorld("HeldLeader");
        await using var other = await InWorld("HeldOther");
        long guild = await Guild(leader, "HeldCo");
        // EnterWorld sees the self end-frame before SceneLoaded finishes its realm-scoped publication.
        // This same-connection roundtrip confirms that the prior handler has completed before holding the producer.
        await other.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = long.MaxValue, ItemCount = 1 });
        Assert.Equal(ErrorCodes.ItemMissing, (await Next(other, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        var escort = Service<TerritoryEscortService>();
        var accounts = Service<AccountStore>();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        accounts.BeforeOwnedCommitForTest = async label =>
        {
            if (label == "economy/event/escort-producer-stock") { reached.TrySetResult(); await release.Task; }
        };
        var credit = escort.CreditGuildGoodsAsync(guild, 7, "test:escort:held-source");
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal(0, escort.GetGuildGoods(guild));
            Assert.Equal(0, SqlCount("SELECT count(*) FROM operation_source WHERE source_key=$source", "test:escort:held-source"));
            await other.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = long.MaxValue, ItemCount = 1 });
            Assert.Equal(ErrorCodes.ItemMissing,
                (await Next(other, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        }
        finally
        {
            release.TrySetResult();
            accounts.BeforeOwnedCommitForTest = null;
        }
        await credit.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(7, escort.GetGuildGoods(guild));
        Assert.Equal(1, SqlCount("SELECT count(*) FROM operation_source WHERE source_key=$source", "test:escort:held-source"));
    }

    [ClientDataFact]
    public async Task ProducerHasOneReceiptAndNaturalSourceAfterUnrelatedCreditAndRestart()
    {
        await using (var leader = await InWorld("SourceLeader"))
        {
            long guild = await Guild(leader, "SrcCo");
            var escort = Service<TerritoryEscortService>();
            var catalog = EventEconomy.Catalog(Service<GameData>());
            var database = Service<AccountStore>().DatabasePath;
            var opening = EconomySnapshotReader.Read(database, catalog, "auto");
            await escort.CreditGuildGoodsAsync(guild, 20, "test:escort:once");
            await escort.CreditGuildGoodsAsync(guild, 5, "test:escort:other");
            var closing = EconomySnapshotReader.Read(database, catalog, "auto");
            var ledger = EconomyLedgerReader.Read(database, catalog, "auto", opening.Cut.CommitSequence!.Value,
                closing.Cut.CommitSequence!.Value);
            Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues));
            var exactness = EconomyExactnessChecker.Check(opening, closing, ledger);
            Assert.All(exactness.Assets, asset => Assert.Equal("0", asset.Residual));
            Assert.All(exactness.Locations, location => Assert.Equal("0", location.Residual));
            Assert.DoesNotContain(exactness.Issues, issue => issue.Kind is EconomyIssueKind.Corruption or EconomyIssueKind.UnknownSchema);
            Assert.Equal(25, escort.GetGuildGoods(guild));
            Assert.Equal(1, SqlCount("SELECT count(*) FROM operation_source WHERE source_key=$source", "test:escort:once"));
            Assert.Equal(1, SqlCount("SELECT count(*) FROM operation_receipt WHERE source_key=$source AND command_kind='event/escort-producer-stock'", "test:escort:once"));
            Assert.Equal(1, SqlCount("SELECT count(*) FROM economy_entry WHERE source_key=$source AND asset_kind=4 AND delta=20", "test:escort:once"));
            await Assert.ThrowsAsync<EconomyOperationConflictException>(() =>
                escort.CreditGuildGoodsAsync(guild, 20, "test:escort:once"));
            Assert.Equal(25, escort.GetGuildGoods(guild));
        }
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await StartAsync();
        await using var restored = await InWorld("SourceLeader", create: false);
        var current = Service<TerritoryEscortService>();
        long id = Service<GuildStore>().ForRole((long)restored.Self!.Uuid)!.Id;
        Assert.Equal(25, current.GetGuildGoods(id));
        await Assert.ThrowsAsync<EconomyOperationConflictException>(() =>
            current.CreditGuildGoodsAsync(id, 20, "test:escort:once"));
        Assert.Equal(25, current.GetGuildGoods(id));
        Assert.Equal(1, SqlCount("SELECT count(*) FROM operation_source WHERE source_key=$source", "test:escort:once"));
    }

    [ClientDataFact]
    public async Task EscrowAndListsSurviveServerRestart()
    {
        await using (var seller = await InWorld("RestartSeller"))
        {
            long id = await Guild(seller, "RestCo");
            var escort = Service<TerritoryEscortService>(); await escort.CreditGuildGoodsAsync(id, 30, "test:escort:restart:initial-stock");
            await seller.SendGameAsync(TerritoryEscortService.Sell, new TerritoryWarsEscortGuildSellReq { SellingNum = 20, UnitPrice = 5 });
            Assert.Equal(0, (await Next(seller, TerritoryEscortService.Sell, TerritoryWarsEscortGuildSellRes.Parser)).Result);
            Assert.Equal(10, escort.GetGuildGoods(id));
        }
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await StartAsync();
        await using var restored = await InWorld("RestartSeller", create: false);
        var full = await Next(restored, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid > 0);
        Assert.Equal(10, full.Items.Items[1258000].Stack);
        await restored.SendGameAsync(TerritoryEscortService.SellOrders, new TerritoryWarsEscortGuildSellOrderListReq());
        var orders = await Next(restored, TerritoryEscortService.SellOrders, TerritoryWarsEscortGuildSellOrderListRes.Parser);
        var offer = Assert.Single(orders.Orders);
        Assert.Equal(20, offer.SellingNum);
        var service = Service<TerritoryEscortService>();
        Assert.Equal(10, service.GetGuildGoods(offer.GuildId));
        await restored.SendGameAsync(TerritoryEscortService.TakeDown, new TerritoryWarsEscortGuildTakeDownReq { GoodsUid = offer.GoodsUid });
        Assert.Equal(0, (await Next(restored, TerritoryEscortService.TakeDown, TerritoryWarsEscortGuildTakeDownRes.Parser)).Result);
        Assert.Equal(30, service.GetGuildGoods(offer.GuildId));
        await restored.SendGameAsync(TerritoryEscortService.TakeDown, new TerritoryWarsEscortGuildTakeDownReq { GoodsUid = offer.GoodsUid });
        Assert.NotEqual(0, (await Next(restored, TerritoryEscortService.TakeDown, TerritoryWarsEscortGuildTakeDownRes.Parser, x => x.Result != 0)).Result);
        Assert.Equal(30, service.GetGuildGoods(offer.GuildId));
    }

    [ClientDataFact]
    public async Task QualifiedGuildGetsOneFiniteCartLoadPerRoundAcrossReconcileAndRestart()
    {
        await using var leader = await InWorld("SupplyLeader");
        long id = await Guild(leader, "SupCo");
        var guilds = Service<GuildStore>();
        var roles = Service<RoleStore>();
        var guild = guilds.Get(id)!;
        guild.CreateTime = 1779189299_000;
        for (int i = 0; i < 19; i++)
        {
            var member = roles.Create("territory-escort-fixture", 1, "Member" + i, 1, 1000, 1)!;
            guild.Members[member.Id] = new GuildStore.Member
            {
                RoleId = member.Id,
                Name = member.Name, Official = 20
            };
        }
        Assert.True(guilds.Save(guild));
        var escort = Service<TerritoryEscortService>();
        await escort.ReconcileAsync();
        Assert.Equal(12_000, escort.GetGuildGoods(id));
        long stockReceipts = SqlCount("SELECT count(*) FROM operation_receipt WHERE command_kind=$source", "event/escort-reconcile");
        await escort.CreditGuildGoodsAsync(id, 5, "test:escort:stock:unrelated");
        await escort.ReconcileAsync();
        Assert.Equal(12_005, escort.GetGuildGoods(id));
        Assert.Equal(stockReceipts, SqlCount("SELECT count(*) FROM operation_receipt WHERE command_kind=$source", "event/escort-reconcile"));
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await StartAsync();
        escort = Service<TerritoryEscortService>();
        await escort.ReconcileAsync();
        Assert.Equal(12_005, escort.GetGuildGoods(id));
        Assert.Equal(stockReceipts, SqlCount("SELECT count(*) FROM operation_receipt WHERE command_kind=$source", "event/escort-reconcile"));
        var seasons = Service<SeasonService>();
        Assert.StartsWith("Playbook 102", seasons.SetPlayBook(102));
        await escort.ReconcileAsync();
        Assert.Equal(0, escort.GetGuildGoods(id));
        Assert.StartsWith("Playbook 101", seasons.SetPlayBook(101));
        await escort.ReconcileAsync();
        Assert.Equal(12_005, escort.GetGuildGoods(id));
    }

    [ClientDataFact]
    public async Task EnemyDestroysCartAndCollectsFiniteCargoOnce()
    {
        var errors = new EscortErrors();
        Service<ILoggerFactory>().AddProvider(errors);
        await using var owner = await InWorld("CargoOwner");
        await using var raider = await InWorld("CargoRaider");
        long ownerGuild = await Guild(owner, "CargoA");
        long raiderGuild = await Guild(raider, "CargoB");
        var escort = Service<TerritoryEscortService>();
        await escort.CreditGuildGoodsAsync(ownerGuild, 3000, "test:escort:cargo:initial-stock");
        _clock.Set(1779190300);
        await escort.ReconcileAsync();
        await Warp(owner, 30303, 294.86f, 258.95f);
        await Warp(raider, 30303, 294.86f, 258.95f);
        await owner.SendGameAsync(TerritoryEscortService.Cart, new TerritoryWarsEscortCartReq { CartType = 0, PointId = 3001 });
        var cart = await Next(owner, TerritoryEscortService.Cart, TerritoryWarsEscortCartRes.Parser);
        Assert.Equal(0, cart.Result);
        await Next(raider, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(e => e.EntityTemplateID == 4910001));
        var unit = Service<World>().AllUnits().OfType<MonsterUnit>()
            .Single(m => m.Uid == ((1L << 43) | cart.CartId));
        unit.Hp = 1; // The wire cast below still goes through authoritative combat and kill callbacks.
        await raider.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = unit.Uid });
        Assert.Equal(0, (await Next(raider, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var cargoView = await Next(raider, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(e => e.EntityTemplateID == 4910003));
        for (int i = 0; i < 50 && Service<World>().AllUnits().Count(u => u.Entity().EntityTemplateID == 4910003) < 3; i++)
            await Task.Delay(20);
        Assert.Equal(3, Service<World>().AllUnits().Count(u => u.Entity().EntityTemplateID == 4910003));
        var changed = await Next(raider, TerritoryEscortService.CartVarNtf, TerritoryWarsEscortCartInfoChange.Parser,
            x => x.Uuid == cart.CartId && x.CartVarinfo.CurNum == 1200);
        Assert.Equal(1200, changed.CartVarinfo.CurNum);
        long cargoUid = (long)cargoView.Bases.First(e => e.EntityTemplateID == 4910003).Uuid;
        await raider.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        { NpcUid = cargoUid, InteractionId = 491000301, SceneConfigId = 30303 });
        try { Assert.Equal(0, (await Next(raider, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result); }
        catch (Exception error) { throw new InvalidOperationException(string.Join("\n", errors.Messages), error); }
        Assert.Equal(1, Service<CharacterService>().CountOf(Session(raider), 1258002));
        Assert.Equal(1, SqlCount("SELECT count(*) FROM operation_receipt WHERE command_kind=$source AND length(outcome)>0", "event/escort-cargo-pickup"));
        Assert.Equal(1, SqlCount("SELECT count(*) FROM economy_entry WHERE asset_kind=1 AND asset_id=1258002 AND delta=-1 AND operation_id IN (SELECT operation_id FROM operation_receipt WHERE command_kind=$source)", "event/escort-cargo-pickup"));
        Assert.Equal(1, SqlCount("SELECT count(*) FROM economy_entry WHERE asset_kind=1 AND asset_id=1258002 AND delta=1 AND operation_id IN (SELECT operation_id FROM operation_receipt WHERE command_kind=$source)", "event/escort-cargo-pickup"));
        await raider.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        { NpcUid = cargoUid, InteractionId = 491000301, SceneConfigId = 30303 });
        Assert.Equal(ErrorCodes.NpcMissing,
            (await Next(raider, SceneService.NpcInteraction, NpcInteractionRsp.Parser, x => x.Result != 0)).Result);
        Assert.Equal(1, Service<CharacterService>().CountOf(Session(raider), 1258002));
        await Warp(raider, 30312, 273.29f, 392.25f);
        var accounts = Service<AccountStore>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        accounts.BeforeCommitForTest = label =>
        {
            if (label != "economy/event/escort-drop-submit") return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("escort drop submit commit barrier");
        };
        try
        {
            await raider.SendGameAsync(TerritoryEscortService.DropSubmit, new TerritoryWarsEscortDropSubmitReq());
            Assert.True(await Task.Run(() => entered.Wait(Wait)));
            Assert.Equal(1, Service<CharacterService>().CountOf(Session(raider), 1258002));
            Assert.Equal(0, Service<GuildStore>().Get(raiderGuild)!.Materials);
            Assert.Equal(0, SqlCount("SELECT count(*) FROM operation_receipt WHERE command_kind=$source", "event/escort-drop-submit"));
        }
        finally { release.Set(); accounts.BeforeCommitForTest = null; }
        for (int i = 0; i < 50 && Service<GuildStore>().Get(raiderGuild)!.Materials != 1000; i++)
            await Task.Delay(100);
        Assert.Equal(1000, Service<GuildStore>().Get(raiderGuild)!.Materials);
        Assert.Equal(0, Service<CharacterService>().CountOf(Session(raider), 1258002));
        Assert.Equal(1, SqlCount("SELECT count(*) FROM economy_entry WHERE asset_kind=1 AND asset_id=1258002 AND delta=-1 AND operation_id IN (SELECT operation_id FROM operation_receipt WHERE command_kind=$source)", "event/escort-drop-submit"));
        Assert.Equal(1, SqlCount("SELECT count(*) FROM economy_entry WHERE asset_kind=3 AND asset_id=3 AND delta=1000 AND operation_id IN (SELECT operation_id FROM operation_receipt WHERE command_kind=$source)", "event/escort-drop-submit"));
        Assert.Equal(0, escort.GetGuildGoods(ownerGuild));
    }

    [ClientDataFact]
    public async Task HeldClaimKeepsRewardAndClaimMarkerInvisibleUntilCommit()
    {
        await using var player = await InWorld("ClaimPlayer");
        var session = Session(player);
        int round = Service<TerritoryWarsClock>().Current.Round;
        await OnLoop(() =>
        {
            session.TerritoryEscort!.ScoreByRound[round] = 3000;
            return Task.CompletedTask;
        });
        long beforeCurrency = Service<CharacterService>().CountOf(session, 1);
        var accounts = Service<AccountStore>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        accounts.BeforeCommitForTest = label =>
        {
            if (label != "economy/event/escort-claim") return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("escort claim commit barrier");
        };
        try
        {
            await player.SendGameAsync(TerritoryEscortService.Claim,
                new TerritoryWarsEscortGuildPlayerGetScoreAwardReq { AwardIndex = 0 });
            Assert.True(await Task.Run(() => entered.Wait(Wait)));
            bool claimed = false;
            await OnLoop(() =>
            {
                claimed = session.TerritoryEscort!.ClaimedByRound.GetValueOrDefault(round)?.Contains(0) == true;
                return Task.CompletedTask;
            });
            Assert.False(claimed);
            Assert.Equal(beforeCurrency, Service<CharacterService>().CountOf(session, 1));
            Assert.Equal(0, SqlCount("SELECT count(*) FROM operation_receipt WHERE command_kind=$source", "event/escort-claim"));
        }
        finally { release.Set(); accounts.BeforeCommitForTest = null; }
        for (int i = 0; i < 50 && session.TerritoryEscort!.ClaimedByRound.GetValueOrDefault(round)?.Contains(0) != true; i++)
            await Task.Delay(20);
        Assert.Contains(0, session.TerritoryEscort!.ClaimedByRound[round]);
        Assert.Equal(beforeCurrency + 5000, Service<CharacterService>().CountOf(session, 1));
        Assert.Equal(1, SqlCount("SELECT count(*) FROM operation_receipt WHERE command_kind=$source", "event/escort-claim"));
        Assert.Equal(1, SqlCount("SELECT count(*) FROM economy_entry WHERE asset_kind=2 AND asset_id=1 AND delta=5000 AND operation_id IN (SELECT operation_id FROM operation_receipt WHERE command_kind=$source)", "event/escort-claim"));
    }

    [ClientDataFact]
    public async Task CommittedCargoApplyFaultCannotResurrectClaimAcrossRestartAndOtherSave()
    {
        await using var owner = await InWorld("FaultOwner");
        await using var raider = await InWorld("FaultRaider");
        long ownerGuild = await Guild(owner, "FaultA");
        await Guild(raider, "FaultB");
        var escort = Service<TerritoryEscortService>();
        await escort.CreditGuildGoodsAsync(ownerGuild, 3000, "test:escort:fault:stock");
        _clock.Set(1779190300);
        await escort.ReconcileAsync();
        await Warp(owner, 30303, 294.86f, 258.95f);
        await Warp(raider, 30303, 294.86f, 258.95f);
        await owner.SendGameAsync(TerritoryEscortService.Cart, new TerritoryWarsEscortCartReq { CartType = 0, PointId = 3001 });
        var cart = await Next(owner, TerritoryEscortService.Cart, TerritoryWarsEscortCartRes.Parser);
        Assert.Equal(0, cart.Result);
        await Next(raider, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(e => e.EntityTemplateID == 4910001));
        var unit = Service<World>().AllUnits().OfType<MonsterUnit>()
            .Single(m => m.Uid == ((1L << 43) | cart.CartId));
        unit.Hp = 1;
        await raider.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = unit.Uid });
        Assert.Equal(0, (await Next(raider, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(raider, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(e => e.EntityTemplateID == 4910003));
        WorldUnit[] cargo = [];
        for (int i = 0; i < 50 && cargo.Length == 0; i++)
        {
            cargo = Service<World>().AllUnits().Where(u => u.Entity().EntityTemplateID == 4910003).ToArray();
            if (cargo.Length == 0) await Task.Delay(20);
        }
        Assert.NotEmpty(cargo);
        var actor = cargo[0];
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(raider),
            new MapPoint(actor.X, actor.Y, actor.Z, 0)));
        long before = SqlCount("SELECT count(*) FROM operation_source WHERE source_key LIKE $source", "escort-cargo:%:claim");
        escort.BeforeCargoApplyForTest = () => throw new IOException("injected committed cargo apply fault");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => OnLoop(async () =>
            {
                await escort.HandleSharedAsync(Session(raider), SceneService.NpcInteraction,
                    new NpcInteractionReq
                    {
                        NpcUid = actor.Uid, InteractionId = 491000301,
                        SceneConfigId = 30303
                    }.ToByteArray());
            }));
        }
        finally { escort.BeforeCargoApplyForTest = null; }
        Assert.Equal(1, DurablePickedCargo());
        Assert.Equal(before + 1, SqlCount("SELECT count(*) FROM operation_source WHERE source_key LIKE $source", "escort-cargo:%:claim"));
        Assert.DoesNotContain(Service<World>().AllUnits(), current => current.Uid == actor.Uid);
        var blocked = await Assert.ThrowsAnyAsync<Exception>(() =>
            escort.CreditGuildGoodsAsync(ownerGuild, 1, "test:escort:fault:blocked-credit"));
        Assert.True(blocked.ToString().Contains("recovery", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, DurablePickedCargo());
        Assert.Equal(0, SqlCount("SELECT count(*) FROM operation_source WHERE source_key=$source",
            "test:escort:fault:blocked-credit"));
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await StartAsync();
        Assert.Equal(1, DurablePickedCargo());
        Assert.Equal(before + 1, SqlCount("SELECT count(*) FROM operation_source WHERE source_key LIKE $source", "escort-cargo:%:claim"));
        await Service<TerritoryEscortService>().CreditGuildGoodsAsync(ownerGuild, 1, "test:escort:fault:other-credit");
        Assert.Equal(1, DurablePickedCargo());
        Assert.Equal(before + 1, SqlCount("SELECT count(*) FROM operation_source WHERE source_key LIKE $source", "escort-cargo:%:claim"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "data", "config"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository data/config");
    }
}
