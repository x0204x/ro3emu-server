using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.LoadTest;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Fashion-tab commodities (ShopCommodityConfig._iAppearanceId with _iItemId 0): payment, 5034 unlock of the look and
/// its suit pieces, the _iAppearanceAwardId bonus and the counter in one cd13a/shop-appearance commit; refusals
/// charge nothing and never drop the gate connection.
/// </summary>
public sealed class AppearanceShopTests : IAsyncLifetime
{
    private const int Fashion = 3;                                    // ItemConfig 3 "Sacred Relic Crystal".
    private const int TeaParty = 5000002, TeaPartySuit = 40050010, TeaPartyHat = 10050010; // [3,1880], award 180002.
    private const int TeaPartyBonus = 3025101;                        // AwardConfig 100180002: [[3025101,1,0]].
    private const int Macaron = 5000015, MacaronHair = 70050010;      // [3,480], no award.
    private const int Stroll = 5000007, StrollSuit = 40030010;        // [3,680], no award, no pieces.
    private const int Potions = 12105, PotionItem = 1003100;          // [1,1000], cd13a/shop-buy.
    private const int BigPoringLook = 5000038, BigPoring = 60030010;  // class 50202 [3,980]; WardRobeConfig type 6 -> MountsConfig.
    private readonly string _directory = Path.Combine(RepoRoot(), ".loadtest-temp", "fashion-shop-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly Lazy<EconomyCatalog> Catalog = new(() => EconomyCatalog.Load(Path.Combine(RepoRoot(), "data")));
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private GameSession Alice => Service<SessionRegistry>().All().Single(s => s.Account == "fashionalice" && s.Role != null);
    private EconomySnapshot Cut() => S3EconomyReader.Read(_directory, Catalog.Value);
    private EconomyLedgerWindow Window(EconomySnapshot a, EconomySnapshot b) => S3EconomyReader.ReadLedger(_directory, Catalog.Value, a, b)!;

    public async Task InitializeAsync()
    {
        _clock.Set("2026-10-09 12:00:00");
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
            o.PlayBook = 101;
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task<BotClient> Enter()
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync("fashionalice", "pw"); await bot.EnterGateAsync();
        long role = (await bot.CreateRoleAsync("FashionAlice")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(role);
        await Next(bot, ShopService.ShopSync, DBShopInfoIncrement.Parser);
        await Next(bot, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser);
        // C/D 15: let the scene's stall maintenance land before the opening cut (EconomyShopTests).
        await Service<GameLoop>().InvokeAsync(() => Service<StallsService>().RefreshWorldMaintenanceAsync());
        await Gm("!lvl 10"); // FunctionConfig 70502-70509 open at base level 10 (conditions 2000302-2000307).
        await Gm("!item 3 10000");
        await Gm("!item 1 10000");
        return bot;
    }

    private async Task Gm(string command, string account = "fashionalice")
    {
        using var client = new HttpClient();
        (await client.PostAsync(_http + "/ro3emu/gm?account=" + account, new StringContent(command))).EnsureSuccessStatusCode();
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait)).Body);

    private static async Task<BatchBuyCommodityResp> Batch(BotClient bot, params (int Commodity, int Count, int Price)[] items)
    {
        var req = new BatchBuyCommodityReq();
        foreach (var (commodity, count, price) in items) req.Items.Add(new BatchBuyCommodityItem { CommodityId = commodity, Count = count, Price = price });
        await bot.SendGameAsync(ShopService.BatchBuyCommodity, req);
        return await Next(bot, ShopService.BatchBuyCommodity, BatchBuyCommodityResp.Parser);
    }

