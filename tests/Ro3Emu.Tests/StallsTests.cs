using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

// Serial startup keeps market hosts from overlapping; bots within each test still exercise concurrent ownership.
[CollectionDefinition("Market server", DisableParallelization = true)]
public sealed class MarketServerCollection;

/// <summary>Two wire clients exercise both 33406 paths and authoritative refusals.</summary>
[Collection("Market server")]
public sealed partial class StallsTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-stalls-" + Guid.NewGuid().ToString("N"));
    private readonly MutableClock _clock = new();
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    private long Id(BotClient bot) => (long)bot.Self!.Uuid;
    private List<InvItem> Items(BotClient bot) => Service<ItemStore>().Load(Id(bot));
    private long Money(BotClient bot, int currency) => Service<RoleStore>().LoadProgress(Id(bot)).Currency.GetValueOrDefault(currency);

    public async Task InitializeAsync()
    {
        var (port, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = port; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
        }, _clock);
        await _app.StartAsync(); _http = $"http://127.0.0.1:{port}";
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
        await bot.EnterWorldAsync(id); await SettleStalls(); return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait)).Body);

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private async Task Warp(BotClient bot, long map = 10100, int x = 156, int z = 191)
    {
        await Gm(bot, $"!warp {map} {x} {z}");
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await SettleStalls();
    }

    // C/D 15: logins and scene loads request their scene's stall maintenance; the one-second maintenance runs it.
    // Run it now, so the scene's stalls are populated and restored as soon as the test continues.
    private Task SettleStalls() => Service<GameLoop>().InvokeAsync(() => Service<StallsService>().RefreshWorldMaintenanceAsync());

    private async Task Ready(BotClient bot)
    { await Gm(bot, "!lvl 30"); await Warp(bot); }

    private Task Edit(BotClient bot, Action<MarketParticipant> edit) => Service<GameLoop>().InvokeAsync(async () =>
        Assert.Equal(0, await Service<MarketStore>().RunAsync(Session(bot), tx => { MarketFixtureEconomy.Edit(tx, tx.Role(Id(bot)), edit); return 0; })));

    private static PlayerStartStallsReq Offer(long guid, int num = 2, int price = 10000, int currency = 2)
    {
        var req = new PlayerStartStallsReq { StallsTitle = "Goods" };
        req.Itembase.Add(new StallsPutItem { Itemid = guid, Itemnum = num, Price = price, Pricetype = currency }); return req;
    }

    private async Task<PlayerStartStallsRsp> Open(BotClient bot, PlayerStartStallsReq req)
    { await bot.SendGameAsync(StallsService.Start, req); return await Next(bot, StallsService.Start, PlayerStartStallsRsp.Parser); }

    private async Task<int> Purchase(BotClient buyer, long owner, long item, int num = 1, int price = 10000, long system = 0)
    {
        await buyer.SendGameAsync(StallsService.Buy, new PlayerStallsBuyItemReq { StallsID = owner, StallsItemID = item, Num = num, Price = price, SystemStallID = system });
        return (await Next(buyer, StallsService.Buy, PlayerStallsBuyItemResp.Parser)).Result;
    }

    private static async Task<(string Id, int Template, bool Claimed, MailItemList Items)[]> Mail(BotClient bot, long role)
    {
        var reply = await bot.MicroAsync("MailService", "getUserMailList", new() { ["Uid"] = role.ToString(), ["Start"] = 0, ["Limit"] = 150, ["MailType"] = 1 });
        return Assert.IsType<object?[]>(reply["MailList"]).Select(raw =>
        {
            var r = Assert.IsType<Dictionary<object, object?>>(raw); var p = Assert.IsType<Dictionary<object, object?>>(r["Params"]);
            return ((string)r["MailId"]!, Convert.ToInt32(p["cfg_id"]), (bool)p["giftReceived"]!,
                p.TryGetValue("items", out var bytes) ? MailItemList.Parser.ParseFrom((byte[])bytes!) : new MailItemList());
        }).ToArray();
    }

    private async Task Claim(BotClient bot, params int[] templates)
    {
        var mail = (await Mail(bot, Id(bot))).Where(m => !m.Claimed && templates.Contains(m.Template) && m.Items.Items.Count > 0).ToList();
        Assert.NotEmpty(mail);
        var req = new ClaimMailReq(); req.MailUid.Add(mail.Select(m => m.Id));
        await bot.SendGameAsync(MailService.ClaimMail, req);
        Assert.Equal(0, (await Next(bot, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task DelayedOpeningPublishesTheCurrentStockAfterAConcurrentPurchase()
    {
        await using var seller = await InWorld("Seller");
        await using var buyer = await InWorld("Buyer");
        await Ready(seller); await Ready(buyer);
        await Gm(seller, "!item 1000100 3");
        await Gm(buyer, "!item 2 20000");
        long guid = Items(seller).Single(i => i.Entry == 1000100).Guid;
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Service<GameLoop>().InvokeAsync(() =>
        {
            Service<StallsService>().OpeningCommittedForTests = () => { committed.TrySetResult(); return release.Task; };
            return Task.CompletedTask;
        });
        try
        {
            await seller.SendGameAsync(StallsService.Start, Offer(guid));
            await committed.Task.WaitAsync(Wait);
            var row = Service<MarketStore>().ReadCurrent<StallsService.StallRow>("stall", "p:" + Id(seller))!;
            Assert.Equal(0, await Purchase(buyer, Id(seller), row.Stock.Single().Id));
            release.TrySetResult();
            var opened = await Next(seller, StallsService.Start, PlayerStartStallsRsp.Parser);
            Assert.Equal(0, opened.Result);
            Assert.Equal(1, Assert.Single(opened.Info.Itembase).Itembase.Stack);
            var current = Service<MarketStore>().ReadCurrent<StallsService.StallRow>("stall", "p:" + Id(seller))!;
            Assert.Equal(1, Assert.Single(current.Stock).Item.Stack);
        }
        finally
        {
            release.TrySetResult();
            Service<StallsService>().OpeningCommittedForTests = null;
        }
    }

    [ClientDataFact]
    public async Task CommittedOpeningKeepsLineUntilItsActorIsPublished()
    {
        await using var seller = await InWorld("Seller");
        await Ready(seller);
        await Gm(seller, "!item 1000100 3");
        long sellerId = Id(seller);
        long guid = Items(seller).Single(i => i.Entry == 1000100).Guid;
        var runtime = RuntimeMapKey.Public(10100, 2);
        await Service<GameLoop>().InvokeAsync(async () =>
            Assert.True(await Service<SceneService>().EnterSceneAsync(Session(seller), 10100, createLine: true)));
        var openedLine = await Next(seller, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        Assert.Equal(2, openedLine.Linenum);
        await seller.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = openedLine.SceneEntry });
        Assert.Equal(0, (await Next(seller, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(seller, MsgId.EntitySelf, EntitySelf.Parser);

        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Service<GameLoop>().InvokeAsync(() =>
        {
            Service<StallsService>().OpeningCommittedForTests = async () =>
            {
                committed.TrySetResult();
                await release.Task;
            };
            return Task.CompletedTask;
        });
        try
        {
            await seller.SendGameAsync(StallsService.Start, Offer(guid));
            await committed.Task.WaitAsync(Wait); // Escrow is committed/applied; StartAsync still owes its actor publication.
            Assert.True(Service<StallsService>().HasPendingWork(runtime));
            await Service<GameLoop>().InvokeAsync(async () =>
                Assert.True(await Service<SceneService>().EnterSceneAsync(Session(seller), 10100, lineNumber: 1)));
            var back = await Next(seller, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
            Assert.Equal(1, back.Linenum);
            await Service<GameLoop>().InvokeAsync(() => Service<PublicLineManager>().RetireEmptyAsync());
            Assert.Contains(Service<PublicLineManager>().Snapshot(10100), line => line.Map == runtime);
            Assert.True(Service<StallsService>().HasPendingWork(runtime));

            release.TrySetResult();
            Assert.Equal(0, (await Next(seller, StallsService.Start, PlayerStartStallsRsp.Parser)).Result);
            await seller.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = back.SceneEntry });
            Assert.Equal(0, (await Next(seller, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
            await Next(seller, MsgId.EntitySelf, EntitySelf.Parser);
            await using var buyer = await InWorld("Buyer");
            await Ready(buyer);
            await Service<GameLoop>().InvokeAsync(async () =>
                Assert.True(await Service<SceneService>().EnterSceneAsync(Session(buyer), 10100, lineNumber: 2)));
            var buyerLine = await Next(buyer, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
            Assert.Equal(2, buyerLine.Linenum);
            await buyer.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = buyerLine.SceneEntry });
            Assert.Equal(0, (await Next(buyer, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
            await Next(buyer, MsgId.EntitySelf, EntitySelf.Parser);
            await Next(buyer, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
                view => view.Bases.Any(entity => entity.StallsInfo?.StallsID == sellerId));
        }
        finally
        {
            release.TrySetResult();
            Service<StallsService>().OpeningCommittedForTests = null;
        }
    }

    [ClientDataFact]
    public async Task OpenPlayerStallPinsItsPublicLineUntilClosure()
    {
        await using var seller = await InWorld("Seller");
        await Ready(seller);
        await Gm(seller, "!item 1000100 3");
        long guid = Items(seller).Single(i => i.Entry == 1000100).Guid;

        async Task Move(int line, bool create)
        {
            await Service<GameLoop>().InvokeAsync(async () =>
                Assert.True(await Service<SceneService>().EnterSceneAsync(Session(seller), 10100,
                    lineNumber: line, createLine: create)));
            var sw = await Next(seller, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
            Assert.Equal(create ? 2 : line, sw.Linenum);
            await seller.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
            Assert.Equal(0, (await Next(seller, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
            await Next(seller, MsgId.EntitySelf, EntitySelf.Parser);
        }

        await Move(0, create: true);
        Assert.Equal(2, Session(seller).LineNumber);
        Assert.Equal(0, (await Open(seller, Offer(guid))).Result);
        await Move(1, create: false);
        await Service<GameLoop>().InvokeAsync(() => Service<PublicLineManager>().RetireEmptyAsync());
        Assert.Contains(Service<PublicLineManager>().Snapshot(10100), line => line.Map.LineNumber == 2);
        Assert.True(Service<StallsService>().HasPendingWork(RuntimeMapKey.Public(10100, 2)));

        await seller.SendGameAsync(StallsService.End, new PlayerStallsEndReq());
        Assert.Equal(0, (await Next(seller, StallsService.End, PlayerStallsEndResp.Parser)).Result);
        // End acknowledges escrow first; settlement follows removal of the line pin and AOI projection.
        await Next(seller, StallsService.Settlement, EndStallsInfoNtf.Parser);
        await Service<GameLoop>().InvokeAsync(() => Service<PublicLineManager>().RetireEmptyAsync());
        Assert.DoesNotContain(Service<PublicLineManager>().Snapshot(10100), line => line.Map.LineNumber == 2);
    }

    [ClientDataFact]
    public async Task RestartRestoresOfflinePlayerStallOnItsOpeningLine()
    {
        long sellerId;
        await using (var seller = await InWorld("Seller"))
        {
            await Ready(seller);
            await Gm(seller, "!item 1000100 3");
            await Service<GameLoop>().InvokeAsync(async () =>
                Assert.True(await Service<SceneService>().EnterSceneAsync(Session(seller), 10100, createLine: true)));
            var sw = await Next(seller, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
            Assert.Equal(2, sw.Linenum);
            await seller.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
            Assert.Equal(0, (await Next(seller, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
            await Next(seller, MsgId.EntitySelf, EntitySelf.Parser);
            sellerId = Id(seller);
            Assert.Equal(0, (await Open(seller, Offer(Items(seller).Single(i => i.Entry == 1000100).Guid))).Result);
        }

        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await InitializeAsync(); // Same market DB; no owner connected and no saved login line.
        await using var buyer = await InWorld("Buyer");
        await Ready(buyer);
        await Gm(buyer, "!item 2 50000");
        Assert.Contains(Service<PublicLineManager>().Snapshot(10100), line => line.Map.LineNumber == 2);
        await Service<GameLoop>().InvokeAsync(async () =>
            Assert.True(await Service<SceneService>().EnterSceneAsync(Session(buyer), 10100, lineNumber: 2)));
        var line = await Next(buyer, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        Assert.Equal(2, line.Linenum);
        await buyer.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = line.SceneEntry });
        Assert.Equal(0, (await Next(buyer, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(buyer, MsgId.EntitySelf, EntitySelf.Parser);
        await Next(buyer, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            view => view.Bases.Any(entity => entity.StallsInfo?.StallsID == sellerId));
        await buyer.SendGameAsync(StallsService.View, new PlayerStallsSyncBuyerStallsInfoReq
        { StallsID = sellerId, SceneEntryID = 10100, SceneLineNum = 2 });
        var listing = await Next(buyer, StallsService.View, PlayerStallsSyncBuyerStallsInfoResp.Parser);
        Assert.Equal(0, listing.Result);
        Assert.Equal(2, listing.Info.Baseinfo.SceneLineNum);
        Assert.Equal(0, await Purchase(buyer, sellerId, listing.Info.Itembase[0].StallsItemID));
    }

    [ClientDataFact]
    public async Task PlayerPurchasePreservesMetadataAndSettlesThroughMailWithAoiClosure()
    {
        await using var seller = await InWorld("Seller"); await using var buyer = await InWorld("Buyer");
        var sync = await Next(seller, StallsService.Sync, DBStallsInfoIncrement.Parser);
        Assert.True(sync.SyncAllMark); Assert.NotNull(sync.CurTax);
        await Ready(seller); await Ready(buyer);
        await Gm(seller, "!item 1000100 3"); await Gm(buyer, "!item 2 50000");
        long guid = Items(seller).Single(i => i.Entry == 1000100).Guid;
        await Edit(seller, p =>
        {
            var i = p.Items.Single(i => i.Guid == guid); i.Attrs = new() { [999] = 42 };
            i.StringAttrs = new() { [510] = "history" }; i.BindType = 2; i.BindExpire = _clock.GetUtcNow().ToUnixTimeSeconds() - 1;
            i.Enchantments[7] = new ItemEnchantment { Attr = new() { [103] = 5 }, GetTime = 123 };
        });
        await seller.SendGameAsync(StallsService.Prices, new PlayerStallsQueryItemPriceReq { ItemID = { 1000100 } });
        var price = await Next(seller, StallsService.Prices, PlayerStallsQueryItemPriceResp.Parser);
        Assert.Equal((5, 15), (price.ItemPrice[1000100].MinPercent, price.ItemPrice[1000100].MaxPercent));
        var opened = await Open(seller, Offer(guid)); Assert.Equal(0, opened.Result);
        var offer = Assert.Single(opened.Info.Itembase);
        Assert.Equal(980, Money(seller, 1)); Assert.Equal(1, Items(seller).Single(i => i.Entry == 1000100).Stack);
        var aoi = await Next(buyer, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, m => m.Bases.Any(b => b.StallsInfo?.StallsID == Id(seller)));
        var actor = aoi.Bases.Single(b => b.StallsInfo?.StallsID == Id(seller)); Assert.Equal(9, actor.EntityType);
        await buyer.SendGameAsync(StallsService.View, new PlayerStallsSyncBuyerStallsInfoReq { StallsID = Id(seller), SceneEntryID = 10100, SceneLineNum = 1 });
        Assert.Equal(0, (await Next(buyer, StallsService.View, PlayerStallsSyncBuyerStallsInfoResp.Parser)).Result);
        Assert.Equal(0, await Purchase(buyer, Id(seller), offer.StallsItemID));
        Assert.Equal(40000, Money(buyer, 2)); Assert.DoesNotContain(Items(buyer), i => i.Entry == 1000100);
        var changed = await Next(buyer, StallsService.BuyerChange, PlayerStallsSyncBuyerStallsInfoNtf.Parser);
        Assert.Equal(1, Assert.Single(changed.Info.Itembase).Itembase.Stack);
        await Claim(buyer, 1028);
        var received = Items(buyer).Single(i => i.Entry == 1000100);
        Assert.Equal(42, received.Attrs![999]); Assert.Equal("history", received.StringAttrs![510]);
        Assert.Equal(5, received.Enchantments[7].Attr[103]); Assert.Equal(123, received.Enchantments[7].GetTime);
        Assert.Equal(2, received.BindType); Assert.NotEqual(guid, received.Guid);
        await Claim(seller, 1029); Assert.Equal(9100, Money(seller, 2));
        await seller.SendGameAsync(StallsService.End, new PlayerStallsEndReq());
        Assert.Equal(0, (await Next(seller, StallsService.End, PlayerStallsEndResp.Parser)).Result);
        await Next(buyer, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, m => m.LeaveAoi.Contains(actor.Uuid));
        await Next(buyer, StallsService.BuyerChange, PlayerStallsSyncBuyerStallsInfoNtf.Parser, m => m.Info.BEnd);
        await Claim(seller, 1030);
        Assert.Equal(2, Items(seller).Where(i => i.Entry == 1000100).Sum(i => i.Stack));
        Assert.Equal(ErrorCodes.StallMissing, await Purchase(buyer, Id(seller), offer.StallsItemID));
    }

    [ClientDataFact]
    public async Task PlayerRefusalsDoNotMoveOwnershipOrMoney()
    {
        await using var seller = await InWorld("Seller"); await using var buyer = await InWorld("Buyer");
        await Gm(seller, "!item 1000100 3"); long guid = Items(seller).Single(i => i.Entry == 1000100).Guid;
        Assert.Equal(ErrorCodes.FunctionLocked, (await Open(seller, Offer(guid))).Result);
        await Gm(seller, "!lvl 30"); Assert.Equal(ErrorCodes.StallArea, (await Open(seller, Offer(guid))).Result);
        await Warp(seller); await Ready(buyer);
        await Edit(seller, p => p.Items.Single(i => i.Guid == guid).IsLocked = true);
        Assert.Equal(ErrorCodes.ItemLocked, (await Open(seller, Offer(guid))).Result);
        await Edit(seller, p => { var i = p.Items.Single(i => i.Guid == guid); i.IsLocked = false; i.BindType = 1; });
        Assert.Equal(ErrorCodes.TradeBound, (await Open(seller, Offer(guid))).Result);
        await Edit(seller, p => p.Items.Single(i => i.Guid == guid).BindType = 0);
        Assert.Equal(ErrorCodes.ItemMissing, (await Open(buyer, Offer(guid))).Result);
        Assert.Equal(ErrorCodes.TradePrice, (await Open(seller, Offer(guid, price: 1))).Result);
        Assert.Equal(ErrorCodes.TradePrice, (await Open(seller, Offer(guid, currency: 1))).Result);
        Assert.Equal(ErrorCodes.StallQuantity, (await Open(seller, Offer(guid, num: 4))).Result);
        var duplicate = Offer(guid, num: 1); duplicate.Itembase.Add(duplicate.Itembase[0].Clone());
        Assert.Equal(ErrorCodes.Parameter, (await Open(seller, duplicate)).Result);
        Assert.Equal(1000, Money(seller, 1)); Assert.Equal(3, Items(seller).Single(i => i.Entry == 1000100).Stack);
        var opened = await Open(seller, Offer(guid)); Assert.Equal(0, opened.Result); long item = opened.Info.Itembase[0].StallsItemID;
        Assert.Equal(ErrorCodes.StallExists, (await Open(seller, Offer(guid))).Result);
        Assert.Equal(ErrorCodes.TradeRequest, await Purchase(seller, Id(seller), item));
        Assert.Equal(ErrorCodes.StallCurrency, await Purchase(buyer, Id(seller), item));
        Assert.Equal(ErrorCodes.StallPriceChanged, await Purchase(buyer, Id(seller), item, price: 11000));
        Assert.Equal(ErrorCodes.StallQuantity, await Purchase(buyer, Id(seller), item, num: 3));
        Assert.Equal(ErrorCodes.Parameter, await Purchase(buyer, Id(seller), item, num: -1));
        Assert.Equal(ErrorCodes.StallItemMissing, await Purchase(buyer, Id(seller), 999));
        await Warp(buyer, 10111, 156, 192);
        Assert.Equal(ErrorCodes.StallDistance, await Purchase(buyer, Id(seller), item));
        Assert.Empty(await Mail(buyer, Id(buyer))); Assert.Equal(0, Money(buyer, 2));
        Assert.Equal(1, Items(seller).Single(i => i.Entry == 1000100).Stack);
    }

    [ClientDataFact]
    public async Task SystemStallStockAndLimitsAreSharedByTwoBuyers()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Warp(alice, 10111, 251, 2); await Warp(bob, 10111, 251, 2);
        await Gm(alice, "!item 2 2000000"); await Gm(bob, "!item 2 2000000");
        foreach (var bot in new[] { alice, bob })
        {
            await bot.SendGameAsync(StallsService.View, new PlayerStallsSyncBuyerStallsInfoReq { SystemStallID = 1, SceneEntryID = 10111, SceneLineNum = 1 });
            var view = await Next(bot, StallsService.View, PlayerStallsSyncBuyerStallsInfoResp.Parser);
            Assert.Equal(0, view.Result); Assert.Equal(100, view.Info.Itembase.Single(i => i.SystemStallCfgID == 1).Itembase.Stack);
        }
        Assert.Equal(ErrorCodes.StallPriceChanged, await Purchase(alice, 0, 1, price: 1, system: 1));
        Assert.Equal(ErrorCodes.Parameter, await Purchase(alice, 0, 1, num: 0, system: 1));
        Assert.Equal(ErrorCodes.PurchaseLimit, await Purchase(alice, 0, 1, num: 100, system: 1));
        Assert.Equal(0, await Purchase(alice, 0, 1, num: 99, system: 1));
        Assert.Equal(ErrorCodes.PurchaseLimit, await Purchase(alice, 0, 1, system: 1));
        Assert.Equal(0, await Purchase(bob, 0, 1, system: 1));
        Assert.Equal(ErrorCodes.StallQuantity, await Purchase(bob, 0, 1, system: 1));
        await Claim(alice, 1028); await Claim(bob, 1028);
        Assert.Equal(99, Items(alice).Single(i => i.Entry == 1000100).Stack);
        Assert.Equal(1, Items(bob).Single(i => i.Entry == 1000100).Stack);
        Assert.Equal(1, Items(bob).Single(i => i.Entry == 1000100).BindType);
        Assert.Equal(1010000, Money(alice, 2)); Assert.Equal(1990000, Money(bob, 2));
    }

    [ClientDataFact]
    public async Task SqliteFailureRollsBackStockPaymentAndBothMailsThenRetrySucceeds()
    {
        await using var seller = await InWorld("Seller"); await using var buyer = await InWorld("Buyer");
        await Ready(seller); await Ready(buyer); await Gm(seller, "!item 1000100 2"); await Gm(buyer, "!item 2 20000");
        long guid = Items(seller).Single(i => i.Entry == 1000100).Guid;
        var opened = await Open(seller, Offer(guid)); Assert.Equal(0, opened.Result);
        FixtureSql.Execute(Service<AccountStore>(),
            $"CREATE TRIGGER fail_market BEFORE UPDATE OF currency ON role WHEN NEW.id={Id(buyer)} BEGIN SELECT RAISE(ABORT, 'test rollback'); END");
        Assert.Equal(ErrorCodes.Unknown, await Purchase(buyer, Id(seller), opened.Info.Itembase[0].StallsItemID));
        Assert.Equal(20000, Money(buyer, 2)); Assert.Equal(20000, Session(buyer).Progress!.Currency[2]);
        Assert.Empty(await Mail(seller, Id(seller))); Assert.Empty(await Mail(buyer, Id(buyer)));
        await seller.SendGameAsync(StallsService.Info, new PlayerStallsInfoReq());
        Assert.Equal(2, (await Next(seller, StallsService.Info, PlayerStallsInfoResp.Parser)).Info.Itembase[0].Itembase.Stack);
        FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_market");
        Assert.Equal(0, await Purchase(buyer, Id(seller), opened.Info.Itembase[0].StallsItemID));
        Assert.Single(await Mail(seller, Id(seller))); Assert.Single(await Mail(buyer, Id(buyer)));
    }

    [ClientDataFact]
    public async Task LiveEditsAndExpiryReturnOnlyUnsoldStockAndSurviveRelog()
    {
        await using var seller = await InWorld("Seller"); await using var buyer = await InWorld("Buyer");
        await Ready(seller); await Ready(buyer); await Gm(seller, "!item 1000100 5");
        long guid = Items(seller).Single(i => i.Entry == 1000100).Guid;
        var open = await Open(seller, Offer(guid)); Assert.Equal(0, open.Result); long item = open.Info.Itembase[0].StallsItemID;
        await seller.SendGameAsync(StallsService.Put, new PlayerStallsPutItemReq
        { PutItem = new StallsPutItem { Itemid = guid, Itemnum = 1, Price = 10000, Pricetype = 2 } });
        var added = await Next(seller, StallsService.Put, PlayerStallsPutItemRsp.Parser);
        Assert.Equal(0, added.Result); Assert.NotEqual(item, added.Baseinfo.StallsItemID);
        await seller.SendGameAsync(StallsService.Remove, new PlayerStallsRemoveItemReq { StallsItemID = added.Baseinfo.StallsItemID });
        Assert.Equal(0, (await Next(seller, StallsService.Remove, PlayerStallsRemoveItemRsp.Parser)).Result);
        await Claim(seller, 1027);
        await seller.SendGameAsync(StallsService.Share, new PlayerStallsShareCheckReq { ShareData = new string('x', 21) });
        Assert.Equal(ErrorCodes.Parameter, (await Next(seller, StallsService.Share, PlayerStallsShareCheckResp.Parser)).Result);
        await seller.SendGameAsync(StallsService.Share, new PlayerStallsShareCheckReq());
        Assert.Equal(0, (await Next(seller, StallsService.Share, PlayerStallsShareCheckResp.Parser)).Result);
        await seller.SendGameAsync(StallsService.Share, new PlayerStallsShareCheckReq { ShareData = "Goods" });
        Assert.Equal(ErrorCodes.StallShareCooldown, (await Next(seller, StallsService.Share, PlayerStallsShareCheckResp.Parser)).Result);
        await seller.SendGameAsync(StallsService.Update, new PlayerStallsUpdateItemPriceReq { StallsItemID = item, BAdd = true, Num = 1, Price = 11000 });
        Assert.Equal(0, (await Next(seller, StallsService.Update, PlayerStallsUpdateItemPriceItemRsp.Parser)).Result);
        Assert.Equal(2, Items(seller).Where(i => i.Entry == 1000100).Sum(i => i.Stack));
        await seller.SendGameAsync(StallsService.Update, new PlayerStallsUpdateItemPriceReq { StallsItemID = item, Num = 1, Price = 11000 });
        Assert.Equal(0, (await Next(seller, StallsService.Update, PlayerStallsUpdateItemPriceItemRsp.Parser)).Result);
        await Claim(seller, 1027);
        // Typed titles up to ServerOptions.StallNameMaxLength (24) pass, longer ones fail; the client's default
        // "${1}'s Stall" (Localization 37824) passes at any length.
        await seller.SendGameAsync(StallsService.Rename, new PlayerUpdateStallsNameReq { StallsTitle = "Potions and ores for all" });
        Assert.Equal(0, (await Next(seller, StallsService.Rename, PlayerUpdateStallsNameResp.Parser)).Result);
        await seller.SendGameAsync(StallsService.Rename, new PlayerUpdateStallsNameReq { StallsTitle = "Potions and ores for all!" });
        Assert.Equal(ErrorCodes.StallName, (await Next(seller, StallsService.Rename, PlayerUpdateStallsNameResp.Parser)).Result);
        await seller.SendGameAsync(StallsService.Rename, new PlayerUpdateStallsNameReq { StallsTitle = "New goods" });
        Assert.Equal(0, (await Next(seller, StallsService.Rename, PlayerUpdateStallsNameResp.Parser)).Result);
        await seller.SendGameAsync(StallsService.Top, new PlayerStallsTopItemReq { StallItemID = item });
        Assert.Equal(0, (await Next(seller, StallsService.Top, PlayerStallsTopItemRsp.Parser)).Result);
        await seller.DisposeAsync();
        await using var returning = await InWorld("Seller", create: false);
        await returning.SendGameAsync(StallsService.Info, new PlayerStallsInfoReq());
        var restored = await Next(returning, StallsService.Info, PlayerStallsInfoResp.Parser);
        Assert.Equal("New goods", restored.Info.Baseinfo.StallsTitle); Assert.Equal(2, restored.Info.Itembase[0].Itembase.Stack);
        _clock.Advance(TimeSpan.FromDays(8));
        await returning.SendGameAsync(StallsService.Info, new PlayerStallsInfoReq());
        Assert.Equal(0, (await Next(returning, StallsService.Info, PlayerStallsInfoResp.Parser)).CurrentStallsID);
        await Claim(returning, 1031);
        Assert.Equal(5, Items(returning).Where(i => i.Entry == 1000100).Sum(i => i.Stack));
        Assert.Equal(ErrorCodes.StallMissing, await Purchase(buyer, Id(returning), item));
    }

    [ClientDataFact]
    public async Task ColdRestartRestoresEscrowOfflineAoiAttachmentsAndSystemPurchaseLimit()
    {
        long sellerId, offerId;
        string sku;
        await using (var seller = await InWorld("Seller"))
        await using (var buyer = await InWorld("Buyer"))
        {
            await Ready(seller); await Ready(buyer);
            await Gm(seller, "!item 1000100 3"); await Gm(seller, "!item 1254013 2");
            await Gm(seller, "!item 2 700"); await Gm(buyer, "!item 2 2000000");
            sellerId = Id(seller);
            var opened = await Open(seller, Offer(Items(seller).Single(i => i.Entry == 1000100).Guid));
            Assert.Equal(0, opened.Result); offerId = opened.Info.Itembase[0].StallsItemID;
            Assert.Equal(0, await Purchase(buyer, sellerId, offerId));
            await seller.SendGameAsync(TradeService.PutOn, new TradePutOnItemReq
            { ItemGuid = Items(seller).Single(i => i.Entry == 1254013).Guid, ItemPrice = 600, CurrencyType = 1, Quantity = 2 });
            var listed = await Next(seller, TradeService.PutOn, TradePutOnItemResp.Parser);
            Assert.Equal(0, listed.Result); sku = listed.BoothInfos[0].SkuId;
            await buyer.SendGameAsync(TradeService.BuyNormal, new TradeBuyNormalItemsReq
            { ItemId = 1254013, SpuId = listed.BoothInfos[0].SpuId, Price = 600, TotalPrice = 600, TotalQuantity = 1 });
            Assert.Equal(0, (await Next(buyer, TradeService.BuyNormal, TradeBuyNormalItemsResp.Parser)).Result);
            await Warp(buyer, 10111, 251, 2);
            Assert.Equal(0, await Purchase(buyer, 0, 1, num: 99, system: 1));
        }

        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await InitializeAsync(); // Same database, fresh services, sessions, world and inventory objects.
        await using var returningBuyer = await InWorld("Buyer", create: false);
        await Warp(returningBuyer, 10111, 251, 2);
        Assert.Equal(ErrorCodes.PurchaseLimit, await Purchase(returningBuyer, 0, 1, system: 1));
        Assert.DoesNotContain(Items(returningBuyer), i => i.Entry == 1000100 || i.Entry == 1254013);
        await Claim(returningBuyer, 1028, 1064);
        Assert.Equal(100, Items(returningBuyer).Where(i => i.Entry == 1000100).Sum(i => i.Stack));
        Assert.Equal(1, Items(returningBuyer).Single(i => i.Entry == 1254013).Stack);
        var claimed = (await Mail(returningBuyer, Id(returningBuyer))).First(m => m.Template == 1064);
        await returningBuyer.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { claimed.Id } });
        Assert.Equal(ErrorCodes.MailAlreadyClaimed, (await Next(returningBuyer, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        await Warp(returningBuyer);
        await Next(returningBuyer, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            m => m.Bases.Any(b => b.StallsInfo?.StallsID == sellerId));
        Assert.Equal(0, await Purchase(returningBuyer, sellerId, offerId)); // Seller is still offline.
        await using var returningSeller = await InWorld("Seller", create: false);
        await returningSeller.SendGameAsync(TradeService.OwnBooth, new QueryPlayerBoothInfoRequest());
        var booth = await Next(returningSeller, TradeService.OwnBooth, CQueryPlayerBoothInfoResp.Parser);
        var remaining = Assert.Single(booth.Data.DataList);
        Assert.Equal(sku, remaining.SkuId); Assert.Equal(1, remaining.Quantity);
        await Claim(returningSeller, 1029, 1063);
        Assert.Equal(18200, Money(returningSeller, 2));
        Assert.Equal(1580, Money(returningSeller, 1));
        await returningSeller.SendGameAsync(StallsService.Info, new PlayerStallsInfoReq());
        var info = await Next(returningSeller, StallsService.Info, PlayerStallsInfoResp.Parser);
        Assert.Equal(sellerId, info.CurrentStallsID); Assert.Empty(info.Info.Itembase);
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Ro3Emu.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException();
    }
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
