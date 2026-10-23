using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Level/exp, stat points, bag, equipment and GM commands.</summary>
public sealed class CharacterTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-char-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly int[] NoviceInitialSkillGroups = [10000, 10002, 10003];
    private WebApplication _app = null!;
    private string _http = "";

    private const int Potion = 1000000, Dagger = 2010121, Shirt = 2550120;

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        // Game tables come from the repository's data/config export (tools/ConfigExport).
        string gameData = Path.Combine(RepoRoot(), "data");
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = gameData;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string account, string line)
    {
        using var http = new HttpClient();
        var resp = await http.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(line));
        resp.EnsureSuccessStatusCode();
    }

    [ClientDataFact]
    public async Task LoginSyncsLevelPointsBagAndStats()
    {
        await using var alice = await InWorld("Alice");
        var info = await Next(alice, CharacterService.RoleBaseInfoSync, RoleBaseInfoIncrement.Parser);
        Assert.True(info.SyncAllMark);
        Assert.Equal((long)alice.Self!.Uuid, info.Id);
        Assert.Equal(1000, info.Career);
        Assert.Equal(1, info.Level);
        Assert.Equal(1, info.JobLevel);

        var points = await Next(alice, CharacterService.BaseAttrSync, BaseAttrInfoIncrement.Parser);
        var plan = points.AllPlanBaseProperty[0].All;
        Assert.Equal(48, plan.AttrPoints);
        Assert.Equal(6, plan.BaseProperty.Count);

        var bag = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        var consumables = bag.PackInfo[1].All;
        var potion = Assert.Single(consumables.Items.Values);
        Assert.Equal(Potion, potion.ItemEntry);
        Assert.Equal(20, potion.Stack);
        Assert.True(potion.ItemInfos.ContainsKey(10001));
        Assert.NotNull(potion.ItemBindInfo);
        Assert.Contains(potion.ItemGuid, consumables.SortedIndex.Keys);
        var gear = bag.PackInfo[2].All.Items.Values;
        Assert.Equal(new[] { Dagger, Shirt }, gear.Select(i => i.ItemEntry).Order());
        Assert.All(gear, i => Assert.Equal(1, i.ItemInfos[3].LongVal)); // appraised
        Assert.Equal(1000, bag.Currency[1].All);

        var equip = await Next(alice, CharacterService.EquipSync, DBEquipInfoIncrement.Parser);
        Assert.Equal(0, equip.CurrentEquipPlan);

        // Novice lv1 (AttriBaseLvConfig 1000001: MaxHP 30) + VIT 1 * 40 = 70, * (1 + 0.2% per VIT) = 70.14, rounded up like the client = 71.
        var props = alice.Self!.Property.ToDictionary(p => p.Idx, p => p.Data);
        Assert.Equal(1, props[1]);
        Assert.Equal(71, props[101]);
        Assert.Equal(props[101], props[2000]);
        Assert.Equal(700, props[113]);
        Assert.Equal(1, props[2002]);
        Assert.Equal(new long[7], alice.Self.EquipInfo);
    }

    [ClientDataFact]
    public async Task EquippingTheDaggerShowsItOnTheModelForEveryone()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var bag = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        long dagger = bag.PackInfo[2].All.Items.Values.Single(i => i.ItemEntry == Dagger).ItemGuid;
        long atkBefore = alice.Self!.Property.Single(p => p.Idx == 103).Data;

        await alice.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = dagger });
        var removed = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => !m.SyncAllMark);
        Assert.True(removed.PackInfo[2].Inc.Items[dagger].Del);
        var equipped = await Next(alice, CharacterService.EquipSync, DBEquipInfoIncrement.Parser, m => !m.SyncAllMark);
        Assert.Equal(Dagger, equipped.Items[dagger].All.ItemEntry);
        Assert.Equal(dagger, equipped.EquipPlans[0].Inc.ItemsEquipGrid[1].All); // existing plan: nested increment
        var resp = await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser);
        Assert.Equal(0, resp.Result);
        Assert.Equal(Dagger, resp.Item.ItemEntry);

        foreach (var bot in new[] { alice, bob })
        {
            var look = await Next(bot, CharacterService.EntityBaseChange, EntityBase.Parser);
            Assert.Equal(EntityBase.Types.NotifyType.Equip, look.NotifyType);
            Assert.Equal(alice.Self.Uuid, look.Uuid);
            Assert.Equal(new long[] { Dagger, 0, 0, 0, 0, 0, 0 }, look.EquipInfo);
        }
        var stats = await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser);
        Assert.Equal(atkBefore + 7, stats.Property.Propertys.Single(p => p.PropertyId == 103).Data); // EquipConfig: ATK 7

        // Unequip puts it back in the bag and clears the hand.
        await alice.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { Opr = (EquipItemOprType)1, Pos = (EquipGridType)1, ItemGUID = dagger });
        var back = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => !m.SyncAllMark && m.PackInfo.ContainsKey(2) && m.PackInfo[2].Inc?.Items.ContainsKey(dagger) == true && !m.PackInfo[2].Inc.Items[dagger].Del);
        Assert.Equal(Dagger, back.PackInfo[2].Inc.Items[dagger].All.ItemEntry);
        var bare = await Next(bob, CharacterService.EntityBaseChange, EntityBase.Parser);
        Assert.Equal(new long[7], bare.EquipInfo);
    }

    [ClientDataFact]
    public async Task EquipmentAndLevelSurviveRelog()
    {
        long dagger;
        await using (var alice = await InWorld("Alice"))
        {
            var bag = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
            dagger = bag.PackInfo[2].All.Items.Values.Single(i => i.ItemEntry == Dagger).ItemGuid;
            await alice.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = dagger });
            await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser);
            await Gm("alice", "!lvl 12 5");
            await Next(alice, CharacterService.RoleBaseInfoSync, RoleBaseInfoIncrement.Parser, m => m.Level == 12);
        }
        await using var again = await InWorld("Alice", create: false);
        Assert.Equal(new long[] { Dagger, 0, 0, 0, 0, 0, 0 }, again.Self!.EquipInfo);
        var info = await Next(again, CharacterService.RoleBaseInfoSync, RoleBaseInfoIncrement.Parser);
        Assert.Equal(12, info.Level);
        Assert.Equal(5, info.JobLevel);
        var equip = await Next(again, CharacterService.EquipSync, DBEquipInfoIncrement.Parser);
        Assert.Equal(dagger, equip.EquipPlans[0].All.ItemsEquipGrid[1]);
        var bagAgain = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        Assert.DoesNotContain(dagger, bagAgain.PackInfo[2].All.Items.Keys);
    }

    [ClientDataFact]
    public async Task GmLevelGrantsStatPointsAndPointsCanBeSpent()
    {
        await using var alice = await InWorld("Alice");
        await Next(alice, CharacterService.BaseAttrSync, BaseAttrInfoIncrement.Parser);

        await Gm("alice", "!lvl 10");
        var level = await Next(alice, CharacterService.RoleBaseInfoSync, RoleBaseInfoIncrement.Parser, m => !m.SyncAllMark);
        Assert.Equal(10, level.Level);
        var points = await Next(alice, CharacterService.BaseAttrSync, BaseAttrInfoIncrement.Parser, m => !m.SyncAllMark);
        Assert.Equal(48 + 8 * 2 + 4, points.AllPlanBaseProperty[0].Inc.AttrPoints); // BaseLvConfig: lv2-9 +2, lv10 +4

        // STR 1 -> 5 costs 4 x 2 (BasePointConfig); a value lower than now is refused.
        var req = new AllocateAttrPointReq { PlanIndex = 0 };
        foreach (var (stat, value) in new[] { (1, 5), (2, 1), (3, 1), (4, 1), (5, 1), (6, 1) }) req.Points[stat] = value;
        await alice.SendGameAsync(CharacterService.AllocateAttrPoints, req);
        var ok = await Next(alice, CharacterService.AllocateAttrPoints, AllocateAttrPointRsp.Parser);
        Assert.Equal(0, ok.Result);
        Assert.Equal(5, ok.PlanBaseProperty.BaseProperty[1]);
        Assert.Equal(8, ok.PlanBaseProperty.UsedAttrPoints);
        Assert.Equal(68 - 8, ok.PlanBaseProperty.AttrPoints);
        var str = await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser, m => m.Property.Propertys.Any(p => p.PropertyId == 1 && p.Data == 5));
        Assert.Contains(str.Property.Propertys, p => p.PropertyId == 103);

        req.Points[1] = 2;
        await alice.SendGameAsync(CharacterService.AllocateAttrPoints, req);
        Assert.NotEqual(0, (await Next(alice, CharacterService.AllocateAttrPoints, AllocateAttrPointRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task ExpLevelsUpThroughTheTable()
    {
        await using var alice = await InWorld("Alice");
        await Next(alice, CharacterService.RoleBaseInfoSync, RoleBaseInfoIncrement.Parser);
        await Gm("alice", "!exp 15 2"); // BaseLvConfig lv1 needs 5, lv2 8 -> lv3 with 2; JobLvConfig 101/102 need 1 each -> job 3
        var info = await Next(alice, CharacterService.RoleBaseInfoSync, RoleBaseInfoIncrement.Parser, m => !m.SyncAllMark);
        Assert.Equal(3, info.Level);
        Assert.Equal(2, info.Exp);
        Assert.Equal(3, info.JobLevel);
        var reward = await Next(alice, CharacterService.GetRewardNotify, GetRewardNotify.Parser);
        Assert.Equal(100, reward.Rewards[0].ItemEntry);
    }

    [ClientDataFact]
    public async Task JobChangeSkipsTheFreeJobLevelsAndChangesTheModel()
    {
        await using var alice = await InWorld("Alice");
        await Next(alice, CharacterService.RoleBaseInfoSync, RoleBaseInfoIncrement.Parser);
        await Gm("alice", "!job 1100");
        var info = await Next(alice, CharacterService.RoleBaseInfoSync, RoleBaseInfoIncrement.Parser, m => !m.SyncAllMark);
        Assert.Equal(1100, info.Career);
        Assert.Equal(15, info.JobLevel); // JobLvConfig 201-214 need 0 exp
        var look = await Next(alice, CharacterService.EntityBaseChange, EntityBase.Parser);
        Assert.Equal(EntityBase.Types.NotifyType.Base, look.NotifyType);
        Assert.Equal(1100UL, look.EntityTemplateID);
    }

    [ClientDataFact]
    public async Task TransformJobChecksLevelsThenRepliesBeforeTheModelChange()
    {
        await using var alice = await InWorld("Alice");
        await Next(alice, CharacterService.RoleBaseInfoSync, RoleBaseInfoIncrement.Parser);
        await alice.SendGameAsync(CharacterService.PlayerTransformJob, new PlayerTransformJobReq { JobId = 1100 });
        var refused = await Next(alice, CharacterService.PlayerTransformJob, PlayerTransformJobResp.Parser);
        Assert.Equal(2058, refused.Result); // Base 1 < TransformJobLevelLimit rank 2 (10)

        await alice.SendGameAsync(CharacterService.PlayerTransformJob, new PlayerTransformJobReq { JobId = 2110 });
        Assert.NotEqual(0, (await Next(alice, CharacterService.PlayerTransformJob, PlayerTransformJobResp.Parser, r => r.JobId == 2110)).Result);

        await Gm("alice", "!lvl 10");
        await Gm("alice", "!exp 0 1000");
        await alice.SendGameAsync(CharacterService.PlayerTransformJob, new PlayerTransformJobReq { JobId = 1100 });
        var ok = await Next(alice, CharacterService.PlayerTransformJob, PlayerTransformJobResp.Parser);
        Assert.Equal(0, ok.Result);
        Assert.Equal(1100, ok.JobId);
        var info = await Next(alice, CharacterService.RoleBaseInfoSync, RoleBaseInfoIncrement.Parser, m => m.Career == 1100);
        Assert.Equal(15, info.JobLevel); // new rank from job level 1, through the free JobLvConfig rows
        var look = await Next(alice, CharacterService.EntityBaseChange, EntityBase.Parser, e => e.NotifyType == EntityBase.Types.NotifyType.Base);
        Assert.Equal(1100UL, look.EntityTemplateID);
    }

    [Fact]
    public async Task ARoleCanBeDeletedWithTheAccountPasswordAndItsNameReused()
    {
        var pem = File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem"));
        await using (var bot = new BotClient(_http, pem))
        {
            await bot.LoginAsync("carol", "pw");
            await bot.EnterGateAsync();
            await bot.CreateRoleAsync("Carol");
            await bot.CreateRoleAsync("Carol2");
        }
        using var http = new HttpClient();
        async Task<int> Delete(string password, string role)
        {
            var resp = await http.PostAsync($"{_http}/ro3emu/account/delete-role", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["identity"] = "carol", ["credential"] = password, ["role"] = role,
            }));
            using var json = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("code").GetInt32();
        }
        Assert.Equal(401, await Delete("wrong", "Carol2"));
        Assert.Equal(404, await Delete("pw", "Nobody"));
        Assert.Equal(0, await Delete("pw", "Carol2"));

        await using var again = new BotClient(_http, pem);
        await again.LoginAsync("carol", "pw");
        await again.EnterGateAsync();
        Assert.Equal("Carol", Assert.Single(again.Roles).Name);
        Assert.Equal(0, (await again.CreateRoleAsync("Carol2")).Result); // the name is free again
    }

    [ClientDataFact]
    public async Task ACardGoesIntoTheWeaponSocketAndBackAndCountsInStats()
    {
        const int Lunatic = 1420100; // Card: Lunatic, part 1 (weapon): CardConfig tier 0 LUK +1, CRI +100, FLEE +1
        await using var alice = await InWorld("Alice");
        var bag = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        long dagger = bag.PackInfo[2].All.Items.Values.Single(i => i.ItemEntry == Dagger).ItemGuid;
        await Gm("alice", "!lvl 10"); // main-hand socket 1 opens at base level 10 (EquipGridConfig 1, condition 4000000)
        await Gm("alice", $"!item {Lunatic} 2");
        var got = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.PackInfo.ContainsKey(3));
        var cardInfo = got.PackInfo[3].All?.Items.Values.Single(i => i.ItemEntry == Lunatic) ?? got.PackInfo[3].Inc.Items.Values.Single(i => i.All?.ItemEntry == Lunatic).All;
        Assert.True(cardInfo.ItemInfos.ContainsKey(502)); // card star
        var library = await Next(alice, CardService.LibrarySync, DBLibraryInfoIncrement.Parser);
        Assert.False(library.Cards[Lunatic].All.Unlock);

        // No weapon worn: the main-hand socket is closed.
        await alice.SendGameAsync(CardService.SlotInsert, new EquipmentCardInsertReq { EquipPos = (EquipGridType)1, CardGuid = cardInfo.ItemGuid, SlotIndex = 1 });
        Assert.NotEqual(0, (await Next(alice, CardService.SlotInsert, EquipmentCardInsertResp.Parser)).Result);

        await alice.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = dagger });
        await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser);
        var before = await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser);
        long luk = before.Property.Propertys.Single(p => p.PropertyId == 6).Data;

        await alice.SendGameAsync(CardService.SlotInsert, new EquipmentCardInsertReq { EquipPos = (EquipGridType)1, CardGuid = cardInfo.ItemGuid, SlotIndex = 1 });
        var socket = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.StoragePack != null);
        Assert.True(socket.SyncAllMark); // the first socketed card: 5009 whole again (CharacterService.SyncItemsAsync)
        Assert.Equal(Lunatic, socket.StoragePack.All.CardInfo[1].CardSlotInfo[1].ItemEntry);
        // Earlier property messages (the level-up, the equip) are still queued: wait for the one with the card's LUK.
        await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser, m => m.Property.Propertys.Any(p => p.PropertyId == 6 && p.Data == luk + 1));
        var plan = await Next(alice, CardService.StorageHoleSync, DBStorageHoleInfoIncrement.Parser, m => !m.SyncAllMark);
        Assert.Equal(Lunatic, plan.StoragePlans[1].Inc.CardInfo[1].All.CardSlotInfo[1].ItemId);
        var insert = await Next(alice, CardService.SlotInsert, EquipmentCardInsertResp.Parser);
        Assert.Equal(0, insert.Result);

        // Out again: back on the bag stack, the socket map keeps its outer table (deletions only).
        await alice.SendGameAsync(CardService.SlotRemove, new EquipmentCardRemoveReq { EquipPos = (EquipGridType)1, SlotIndex = 1 });
        var removed = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.StoragePack?.Inc != null);
        Assert.True(removed.StoragePack.Inc.CardInfo[1].Del);
        Assert.Equal(0, (await Next(alice, CardService.SlotRemove, EquipmentCardRemoveResp.Parser)).Result);

        await alice.SendGameAsync(CardService.LibraryCardUnlock, new LibraryCardUnlockReq { CardId = Lunatic });
        var unlocked = await Next(alice, CardService.LibrarySync, DBLibraryInfoIncrement.Parser, m => !m.SyncAllMark);
        Assert.True(unlocked.Cards[Lunatic].Inc.Unlock);
    }

    [ClientDataFact]
    public async Task TheNpcShopSellsPotionsForZenyOnceItsTabIsOpen()
    {
        const int Potions = 118, BeginnerPotion = 1000000; // ShopCommodityConfig 118: class 302 (function 701102, Base 10), 100 Zeny each
        await using var alice = await InWorld("Alice");
        var shop = await Next(alice, ShopService.ShopSync, DBShopInfoIncrement.Parser);
        Assert.True(shop.SyncAllMark);
        var season = await Next(alice, SeasonService.SeasonSync, DBSeasonInfoIncrement.Parser); // the shop tabs read its playBookId
        Assert.NotEqual(0, season.Current.All.PlayBookId);
        await Gm("alice", "!item 1 1000");

        await alice.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = Potions, Count = 2, Price = 100 });
        Assert.Equal(ErrorCodes.ShopClosed, (await Next(alice, ShopService.BuyCommodity, BuyCommodityResp.Parser)).Result); // Base 1: tab closed

        await Gm("alice", "!lvl 10");
        await alice.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = Potions, Count = 2, Price = 100 });
        var bought = await Next(alice, ShopService.BuyCommodity, BuyCommodityResp.Parser);
        Assert.Equal((0, 100, 2), (bought.Result, bought.Price, bought.BuyTimes));
        var counters = await Next(alice, ShopService.ShopSync, DBShopInfoIncrement.Parser, m => !m.SyncAllMark);
        Assert.Equal(2, counters.CommodityBuyTimes[Potions].All);
        // The potions land on the starting stack (an inc) or a new one (an all).
        await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.PackInfo.Values.Any(p => p.Inc?.Items.Values.Any(i => i.All?.ItemEntry == BeginnerPotion || i.Inc?.Stack > 0) == true));

        await alice.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = Potions, Count = 1_000_000, Price = 100 });
        Assert.Equal(ErrorCodes.CurrencyShort, (await Next(alice, ShopService.BuyCommodity, BuyCommodityResp.Parser)).Result); // 100M Zeny: short
    }

    [ClientDataFact]
    public async Task CancelledShopExchangeCommitsButSuppressesOuterReplyOnlyForItsSource()
    {
        // E05a: suspend the point-shop envelope at the same writer barrier as the original exchange.
        const int commodity = 12105, potion = 1003100;
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm("alice", "!lvl 10");
        await Gm("alice", "!item 1 10000");
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var items = _app.Services.GetRequiredService<ItemStore>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        long role = (long)alice.Self!.Uuid;
        var source = sessions.All().Single(s => s.Role?.Id == role);
        var recipient = sessions.All().Single(s => s.Role?.Id == (long)bob.Self!.Uuid);
        long currency = roles.LoadProgress(role).Currency[1];
        int stack = items.Load(role).Where(i => i.Entry == potion).Sum(i => i.Stack);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        accounts.BeforeCommitForTest = label =>
        {
            if (label != "economy/cd13a/shop-buy") return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("shop exchange commit barrier");
        };
        try
        {
            var buying = loop.InvokeAsync(async () =>
            {
                await _app.Services.GetRequiredService<ShopService>().BuyAsync(source,
                    new BuyCommodityReq { CommodityId = commodity, Count = 2, Price = 1000 });
                await recipient.SendGameAsync(ShopService.BuyCommodity,
                    new BuyCommodityResp { CommodityId = 987654, Result = 0 });
            });
            Assert.True(await Task.Run(() => entered.Wait(Wait)));
            Assert.False(buying.IsCompleted);
            await loop.InvokeAsync(() =>
            {
                source.Attachment!.Cancelled = true; // the admitted mutation survives an obsolete presentation lease
                return Task.CompletedTask;
            });
            release.Set();
            await buying.WaitAsync(Wait);
            Assert.Equal(currency - 2000, roles.LoadProgress(role).Currency[1]);
            Assert.Equal(stack + 2, items.Load(role).Where(i => i.Entry == potion).Sum(i => i.Stack));
            Assert.Equal(2, source.Shop!.Bought[commodity].Count);
            Assert.Equal(987654, (await Next(bob, ShopService.BuyCommodity, BuyCommodityResp.Parser,
                r => r.CommodityId == 987654)).CommodityId);
            // A fresh, unbound permit can still address this socket. Receiving its sentinel
            // fences all earlier sends on Alice's transport queue before the absence check.
            await loop.InvokeAsync(() => source.SendGameAsync(ShopService.BuyCommodity,
                new BuyCommodityResp { CommodityId = 876543, Result = 0 }));
            Assert.Equal(876543, (await Next(alice, ShopService.BuyCommodity, BuyCommodityResp.Parser,
                r => r.CommodityId == 876543)).CommodityId);
            Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(ShopService.BuyCommodity) &&
                BuyCommodityResp.Parser.ParseFrom(e.Body).CommodityId == commodity);
            Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(ShopService.ShopSync) &&
                DBShopInfoIncrement.Parser.ParseFrom(e.Body).CommodityBuyTimes.ContainsKey(commodity));
        }
        finally
        {
            release.Set();
            accounts.BeforeCommitForTest = null;
            await loop.InvokeAsync(() => { source.Attachment!.Cancelled = false; return Task.CompletedTask; });
        }
    }

    [ClientDataFact]
    public async Task StrengtheningTheWeaponGridRaisesItsLevelAndAttack()
    {
        const int Ore = 1200200; // StrengthenTable grid 1 levels 1-2: 2 ore + 10000 of currency 2 each, ATK/MATK +2 per level
        await using var alice = await InWorld("Alice");
        var bag = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        long dagger = bag.PackInfo[2].All.Items.Values.Single(i => i.ItemEntry == Dagger).ItemGuid;
        await alice.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = dagger });
        await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser);

        await alice.SendGameAsync(EquipmentService.Strengthen, new StrengthenEquipmentReq { EquipPos = (EquipGridType)1, StrengthenNum = 2 });
        var refused = await Next(alice, EquipmentService.Strengthen, StrengthenEquipmentRsp.Parser);
        Assert.Equal((ErrorCodes.StrengthenMaterials, 0), (refused.Result, refused.StrengthenLv)); // no ore

        await Gm("alice", $"!item {Ore} 4");
        await Gm("alice", "!item 2 20000");
        await alice.SendGameAsync(EquipmentService.Strengthen, new StrengthenEquipmentReq { EquipPos = (EquipGridType)1, StrengthenNum = 2 });
        var grid = await Next(alice, CharacterService.EquipSync, DBEquipInfoIncrement.Parser, m => !m.SyncAllMark && m.ItemsStrength.ContainsKey(1));
        Assert.Equal(2, grid.ItemsStrength[1].All);
        var done = await Next(alice, EquipmentService.Strengthen, StrengthenEquipmentRsp.Parser);
        Assert.Equal((0, 2), (done.Result, done.StrengthenLv));
    }

    [ClientDataFact]
    public async Task MaterialsCombineAndDismantleByTheirItemConfig()
    {
        const int Shard = 1200000, Crystal = 1200001; // ItemConfig[1200001]: _kCombine and _kDecompose [[1200000,5]]
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 20"); // Function 2602: dismantling opens at base level 20.
        await Gm("alice", $"!item {Shard} 10");
        await alice.SendGameAsync(EquipmentService.ItemCombine, new ItemCombineReq { ItemId = Crystal, ItemCount = 2, CostItem = { [Shard] = 10 } });
        var made = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.Values.Any(p => p.Inc?.Items.Values.Any(i => i.All?.ItemEntry == Crystal) == true));
        long crystals = made.PackInfo.Values.SelectMany(p => p.Inc.Items.Values).Single(i => i.All?.ItemEntry == Crystal).All.ItemGuid;
        Assert.Equal(0, (await Next(alice, EquipmentService.ItemCombine, ItemCombineResp.Parser)).Result);

        await alice.SendGameAsync(EquipmentService.ItemCombine, new ItemCombineReq { ItemId = Crystal, ItemCount = 1, CostItem = { [Shard] = 5 } });
        Assert.Equal(ErrorCodes.ItemsShort, (await Next(alice, EquipmentService.ItemCombine, ItemCombineResp.Parser)).Result); // shards used up

        await alice.SendGameAsync(EquipmentService.ItemDecompose, new ItemDecomposeReq { DecomposeItem = { [crystals] = 1 } });
        Assert.Equal(0, (await Next(alice, EquipmentService.ItemDecompose, ItemDecomposeResp.Parser)).Result);
        await alice.SendGameAsync(EquipmentService.ItemCombine, new ItemCombineReq { ItemId = Crystal, ItemCount = 1, CostItem = { [Shard] = 5 } });
        Assert.Equal(0, (await Next(alice, EquipmentService.ItemCombine, ItemCombineResp.Parser)).Result); // the 5 shards came back
    }

    [ClientDataFact]
    public async Task CraftingARankTwoWeaponNeedsTheCraftFunction()
    {
        // EquipCraftConfig 2001: rank 2 subtype 1, 1254001 + 2x 1200704; rank-2 weights make quality 4 (2010240) certain.
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!item 1254001 2");
        await Gm("alice", "!item 1200704 4");
        await alice.SendGameAsync(EquipmentService.Craft, new EquipCraftReq { Rank = 2, Subtype = 1 });
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(alice, EquipmentService.Craft, EquipCraftResp.Parser)).Result); // function 43 closed

        await Gm("alice", "!quest done 10103020");
        await alice.SendGameAsync(EquipmentService.Craft, new EquipCraftReq { Rank = 2, Subtype = 1 });
        var made = await Next(alice, EquipmentService.Craft, EquipCraftResp.Parser);
        Assert.Equal((0, 2010240), (made.Result, made.Item.ItemEntry));
        // Rolled base stats in itemInfos 30+i (attribute) / 60+i (value): EquipConfig [[103,28,35],[104,28,35]].
        Assert.Equal((103L, 104L), (made.Item.ItemInfos[30].LongVal, made.Item.ItemInfos[31].LongVal));
        Assert.InRange(made.Item.ItemInfos[60].LongVal, 28, 35);
        Assert.InRange(made.Item.ItemInfos[61].LongVal, 28, 35);
    }

    [Fact]
    public async Task FirstHourRequestsGetTheirReplies()
    {
        await using var alice = await InWorld("Alice");
        await alice.SendGameAsync(70009, new GetSceneLineInfoReq { SceneEntry = 0 });
        var lines = await Next(alice, 70009, GetSceneLineInfoResp.Parser);
        Assert.Equal(1, Assert.Single(lines.LineInfo).Currolenum); // Alice herself on line 1

        await alice.SendGameAsync(31470, new QueryEquipRankReq { Rankid = 3, Itemuid = 42 });
        var rank = await Next(alice, 31470, QueryEquipRankRes.Parser);
        Assert.Equal((3, 42L), (rank.Rankid, (long)rank.Itemuid));
    }

    // A Novice's saved bar holds the Novice normal attack. After a job and weapon change its slot shows the current
    // JobAttackConfig attack (an Archer's bow attack 1300001), or the client keeps casting the Novice's 1000001.
    [ClientDataFact]
    public async Task ASavedNoviceAttackSlotBecomesTheCareersAttackAfterAJobChange()
    {
        await using var alice = await InWorld("Alice");
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        {
            var s = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
            s.Progress!.SkillBar[1] = 10000;
            // ... and a plan's manual bar (SkillAssignPlan.ManualLoadSkill), which the wheel prefers.
            var plan = s.SkillPlans!.SkillAssignPlans[s.SkillPlans.CurrentPlanIndex];
            plan.ManualLoadSkill.Clear();
            plan.ManualLoadSkill.Add(new SkillData { SkillId = 10000, SkillLevel = 1 });
            return Task.CompletedTask;
        });
        await Gm("alice", "!job 3100");
        const int Bow = 2110120;
        await Gm("alice", $"!item {Bow}");
        var granted = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.GetValueOrDefault(2)?.Inc?.Items.Values.Any(i => i.All?.ItemEntry == Bow) == true);
        long bow = granted.PackInfo[2].Inc.Items.Values.Single(i => i.All?.ItemEntry == Bow).All.ItemGuid;
        await alice.SendGameAsync(CharacterService.EquipItemOpr,
            new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = bow });
        Assert.Equal(0, (await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        await alice.SendGameAsync(1023, new PlayerAssetReq { AssetType = { 5 } });
        var skills = await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser, m => m.SyncAllMark && m.CurrentAtkSkillId == 1300001);
        Assert.Equal(1300001, skills.SkillWheel.All.AtkSkillId); // JobConfig 3100 _kSkillNormalID
        Assert.DoesNotContain(skills.SkillWheel.All.WhellSkills.Values, slot => slot.SkillConfigId == 1000001);
        Assert.Equal(1300001, skills.SkillWheel.All.WhellSkills[1].SkillConfigId);
    }

    [ClientDataFact]
    public async Task SkillDataCarriesTheNormalAttackAndBashCanBeLearned()
    {
        await using var alice = await InWorld("Alice");
        var skills = await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
        Assert.True(skills.SyncAllMark);
        Assert.Equal(1000001, skills.CurrentAtkSkillId);
        Assert.Equal(1000001, skills.SkillWheel.All.AtkSkillId); // the attack button / click needs it
        Assert.Equal(5, skills.MaxPlanCount); // live generic SchemeNum plans
        Assert.Equal(5, skills.SkillAssignPlans.All.Value.Count);
        Assert.Equal(0, skills.CurrentPlanIndex);
        var plan = skills.SkillAssignPlans.All.Value[skills.CurrentPlanIndex];
        Assert.Equal(NoviceInitialSkillGroups, plan.SkillAssigns.Keys.Order());
        Assert.Equal(0, plan.SkillAssigns[10002].SkillLevel); // Smite activates at base level 5

        // Keep the master's learned-Smite wheel assertion after the configured activation level.
        await Gm("alice", "!lvl 5");
        await alice.SendGameAsync(1023, new PlayerAssetReq { AssetType = { 5 } });
        skills = await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser, m => m.SyncAllMark);
        plan = skills.SkillAssignPlans.All.Value[skills.CurrentPlanIndex];
        Assert.Equal(1, plan.SkillAssigns[10002].SkillLevel);
        Assert.Contains(1000201, plan.SkillAssigns.Count > 0 ? skills.SkillWheel.All.WhellSkills.Values.Select(s => s.SkillConfigId) : []); // Smite on the bar

        await Gm("alice", "!job 1100");
        var swordman = await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser, m => !m.SyncAllMark && m.HasCurrentAtkSkillId);
        Assert.Equal(1001001, swordman.CurrentAtkSkillId); // JobAttackConfig default attack

        // Job 15 of rank 2 = 15 points (JobLvConfig 201..215); learn Bash level 3.
        var req = new SkillGrowthSkillPlanUpdateReq { Plan = 0 };
        req.LevelUpList[11001] = new SkillData { SkillId = 11001, SkillLevel = 3 };
        await alice.SendGameAsync(SkillService.PlanUpdate, req);
        var learned = await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser,
            m => m.SkillAssignPlans?.Inc?.Value.Any(pl => pl.SkillAssigns.ContainsKey(11001)) == true);
        Assert.Equal(5, learned.SkillAssignPlans.Inc.Value.Count);
        var bash = learned.SkillAssignPlans.Inc.Value[req.Plan]; // element-wise: the client listens on skillAssignPlans[plan + 1]
        // A learnable skill is already present at level zero, so allocation updates its existing map entry.
        Assert.NotNull(bash.SkillAssigns[11001].Inc);
        Assert.Equal(3, bash.SkillAssigns[11001].Inc.SkillLevel);
        Assert.Equal(12, bash.SkillPoints[2].All);
        Assert.All(learned.SkillAssignPlans.Inc.Value.Skip(1), other => Assert.DoesNotContain(11001, other.SkillAssigns.Keys));
        Assert.Equal(0, (await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task MageStaffChangesThePublishedNormalAttackImmediately()
    {
        const int Staff = 2090120;
        await using var alice = await InWorld("Alice");
        await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser, m => m.SyncAllMark);
        var startingBag = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.SyncAllMark);
        long dagger = startingBag.PackInfo[2].All.Items.Values.Single(item => item.ItemEntry == Dagger).ItemGuid;
        await alice.SendGameAsync(CharacterService.EquipItemOpr,
            new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = dagger });
        Assert.Equal(0, (await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);

        await Gm("alice", "!job 2100");
        await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser, m => !m.SyncAllMark);
        Assert.Equal(1000001, _app.Services.GetRequiredService<SkillService>().NormalAttack(2100));
        var live = _app.Services.GetRequiredService<SessionRegistry>().All().Single(session => session.Account == "alice");
        Assert.Equal(1000001, _app.Services.GetRequiredService<SkillService>().NormalAttack(live)); // incompatible saved dagger -> valid unarmed row

        await Gm("alice", $"!item {Staff}");
        var granted = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.GetValueOrDefault(2)?.Inc?.Items.Values.Any(i => i.All?.ItemEntry == Staff) == true);
        long guid = granted.PackInfo[2].Inc.Items.Values.Single(i => i.All?.ItemEntry == Staff).All.ItemGuid;
        await alice.SendGameAsync(CharacterService.EquipItemOpr,
            new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = guid });
        Assert.Equal(0, (await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        var equipped = await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser,
            m => !m.SyncAllMark && m.HasCurrentAtkSkillId);
        Assert.Equal(1299901, equipped.CurrentAtkSkillId);
        Assert.Equal(1299901, equipped.SkillWheel.Inc.AtkSkillId);
    }

    [ClientDataFact]
    public async Task GmItemStacksOntoExistingAndZenyGoesToCurrency()
    {
        await using var alice = await InWorld("Alice");
        await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);

        await Gm("alice", "!item beginner potion 5");
        var stack = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => !m.SyncAllMark);
        Assert.Equal(25, Assert.Single(stack.PackInfo[1].Inc.Items.Values).Inc.Stack); // existing item: merge

        await Gm("alice", "!zeny 500");
        var zeny = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.Currency.ContainsKey(1));
        Assert.Equal(1500, zeny.Currency[1].All);

        // A card goes to a pack the client never got: the whole pack is sent with `all`.
        await Gm("alice", "!item 2010120");
        var newItem = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.GuidToNew.Count > 0);
        Assert.Equal(2010120, newItem.PackInfo[2].Inc.Items.Values.Single().All.ItemEntry);
    }

    [Fact]
    public async Task GmPanelProbeAndCommands()
    {
        await using var alice = await InWorld("Alice");
        await alice.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq { ParamString = "ro3-gm-panel-capability-v1:3" });
        var probe = await Next(alice, GmCommands.GeneralProcGm, GeneralGMProcRsp.Parser);
        Assert.Equal("ro3-gm-panel-capability-v1:3:1", probe.ErrInfo);

        await alice.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq { GmType = (GmOpEnum)24, ParamString = "20,7" });
        var level = await Next(alice, CharacterService.RoleBaseInfoSync, RoleBaseInfoIncrement.Parser, m => !m.SyncAllMark);
        Assert.Equal(20, level.Level);
        Assert.Equal(7, level.JobLevel);
        var done = await Next(alice, GmCommands.GeneralProcGm, GeneralGMProcRsp.Parser);
        Assert.Equal(0, done.Result);
        Assert.Contains("20", done.ErrInfo);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

}