    private static async Task<BuyCommodityResp> Buy(BotClient bot, int commodity, int count = 1, int price = 0)
    {
        await bot.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = commodity, Count = count, Price = price });
        return await Next(bot, ShopService.BuyCommodity, BuyCommodityResp.Parser);
    }

    private long Crystals => Service<CharacterService>().CountOf(Alice, Fashion);
    private int Bag(int entry) => Alice.Items.Where(i => i.Entry == entry).Sum(i => i.Stack);
    private DBWardRobeInfo SavedWardrobe => DBWardRobeInfo.Parser.ParseFrom(Service<RoleStore>().LoadWardrobe(Alice.Role!.Id));
    private ShopService.ShopState SavedShop => JsonSerializer.Deserialize<ShopService.ShopState>(Service<RoleStore>().LoadShop(Alice.Role!.Id)!)!;

    private static void Exact(EconomySnapshot a, EconomySnapshot b, EconomyLedgerWindow ledger)
    {
        Assert.True(ledger.CompleteHistory, string.Join(";", ledger.Issues.Select(x => x.Detail)));
        var result = EconomyExactnessChecker.Check(a, b, ledger);
        Assert.DoesNotContain(result.Issues, i => i.Kind == EconomyIssueKind.Corruption);
        Assert.All(result.Assets, x => { Assert.Equal("0", x.Residual); Assert.Equal("0", x.LegResidual); });
        Assert.All(result.Locations, x => Assert.Equal("0", x.Residual));
    }

    [ClientDataFact]
    public async Task SuitPurchaseUnlocksTheSuitAndItsPiecesGrantsTheBonusAndChargesOnce()
    {
        await using var bot = await Enter();
        long crystals = Crystals;
        int bonus = Bag(TeaPartyBonus);
        var opening = Cut();

        var reply = await Batch(bot, (TeaParty, 1, 1880));
        Assert.Equal(0, reply.Result);
        var item = Assert.Single(reply.Results);
        Assert.Equal((TeaParty, 0, 1, 1880, 1), (item.CommodityId, item.Result, item.Count, item.Price, item.BuyTimes));
        var unlocked = await Next(bot, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser,
            i => i.AllLightUpAppearances.ContainsKey(TeaPartySuit));
        Assert.True(unlocked.AllLightUpAppearances.ContainsKey(TeaPartyHat));
        Assert.Equal(TeaPartySuit, (await Next(bot, WardrobeService.UnlockNotice, WardRobeUnLockNtf.Parser)).AppearanceId);
        var counter = await Next(bot, ShopService.ShopSync, DBShopInfoIncrement.Parser, s => s.CommodityBuyTimes.ContainsKey(TeaParty));
        Assert.Equal(1, counter.CommodityBuyTimes[TeaParty].All);

        Assert.Equal(crystals - 1880, Crystals);
        Assert.Equal(bonus + 1, Bag(TeaPartyBonus));
        Assert.Equal(crystals - 1880, Service<RoleStore>().LoadProgress(Alice.Role!.Id).Currency[Fashion]);
        Assert.True(SavedWardrobe.AllLightUpAppearances.ContainsKey(TeaPartySuit));
        Assert.True(SavedWardrobe.AllLightUpAppearances.ContainsKey(TeaPartyHat));
        Assert.Equal(1, SavedShop.Bought[TeaParty].Count);

        var closing = Cut();
        var ledger = Window(opening, closing);
        var receipt = Assert.Single(ledger.Receipts, r => r.CommandKind.StartsWith("cd13a/shop-", StringComparison.Ordinal));
        Assert.Equal("cd13a/shop-appearance", receipt.CommandKind);
        Assert.Equal(2, receipt.LegCount);
        var sink = Assert.Single(ledger.Legs, x => x.Kind == EconomyLegKind.Sink && x.OperationId == receipt.OperationId);
        Assert.Equal((EconomyReason.ShopPayment, new EconomyAsset(EconomyAssetKind.Currency, Fashion), -1880L), (sink.Reason, sink.Asset, sink.Delta));
        var source = Assert.Single(ledger.Legs, x => x.Kind == EconomyLegKind.Source && x.OperationId == receipt.OperationId);
        Assert.Equal((EconomyReason.ShopGoods, (long)TeaPartyBonus, 1L), (source.Reason, source.Asset.Id, source.Delta));
        Exact(opening, closing, ledger);
    }

    [ClientDataFact]
    public async Task OwnedAppearanceIsRefusedWithoutCharge()
    {
        await using var bot = await Enter();
        long crystals = Crystals;
        var first = await Buy(bot, Macaron, price: 480);
        Assert.Equal((0, 480, 1), (first.Result, first.Price, first.BuyTimes));
        await Next(bot, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser, i => i.AllLightUpAppearances.ContainsKey(MacaronHair));
        Assert.Equal(crystals - 480, Crystals);

        var again = await Buy(bot, Macaron, price: 480);
        Assert.Equal((ErrorCodes.ShopAppearanceOwned, 1), (again.Result, again.BuyTimes));
        var batch = await Batch(bot, (Macaron, 1, 480));
        Assert.Equal(ErrorCodes.ShopAppearanceOwned, batch.Result);
        Assert.Equal(ErrorCodes.ShopAppearanceOwned, Assert.Single(batch.Results).Result);
        Assert.Equal(crystals - 480, Crystals);
        Assert.Equal(1, Alice.Shop!.Bought[Macaron].Count);
        Assert.Equal(1, SavedShop.Bought[Macaron].Count);
        Assert.Single(bot.Inbox.Snapshot(), e => e.IsGame(WardrobeService.UnlockNotice));

        // An appearance is one unlock: a count above 1 is a parameter error, also without charge.
        var many = await Buy(bot, Stroll, count: 2, price: 680);
        Assert.Equal(ErrorCodes.Parameter, many.Result);
        Assert.Equal(crystals - 480, Crystals);
        Assert.False(Alice.Wardrobe!.AllLightUpAppearances.ContainsKey(StrollSuit));
    }

    [ClientDataFact]
    public async Task BatchWithAnAppearanceAndAnItemBuysBoth()
    {
        await using var bot = await Enter();
        long crystals = Crystals, zeny = Service<CharacterService>().CountOf(Alice, 1);
        int potions = Bag(PotionItem);
        var opening = Cut();
        var reply = await Batch(bot, (Stroll, 1, 680), (Potions, 2, 1000));
        Assert.Equal(0, reply.Result);
        Assert.Equal(new[] { (Stroll, 0, 1), (Potions, 0, 2) },
            reply.Results.Select(r => (r.CommodityId, r.Result, r.BuyTimes)).ToArray());
        Assert.Equal(crystals - 680, Crystals);
        Assert.Equal(zeny - 2000, Service<CharacterService>().CountOf(Alice, 1));
        Assert.Equal(potions + 2, Bag(PotionItem));
        Assert.True(Alice.Wardrobe!.AllLightUpAppearances.ContainsKey(StrollSuit));
        Assert.True(SavedWardrobe.AllLightUpAppearances.ContainsKey(StrollSuit));
        Assert.Equal((1, 2), (SavedShop.Bought[Stroll].Count, SavedShop.Bought[Potions].Count));
        var closing = Cut();
        var ledger = Window(opening, closing);
        Assert.Equal(new[] { "cd13a/shop-appearance", "cd13a/shop-buy" },
            ledger.Receipts.Select(r => r.CommandKind).Where(k => k.StartsWith("cd13a/shop-", StringComparison.Ordinal)).Order().ToArray());
        Exact(opening, closing, ledger);
    }

    [ClientDataFact]
    public async Task MountLookPurchaseOwnsTheMountSoTheWardrobeRidesIt()
    {
        await using var bot = await Enter();
        await Gm("!lvl 14"); // FunctionConfig 27 (mounts) for 32743 / 32745.
        long crystals = Crystals;
        var opening = Cut();
        int start = bot.Inbox.Snapshot().Count;

        var reply = await Buy(bot, BigPoringLook, price: 980);
        Assert.Equal((0, 980, 1), (reply.Result, reply.Price, reply.BuyTimes));
        var lit = await Next(bot, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser,
            i => i.AllLightUpAppearances.ContainsKey(BigPoring));
        Assert.Equal(0, lit.AllLightUpAppearances[BigPoring].All.OverdueTimeMs);
        var owned = await Next(bot, MountService.Sync, DBMountsInfoIncrement.Parser, p => p.Mounts.ContainsKey(BigPoring));
        Assert.Equal((BigPoring, 0L), (owned.Mounts[BigPoring].All.MountId, owned.Mounts[BigPoring].All.OverdueTimeMs));
        Assert.Equal(BigPoring, (await Next(bot, WardrobeService.UnlockNotice, WardRobeUnLockNtf.Parser)).AppearanceId);
        // One unlock tip for the purchase: 31189, not also 32747.
        Assert.DoesNotContain(bot.Inbox.Snapshot().Skip(start), e => e.IsGame(MountService.Unlock));
        Assert.Equal(crystals - 980, Crystals);
        var stored = DBMountsInfo.Parser.ParseFrom(JsonSerializer.Deserialize<MountService.SavedState>(
            Service<RoleStore>().LoadMounts(Alice.Role!.Id)!)!.Info);
        Assert.Equal(0, stored.Mounts[BigPoring].OverdueTimeMs);
        Assert.True(SavedWardrobe.AllLightUpAppearances.ContainsKey(BigPoring));

        var closing = Cut();
        var ledger = Window(opening, closing);
        var receipt = Assert.Single(ledger.Receipts, r => r.CommandKind.StartsWith("cd13a/shop-", StringComparison.Ordinal));
        Assert.Equal("cd13a/shop-appearance", receipt.CommandKind);
        Exact(opening, closing, ledger);

        // UI_Sys_WardrobePanelCtrl._doSaveWardrobe: 31183 with the worn list, then 32743 and 32745.
        var wear = new WardRobeUseAppearanceReq(); wear.AppearanceIds.Add(BigPoring);
        await bot.SendGameAsync(WardrobeService.Use, wear);
        Assert.Equal(0, (await Next(bot, WardrobeService.Use, WardRobeUseAppearanceRes.Parser)).Result);
        await bot.SendGameAsync(MountService.SetConfig, new PlayerMountsSetCfgReq { MountId = BigPoring });
        Assert.Equal(0, (await Next(bot, MountService.SetConfig, PlayerMountsSetCfgResp.Parser)).Result);
        await bot.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = (eRiderType)1 });
        Assert.Equal(0, (await Next(bot, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        Assert.Equal(BigPoring, MountService.Rider(Alice));

        // Owned now: the look cannot be bought twice.
        Assert.Equal(ErrorCodes.ShopAppearanceOwned, (await Buy(bot, BigPoringLook, price: 980)).Result);
        Assert.Equal(crystals - 980, Crystals);
    }

    [Fact]
    public async Task GiftToAnUnknownTargetIsRefusedWithoutChargeOrDelivery()
    {
        await using var bot = await Enter();
        long crystals = Crystals;
        byte[] wardrobe = Service<RoleStore>().LoadWardrobe(Alice.Role!.Id)!;
        await bot.SendGameAsync(ShopService.CommodityPresent, new CommodityPresentReq
        {
            TargetId = 424242, GiftContent = "for you",
            BuyReq = new BuyCommodityReq { CommodityId = TeaParty, Count = 1, Price = 1880 },
        });
        var reply = await Next(bot, ShopService.CommodityPresent, CommodityPresentResp.Parser);
        // Non-zero: LC_DAL_Shop.CommodityPresentResp (the "gift sent" tip and chat card) never runs.
        Assert.Equal(ErrorCodes.Parameter, reply.Result);
        Assert.Equal(crystals, Crystals);
        Assert.Empty(Alice.Shop!.Bought);
        Assert.False(Alice.Wardrobe!.AllLightUpAppearances.ContainsKey(TeaPartySuit));
        Assert.Equal(wardrobe, Service<RoleStore>().LoadWardrobe(Alice.Role!.Id));
        Assert.True(Service<RoleStore>().LoadShop(Alice.Role!.Id) is not { } shop
            || JsonSerializer.Deserialize<ShopService.ShopState>(shop)!.Bought.Count == 0);
    }

    [ClientDataFact]
    public async Task MalformedCommoditiesAreRefusedWithoutChargeAndTheConnectionSurvives()
    {
        await using var bot = await Enter();
        // Rows the export does not contain, injected into this host's loaded table (Fashion class 50201, open).
        var table = (Dictionary<string, JsonElement>)Service<GameData>().Table("ShopCommodityConfig");
        void Inject(int id, Action<JsonObject> edit)
        {
            var row = JsonNode.Parse(table[TeaParty.ToString()].GetRawText())!.AsObject();
            row["_iID"] = id;
            edit(row);
            table[id.ToString()] = JsonDocument.Parse(row.ToJsonString()).RootElement.Clone();
        }
        Inject(9900001, r => { r["_iAppearanceId"] = 0; r["_iAppearanceAwardId"] = 0; });      // no goods at all: the old crash.
        Inject(9900002, r => r["_kItemPerPrice"] = new JsonArray(424242, 10));                    // unknown payment item.
        Inject(9900003, r => { r["_iAppearanceId"] = 99999999; r["_kAppearanceParts"] = new JsonObject(); }); // unknown look.
        Inject(9900004, r => r["_iAppearanceAwardId"] = 999999999);                               // unknown bonus group.
        Inject(9900005, r => { r["_iAppearanceId"] = 0; r["_iItemId"] = 424242; });             // unknown item goods.
        Inject(9900006, r => r["_kAppearanceParts"] = new JsonArray(99999998));                  // unknown suit piece.
        long crystals = Crystals;

        var reply = await Batch(bot, (9900001, 1, 1880), (9900002, 1, 10), (9900003, 1, 1880), (9900004, 1, 1880),
            (9900005, 1, 1880), (9900006, 1, 1880), (1011001, 1, 1000), (424242, 1, 1));
        Assert.Equal(new[] { ErrorCodes.ShopNoConfig, ErrorCodes.ShopNoConfig, ErrorCodes.ShopAppearanceMissing,
                ErrorCodes.ShopRewardMissing, ErrorCodes.ShopNoConfig, ErrorCodes.ShopAppearanceMissing },
            reply.Results.Take(6).Select(r => r.Result).ToArray());
        Assert.All(reply.Results, r => Assert.NotEqual(0, r.Result));
        Assert.Equal(ErrorCodes.ShopNoConfig, reply.Result);
        Assert.Equal(ErrorCodes.ShopNoConfig, (await Buy(bot, 9900001, price: 1880)).Result);
        Assert.Equal(ErrorCodes.ShopRewardMissing, (await Buy(bot, 9900004, price: 1880)).Result);
        Assert.Equal(crystals, Crystals);
        Assert.False(Alice.Wardrobe!.AllLightUpAppearances.ContainsKey(TeaPartySuit));
        Assert.Empty(Alice.Shop!.Bought);

        // The same gate connection still serves a good purchase afterwards.
        var good = await Buy(bot, Macaron, price: 480);
        Assert.Equal(0, good.Result);
        Assert.Equal(crystals - 480, Crystals);
    }

    // ---- gifting (82335/82336/82337/82339) ----

    private async Task<(BotClient Bot, long RoleId)> EnterRecipient(string account, string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync(account, "pw"); await bot.EnterGateAsync();
        long role = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(role);
        await Next(bot, ShopService.ShopSync, DBShopInfoIncrement.Parser);
        await Next(bot, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser);
        return (bot, role);
    }

    private static Task<CommodityPresentResp> Present(BotClient bot, long targetId, int commodity, string content = "") =>
        PresentRaw(bot, targetId, new BuyCommodityReq { CommodityId = commodity, Count = 1 }, content);

    private static async Task<CommodityPresentResp> PresentRaw(BotClient bot, long targetId, BuyCommodityReq buy, string content)
    {
        await bot.SendGameAsync(ShopService.CommodityPresent, new CommodityPresentReq { TargetId = targetId, BuyReq = buy, GiftContent = content });
        return await Next(bot, ShopService.CommodityPresent, CommodityPresentResp.Parser);
    }

    private static async Task<CommodityPickResp> Pick(BotClient bot, long orderId)
    {
        await bot.SendGameAsync(ShopService.CommodityPick, new CommodityPickReq { OrderId = orderId });
        return await Next(bot, ShopService.CommodityPick, CommodityPickResp.Parser);
    }

    [ClientDataFact]
    public async Task GiftedAppearanceChargesTheSenderAndUnlocksOnlyAfterTheRecipientPicksIt()
    {
        await using var alice = await Enter();
        var (bob, bobId) = await EnterRecipient("fashionbob", "FashionBob");
        await using var bobDispose = bob;
        await Service<FriendStore>().LinkAsync(Alice.Role!.Id, bobId);
        long crystals = Crystals;

        var sent = await Present(alice, bobId, TeaParty, "enjoy!");
        Assert.Equal((0, TeaParty, bobId), (sent.Result, sent.CommodityId, sent.TargetId));
        Assert.NotEqual(0, sent.OrderId);
        Assert.Equal(crystals - 1880, Crystals); // paid on send, not on pick
        var sentList = await Next(bob, ShopService.CommodityPresentListNtf, CommodityPresentListNtf.Parser,
            n => n.Orders.Any(o => o.OrderId == sent.OrderId));
        var order = Assert.Single(sentList.Orders, o => o.OrderId == sent.OrderId);
        Assert.Equal((Alice.Role!.Id, Alice.Role!.Name, TeaPartySuit, "enjoy!", 1), (order.SenderId, order.SenderName, order.AppranceId, order.GiftContent, order.State));

        var picked = await Pick(bob, sent.OrderId);
        Assert.Equal(0, picked.Result);
        var unlocked = await Next(bob, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser, i => i.AllLightUpAppearances.ContainsKey(TeaPartySuit));
        Assert.True(unlocked.AllLightUpAppearances.ContainsKey(TeaPartyHat));
        Assert.Equal(crystals - 1880, Crystals); // the recipient pays nothing
        var bobSession = Service<SessionRegistry>().All().Single(s => s.Account == "fashionbob" && s.Role != null);
        Assert.Equal(1, bobSession.Items.Where(i => i.Entry == TeaPartyBonus).Sum(i => i.Stack)); // same bonus a direct purchase grants

        var updated = await Next(alice, ShopService.SentGiftsListNtf, SentGiftsListNtf.Parser,
            n => n.SentGifts.Any(o => o.OrderId == sent.OrderId && o.State == 2)); // AlreadyTake, not the earlier WaitTake push
        Assert.Single(updated.SentGifts, o => o.OrderId == sent.OrderId);

        // Picking again finds nothing left to pick.
        Assert.Equal(ErrorCodes.ShopGiftOrderMissing, (await Pick(bob, sent.OrderId)).Result);
    }

    [ClientDataFact]
    public async Task GiftRefusalsChargeNothing()
    {
        await using var alice = await Enter();
        var (bob, bobId) = await EnterRecipient("fashionbobrefuse", "FashionBobRefuse");
        await using var bobDispose = bob;
        long crystals = Crystals;

        Assert.Equal(ErrorCodes.ShopGiftFriendsOnly, (await Present(alice, bobId, TeaParty)).Result);
        Assert.Equal(ErrorCodes.ShopGiftSelf, (await Present(alice, Alice.Role!.Id, TeaParty)).Result);
        Assert.Equal(ErrorCodes.ShopGiftNotAllowed, (await Present(alice, bobId, Potions)).Result); // not a Fashion commodity
        Assert.Equal(ErrorCodes.ShopGiftOrderMissing, (await Pick(alice, 123456789)).Result);

        await Service<FriendStore>().LinkAsync(Alice.Role!.Id, bobId);
        Assert.Equal(ErrorCodes.ShopGiftMessageLength, (await Present(alice, bobId, TeaParty, new string('x', 21))).Result);
        var first = await Present(alice, bobId, TeaParty);
        Assert.Equal(0, first.Result);
        // A second, still-unclaimed gift of the same appearance to the same recipient is refused.
        Assert.Equal(ErrorCodes.ShopGiftDuplicate, (await Present(alice, bobId, TeaParty)).Result);

        Assert.Equal(crystals - 1880, Crystals); // only the one accepted gift was paid
    }

    [ClientDataFact]
    public async Task GiftOfAnAppearanceTheRecipientAlreadyOwnsIsRefusedAtPresentTime()
    {
        await using var alice = await Enter();
        var (bob, bobId) = await EnterRecipient("fashionbobowns", "FashionBobOwns");
        await using var bobDispose = bob;
        await Service<FriendStore>().LinkAsync(Alice.Role!.Id, bobId);
        await Gm("!lvl 10", "fashionbobowns"); // FunctionConfig 70502-70509 open at base level 10 (conditions 2000302-2000307)
        await Gm("!item 3 10000", "fashionbobowns");
        Assert.Equal(0, (await Buy(bob, Macaron, price: 480)).Result);
        await Next(bob, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser, i => i.AllLightUpAppearances.ContainsKey(MacaronHair));
        long crystals = Crystals;

        Assert.Equal(ErrorCodes.ShopAppearanceOwned, (await Present(alice, bobId, Macaron)).Result);
        Assert.Equal(crystals, Crystals); // refused before any payment
    }

    private const int GiftRefundMailTemplate = 1115; // PublicParamConfig.PresentRefundMailId

    /// <summary>A mailbox never written to is an empty (not null) blob, not valid JSON.</summary>
    private MailService.Mailbox Mailbox(long roleId) =>
        Service<RoleStore>().LoadMailbox(roleId) is { Length: > 0 } bytes
            ? JsonSerializer.Deserialize<MailService.Mailbox>(bytes)! : new MailService.Mailbox();

    [ClientDataFact]
    public async Task ExpiredGiftIsSettledAndRefundedByMailWhenEitherSideNextActs()
    {
        await using var alice = await Enter();
        var (bob, bobId) = await EnterRecipient("fashionbobexpire", "FashionBobExpire");
        await using var bobDispose = bob;
        await Service<FriendStore>().LinkAsync(Alice.Role!.Id, bobId);
        long crystals = Crystals;

        var sent = await Present(alice, bobId, TeaParty);
        Assert.Equal(0, sent.Result);
        Assert.Equal(crystals - 1880, Crystals);
        _clock.Advance(TimeSpan.FromHours(25)); // past PublicParamConfig.OrderExpireTime (24h)

        // Bob's own next action (an unrelated, refused present) still settles the stale order and mails Alice her
        // payment back: PresentAsync settles everything the lock covers before even validating this request.
        // The attachment sits in her mailbox until claimed; it does not touch her live currency by itself.
        Assert.NotEqual(0, (await Present(bob, 424242, Stroll)).Result); // whatever it refuses on first; bob never leveled up
        Assert.Equal(crystals - 1880, Crystals);
        var refund = Assert.Single(Mailbox(Alice.Role!.Id).Letters, l => l.TemplateId == GiftRefundMailTemplate);
        Assert.Equal((Fashion, 1880), (Assert.Single(refund.Attachments).Entry, refund.Attachments[0].Count));

        // Picking the now-settled order finds nothing left: it cannot be settled, and so refunded, a second time.
        Assert.Equal(ErrorCodes.ShopGiftOrderMissing, (await Pick(bob, sent.OrderId)).Result);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public void Set(string value) => Interlocked.Exchange(ref _seconds,
            DateTimeOffset.Parse(value + "Z", null, global::System.Globalization.DateTimeStyles.AssumeUniversal).ToUnixTimeSeconds());
        public void Advance(TimeSpan by) => Interlocked.Add(ref _seconds, (long)by.TotalSeconds);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
