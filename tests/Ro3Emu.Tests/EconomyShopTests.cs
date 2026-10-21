using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.LoadTest;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Diagnostics;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>E01 production NPC purchase: existing wire, cost/goods/quota, ledger, failures and D1.</summary>
public sealed class EconomyShopTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(RepoRoot(), ".loadtest-temp", "e01-shop-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private static readonly Lazy<EconomyCatalog> Catalog = new(() => EconomyCatalog.Load(Path.Combine(RepoRoot(), "data")));
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private string Database => Path.Combine(_directory, "ro3emu.db");

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
            o.PlayBook = 101;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task<BotClient> Enter(bool level = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync("economyshop", "pw"); await bot.EnterGateAsync();
        long role = (await bot.CreateRoleAsync("EconomyShop")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(role);
        await Next(bot, ShopService.ShopSync, DBShopInfoIncrement.Parser);
        // C/D 15: the login requests its scene's stall maintenance instead of running it; run it now, so the
        // realm's system stall receipts land before the test's opening cut as they did when the login ran it.
        await Service<GameLoop>().InvokeAsync(() => Service<StallsService>().RefreshWorldMaintenanceAsync());
        if (level) await Gm("!lvl 10");
        return bot;
    }

    private async Task Gm(string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync(_http + "/ro3emu/gm?account=economyshop", new StringContent(command))).EnsureSuccessStatusCode();
    }
    private GameSession Session() => Service<SessionRegistry>().All().Single(s => s.Account == "economyshop" && s.Role != null);
    private EconomySnapshot Cut() => S3EconomyReader.Read(_directory, Catalog.Value);
    private EconomyLedgerWindow Window(EconomySnapshot a, EconomySnapshot b) =>
        S3EconomyReader.ReadLedger(_directory, Catalog.Value, a, b)!;
    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), TimeSpan.FromSeconds(8))).Body);
    private static async Task<BuyCommodityResp> Buy(BotClient bot, int commodity = 118, int count = 1)
    {
        await bot.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = commodity, Count = count, Price = 7 });
        return await Next(bot, ShopService.BuyCommodity, BuyCommodityResp.Parser);
    }
    private static void ExactButUncovered(EconomySnapshot a, EconomySnapshot b, EconomyLedgerWindow ledger)
    {
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues.Select(x => x.Detail)));
        var check = EconomyExactnessChecker.Check(a, b, ledger);
        Assert.Equal(EconomyVerdict.NotMeasured, check.Verdict);
        Assert.DoesNotContain(check.Issues, x => x.Kind == EconomyIssueKind.Corruption);
        Assert.All(check.Assets, x => { Assert.Equal("0", x.Residual); Assert.Equal("0", x.LegResidual); });
        Assert.All(check.Locations, x => Assert.Equal("0", x.Residual));
        Assert.NotEmpty(check.UncoveredMutationFamilies);
    }

    [ClientDataFact]
    public async Task WirePurchaseRestacksPotionAndKeepsPacketsD1AndExactLedger()
    {
        await using var bot = await Enter();
        var session = Session();
        var opening = Cut();
        long oldGuid = session.Items.Single(i => i.Entry == 1000000).Guid;
        Service<ServerMetrics>().Reset();
        var result = await Buy(bot, count: 2);
        Assert.Equal((0, 118, 2, 100, 2), (result.Result, result.CommodityId, result.Count, result.Price, result.BuyTimes));
        var closing = Cut();
        var ledger = Window(opening, closing);
        var receipt = Assert.Single(ledger.Receipts);
        Assert.Equal(2, receipt.LegCount);
        var sink = Assert.Single(ledger.Legs, x => x.Kind == EconomyLegKind.Sink);
        Assert.Equal((EconomyReason.NpcShopPayment, new EconomyAsset(EconomyAssetKind.Currency, 1), -200L),
            (sink.Reason, sink.Asset, sink.Delta));
        var source = Assert.Single(ledger.Legs, x => x.Kind == EconomyLegKind.Source);
        Assert.Equal((EconomyReason.NpcShopGoods, 1000000L, 2L), (source.Reason, source.Asset.Id, source.Delta));
        Assert.Equal(oldGuid.ToString(System.Globalization.CultureInfo.InvariantCulture), source.Location.Instance);
        Assert.Equal(22, session.Items.Single(i => i.Guid == oldGuid).Stack);
        Assert.Equal(2, JsonSerializer.Deserialize<ShopService.ShopState>(Service<RoleStore>().LoadShop(session.Role!.Id)!)!.Bought[118].Count);
        Assert.NotNull(EconomyReceiptReader.Read(Database, receipt.OperationId));
        ExactButUncovered(opening, closing, ledger);
        var metrics = JsonSerializer.SerializeToElement(Service<ServerMetrics>().Snapshot());
        Assert.Equal(0, metrics.GetProperty("database").GetProperty("loopStatements").GetInt64());
        var access = JsonSerializer.SerializeToElement(Service<AccountStore>().AccessMetrics.Snapshot());
        Assert.Equal(0, access.GetProperty("nonWriterWriteAttempts").GetInt64());
        Assert.Equal(0, access.GetProperty("writeGuardErrors").GetInt64());
    }

    [ClientDataFact]
    public async Task EqualWirePurchasesAreDistinctAndRefusalsNeverCreateReceipts()
    {
        await using var bot = await Enter(level: false);
        var opening = Cut();
        Assert.Equal(ErrorCodes.ShopClosed, (await Buy(bot)).Result);
        Assert.Equal(ErrorCodes.ShopNoConfig, (await Buy(bot, -99)).Result);
        Assert.Equal(opening.Cut.CommitSequence, Cut().Cut.CommitSequence);
        await Gm("!lvl 10");
        opening = Cut();
        Assert.Equal(0, (await Buy(bot)).Result);
        Assert.Equal(0, (await Buy(bot)).Result);
        Assert.Equal(ErrorCodes.Parameter, (await Buy(bot, count: 0)).Result);
        Assert.Equal(ErrorCodes.CurrencyShort, (await Buy(bot, count: 1_000_000)).Result);
        var closing = Cut();
        var ledger = Window(opening, closing);
        Assert.Equal(2, ledger.Receipts.Count);
        Assert.Equal(2, ledger.Receipts.Select(x => x.OperationId).Distinct().Count());
        Assert.Equal(4, ledger.Legs.Count);
        ExactButUncovered(opening, closing, ledger);
    }

    [ClientDataFact]
    public async Task BatchPreservesPartialSuccessAndOneReceiptPerSuccessfulChild()
    {
        await using var bot = await Enter();
        var opening = Cut();
        await bot.SendGameAsync(ShopService.BatchBuyCommodity, new BatchBuyCommodityReq
        {
            Items = { new BatchBuyCommodityItem { CommodityId = 118, Count = 1 },
                new BatchBuyCommodityItem { CommodityId = 118, Count = 100_000 },
                new BatchBuyCommodityItem { CommodityId = 118, Count = 2 } }
        });
        var response = await Next(bot, ShopService.BatchBuyCommodity, BatchBuyCommodityResp.Parser);
        Assert.Equal(ErrorCodes.CurrencyShort, response.Result);
        Assert.Equal(new[] { 0, ErrorCodes.CurrencyShort, 0 }, response.Results.Select(x => x.Result));
        Assert.Equal(new[] { 1, 1, 3 }, response.Results.Select(x => x.BuyTimes));
        var closing = Cut();
        var ledger = Window(opening, closing);
        Assert.Equal(2, ledger.Receipts.Count);
        Assert.Equal(4, ledger.Legs.Count);
        Assert.Equal(-300, ledger.Legs.Where(x => x.Kind == EconomyLegKind.Sink).Sum(x => x.Delta));
        Assert.Equal(3, ledger.Legs.Where(x => x.Kind == EconomyLegKind.Source).Sum(x => x.Delta));
        ExactButUncovered(opening, closing, ledger);
    }

    [ClientDataFact]
    public async Task LastLedgerInsertFailureRollsBackQuotaCurrencyItemsAndRevisions()
    {
        await using var bot = await Enter();
        var session = Session();
        var opening = Cut();
        long balance = Service<CharacterService>().CountOf(session, 1);
        FixtureSql.Execute(Service<AccountStore>(), """
            CREATE TRIGGER fail_last_economy_leg BEFORE INSERT ON economy_entry
            WHEN NEW.leg_no=1 BEGIN SELECT RAISE(ABORT,'last ledger leg'); END
            """);
        await Service<GameLoop>().InvokeAsync(() => Service<OwnerRegistry>().RunSessionAsync(
            "dispatcher", [ReservationKey.Role(session.Role!.Id)], session, async () =>
            {
                // Capture/assert under the same role reservation as the failed purchase.
                var revisions = Service<RoleStore>().ReadRevisionVector(session.Role!.Id);
                var failure = await Assert.ThrowsAsync<SqliteException>(() => Service<ShopService>().BuyAsync(session,
                    new BuyCommodityReq { CommodityId = 114, Count = 1 }));
                Assert.Contains("last ledger leg", failure.Message);
                var current = Service<RoleStore>().ReadRevisionVector(session.Role.Id);
                foreach (string part in new[] { "progress", "inventory", "shop" })
                    Assert.Equal(revisions.Revision(part), current.Revision(part));
            }));
        Assert.Equal(balance, Service<CharacterService>().CountOf(session, 1));
        Assert.DoesNotContain(114, session.Shop!.Bought.Keys);
        Assert.Equal(opening.Cut.CommitSequence, Cut().Cut.CommitSequence);
        var rolledBack = Cut();
        Assert.Empty(Window(opening, rolledBack).Receipts);
        ExactButUncovered(opening, rolledBack, Window(opening, rolledBack));
        FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_last_economy_leg");
        Assert.Equal(0, (await Buy(bot, 114)).Result);
        var closing = Cut();
        Assert.Single(Window(opening, closing).Receipts);
        ExactButUncovered(opening, closing, Window(opening, closing));
    }

    [ClientDataFact]
    public async Task LostWriterAcknowledgementResolvesBeforeLiveApplyAndWireReply()
    {
        await using var bot = await Enter();
        var session = Session();
        var opening = Cut();
        Service<AccountStore>().FailNextEconomyAcknowledgementForTest();
        var reply = await Buy(bot, 114);
        Assert.Equal((0, 100, 1), (reply.Result, reply.Price, reply.BuyTimes));
        var closing = Cut();
        Assert.Single(Window(opening, closing).Receipts);
        Assert.Equal(1, session.Shop!.Bought[114].Count);
        Assert.Equal(Service<RoleStore>().LoadProgress(session.Role!.Id).Currency[1], session.Progress!.Currency[1]);
        Assert.Equal(Service<ItemStore>().Load(session.Role.Id).Where(i => i.Entry == 1200201).Select(i => (i.Guid, i.Stack)),
            session.Items.Where(i => i.Entry == 1200201).Select(i => (i.Guid, i.Stack)));
        var durable = Service<RoleStore>().ReadRevisionVector(session.Role.Id);
        foreach (string part in new[] { "progress", "inventory", "shop" })
            Assert.Equal(durable.Revision(part), session.Owner!.Revisions[part]);
        ExactButUncovered(opening, closing, Window(opening, closing));
    }

    [ClientDataFact]
    public async Task UnresolvableWriterOutcomeQuarantinesOwnerAndClosesTheChannel()
    {
        await using var bot = await Enter();
        var session = Session();
        long role = session.Role!.Id;
        var opening = Cut();
        var accounts = Service<AccountStore>();
        accounts.FailNextEconomyAcknowledgementForTest();
        accounts.FailNextEconomyResolutionForTest();
        var error = await Assert.ThrowsAsync<EconomyOutcomeUnknownException>(() => Service<GameLoop>().InvokeAsync(() =>
            Service<ShopService>().BuyAsync(session, new BuyCommodityReq { CommodityId = 114, Count = 1 })));
        Assert.Equal(Ro3Emu.Server.Game.Ownership.RoleOwnerState.RecoveryRequired, session.Owner!.State);
        Assert.True(session.Channel.PeerClosed);
        Assert.NotNull(EconomyReceiptReader.Read(Database, error.OperationId));
        // The async owner's quarantined reservation refuses admission before the closed session can
        // pin its lease; no second purchase or stale handover save can pass this recovery fence.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Service<GameLoop>().InvokeAsync(() =>
            Service<ShopService>().BuyAsync(session, new BuyCommodityReq { CommodityId = 114, Count = 1 })));
        Assert.Equal("An overlapping committed owner operation requires recovery.", refused.Message);
        Assert.Equal(Ro3Emu.Server.Game.Ownership.RoleOwnerState.RecoveryRequired, session.Owner.State);
        Assert.True(session.Channel.PeerClosed);
        var closing = Cut();
        Assert.Single(Window(opening, closing).Receipts);
        Assert.Equal(1, JsonSerializer.Deserialize<ShopService.ShopState>(Service<RoleStore>().LoadShop(role)!)!.Bought[114].Count);
        ExactButUncovered(opening, closing, Window(opening, closing));
    }

    [ClientDataFact]
    public async Task PostCommitNotificationFailureKeepsPurchaseAndQuotaDurable()
    {
        await using var bot = await Enter();
        var session = Session();
        var opening = Cut();
        Action<GameSession> fail = _ => throw new IOException("E01 post-commit notification fault");
        var character = Service<CharacterService>();
        character.StateChanged.Add(fail);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => Service<GameLoop>().InvokeAsync(() =>
                Service<ShopService>().BuyAsync(session, new BuyCommodityReq { CommodityId = 114, Count = 10 })));
        }
        finally { character.StateChanged.Remove(fail); }
        var closing = Cut();
        Assert.Single(Window(opening, closing).Receipts);
        Assert.Equal(10, session.Shop!.Bought[114].Count);
        Assert.Equal(10, JsonSerializer.Deserialize<ShopService.ShopState>(Service<RoleStore>().LoadShop(session.Role!.Id)!)!.Bought[114].Count);
        Assert.Equal(ErrorCodes.PurchaseLimit, (await Buy(bot, 114)).Result);
        Assert.Equal(closing.Cut.CommitSequence, Cut().Cut.CommitSequence);
        ExactButUncovered(opening, closing, Window(opening, closing));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
