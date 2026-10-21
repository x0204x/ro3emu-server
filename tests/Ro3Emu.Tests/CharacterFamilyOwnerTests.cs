using Google.Protobuf;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.LoadTest;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>C/D 13a/E05a: real dispatcher, deadline and GM competitors, and exact economic outcomes.</summary>
public sealed class CharacterFamilyOwnerTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(RepoRoot(), ".loadtest-temp", "cd13a-character-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private GameSession Alice => Service<SessionRegistry>().All().Single(s => s.Account == "alice");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly Lazy<EconomyCatalog> Catalog = new(() => EconomyCatalog.Load(Path.Combine(RepoRoot(), "data")));
    private EconomySnapshot Cut() => S3EconomyReader.Read(_directory, Catalog.Value);
    private EconomyLedgerWindow Window(EconomySnapshot a, EconomySnapshot b) =>
        S3EconomyReader.ReadLedger(_directory, Catalog.Value, a, b)!;

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        await _app.StartAsync(); _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync(); SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task<BotClient> Enter(string name = "Alice")
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long role = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(role);
        await Next(bot, ShopService.ShopSync, DBShopInfoIncrement.Parser);
        // C/D 15: the login requests its scene's stall maintenance instead of running it; run it now, so the
        // realm's system stall receipts land before the test's opening cut as they did when the login ran it.
        await Service<GameLoop>().InvokeAsync(() => Service<StallsService>().RefreshWorldMaintenanceAsync());
        return bot;
    }

    private async Task Gm(string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync(_http + "/ro3emu/gm?account=alice", new StringContent(command))).EnsureSuccessStatusCode();
    }
    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait)).Body);
    private Task Owned(Func<GameSession, Task> work) => Service<GameLoop>().InvokeAsync(() =>
        Service<OwnerRegistry>().RunSessionAsync("13a/test", [ReservationKey.Role(Alice.Role!.Id)], Alice, () => work(Alice)));

    private static void Exact(EconomySnapshot a, EconomySnapshot b, EconomyLedgerWindow ledger)
    {
        Assert.True(ledger.CompleteHistory, string.Join(";", ledger.Issues.Select(x => x.Detail)));
        var result = EconomyExactnessChecker.Check(a, b, ledger);
        Assert.DoesNotContain(result.Issues, i => i.Kind == EconomyIssueKind.Corruption);
        Assert.All(result.Assets, x => { Assert.Equal("0", x.Residual); Assert.Equal("0", x.LegResidual); });
        Assert.All(result.Locations, x => Assert.Equal("0", x.Residual));
    }

    [ClientDataFact]
    public async Task HeldEquipDefersTimerUseAndGmWhileOtherRoleFinishes()
    {
        await using var alice = await Enter(); await using var bob = await Enter("Bob");
        // Both roles' post-scene-load publication reserves Realm, and a session's later messages queue behind it.
        // Let it finish before holding a commit, or the other role's request waits for the held one (C/D 15).
        for (var until = DateTime.UtcNow.AddSeconds(10); Service<SessionRegistry>().All().Any(x => x.Role != null && !x.GuildOnlinePublished);)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("scene online publication did not finish");
            await Task.Delay(10);
        }
        var s = Alice;
        var dagger = s.Items.Single(i => i.Entry == 2010121);
        var potion = s.Items.Single(i => i.Entry == 1000000);
        int oldPotions = potion.Stack;
        var opening = Cut();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gmDeferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timerDeferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timerDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Service<GameLoop>().InvokeAsync(() =>
        {
            Service<AccountStore>().BeforeOwnedCommitForTest = label =>
            { if (label != "economy/cd13a/character-equip") return Task.CompletedTask; entered.TrySetResult(); return release.Task; };
            Service<OwnerRegistry>().DeferredForTest = origin =>
            { if (origin == "GM") gmDeferred.TrySetResult(); if (origin == "deadline/13a-use") timerDeferred.TrySetResult(); };
            return Task.CompletedTask;
        });
        Task? gm = null;
        try
        {
            await alice.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { ItemGUID = dagger.Guid, Pos = (EquipGridType)1 });
            await entered.Task.WaitAsync(Wait);
            gm = Gm("!item 1200000 3");
            await Service<GameLoop>().InvokeAsync(() =>
            {
                Service<Deadlines>().Set(s, "13a-use", DateTimeOffset.UtcNow.AddSeconds(-1), async (owner, _, lease, ct) =>
                {
                    await Service<CharacterService>().UseItemAsync(owner, new ItemUseReq { ItemGuid = potion.Guid, ItemCount = 1 });
                    timerDone.TrySetResult();
                });
                return Task.CompletedTask;
            });
            await Task.WhenAll(gmDeferred.Task, timerDeferred.Task).WaitAsync(Wait);
            await bob.SendGameAsync(CharacterService.QuickUseSave, new QuickUseSaveReq { EntryItem = 1000000 });
            Assert.Equal(0, (await Next(bob, CharacterService.QuickUseSave, QuickUseSaveRes.Parser)).Result);
            Assert.False(gm.IsCompleted); Assert.False(timerDone.Task.IsCompleted);
            Assert.Equal(0, dagger.Grid); Assert.Equal(oldPotions, potion.Stack);
            Assert.Equal(0, Service<ItemStore>().Load(s.Role!.Id).Single(i => i.Guid == dagger.Guid).Grid);
            Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CharacterService.EquipItemOpr));
        }
        finally
        {
            release.TrySetResult();
            await Service<GameLoop>().InvokeAsync(() =>
            { Service<AccountStore>().BeforeOwnedCommitForTest = null; Service<OwnerRegistry>().DeferredForTest = null; return Task.CompletedTask; });
        }
        Assert.Equal(0, (await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        await Task.WhenAll(gm!, timerDone.Task).WaitAsync(Wait);
        Assert.Same(dagger, s.Items.Single(i => i.Guid == dagger.Guid));
        Assert.Equal(1, dagger.Grid); Assert.Equal(oldPotions - 1, potion.Stack);
        Assert.Equal(dagger.Guid, s.EquipmentPlans.EquipPlans[0].ItemsEquipGrid[1]);
        Assert.Equal(3, s.Items.Where(i => i.Entry == 1200000).Sum(i => i.Stack));
        var closing = Cut(); var ledger = Window(opening, closing); Exact(opening, closing, ledger);
        Assert.Contains(ledger.Legs, x => x.Reason == EconomyReason.InventoryRelocation && x.Asset.Id == dagger.Entry);
        Assert.Contains(ledger.Legs, x => x.Reason == EconomyReason.ItemUse && x.Asset.Id == 1000000 && x.Delta == -1);
    }

    [ClientDataFact]
    public async Task ItemUseReceiptFailureRollsBackCooldownAndGuidThenLostAckResolvesOnce()
    {
        await using var bot = await Enter();
        var s = Alice; var item = s.Items.Single(i => i.Entry == 1000000);
        int count = item.Stack; byte[] bag = s.BagState.Info.ToByteArray();
        var beforeRevisions = Service<RoleStore>().ReadRevisionVector(s.Role!.Id);
        var opening = Cut();
        FixtureSql.Execute(Service<AccountStore>(), """
            CREATE TRIGGER fail_13a_receipt BEFORE INSERT ON operation_receipt
            WHEN NEW.command_kind='cd13a/character-use' BEGIN SELECT RAISE(ABORT,'13a receipt fault'); END
            """);
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => Owned(owner => Service<CharacterService>().UseItemAsync(owner,
                new ItemUseReq { ItemGuid = item.Guid, ItemCount = 1 })));
            Assert.Equal(count, item.Stack); Assert.Equal(bag, s.BagState.Info.ToByteArray());
            Assert.Equal(count, Service<ItemStore>().Load(s.Role!.Id).Single(i => i.Guid == item.Guid).Stack);
            var rolledBack = Service<RoleStore>().ReadRevisionVector(s.Role.Id);
            foreach (string part in new[] { "inventory", "bag_state", "equip_plans", "stat_plans", "card_plans", "shop" })
                Assert.Equal(beforeRevisions.Revision(part), rolledBack.Revision(part));
            Assert.Empty(Window(opening, Cut()).Receipts);
        }
        finally { FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_13a_receipt"); }
        Service<AccountStore>().FailNextEconomyAcknowledgementForTest();
        await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = item.Guid, ItemCount = 1 });
        var reply = await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser);
        Assert.Equal(0, reply.Result); Assert.Equal(item.Guid, reply.ItemGuid);
        Assert.Equal(count - 1, item.Stack);
        Assert.Equal(reply.ItemCd, DBItemsInfo.Parser.ParseFrom(Service<RoleStore>().LoadBagState(s.Role!.Id)!).ItemCds.Data[1000000]);
        var closing = Cut(); var ledger = Window(opening, closing);
        var receipt = EconomyReceiptReader.Read(Service<AccountStore>().DatabasePath,
            Assert.Single(ledger.Receipts).OperationId)!;
        using var before = JsonDocument.Parse(receipt.BeforeRevisions);
        using var after = JsonDocument.Parse(receipt.AfterRevisions);
        var committed = Service<RoleStore>().ReadRevisionVector(s.Role.Id);
        Assert.Equal(new[] { "bag_state", "inventory" }, after.RootElement.GetProperty("parts").EnumerateObject().Select(p => p.Name).Order().ToArray());
        foreach (string part in new[] { "inventory", "bag_state" })
        {
            Assert.Equal(beforeRevisions.Revision(part), before.RootElement.GetProperty("parts").GetProperty(part).GetInt64());
            Assert.Equal(beforeRevisions.Revision(part) + 1, after.RootElement.GetProperty("parts").GetProperty(part).GetInt64());
            Assert.Equal(beforeRevisions.Revision(part) + 1, committed.Revision(part));
            Assert.Equal(committed.Revision(part), s.Owner!.Revisions[part]);
        }
        foreach (string part in new[] { "equip_plans", "stat_plans", "card_plans", "shop" })
            Assert.Equal(beforeRevisions.Revision(part), committed.Revision(part));
        Assert.Equal(-1, Assert.Single(ledger.Legs).Delta); Exact(opening, closing, ledger);
    }

    [ClientDataTheory]
    [InlineData(118, "npc-shop-buy", 1000000, 100)]
    [InlineData(12105, "cd13a/shop-buy", 1003100, 1000)]
    public async Task EveryShopCommitsPaymentGoodsAndCounterBeforeApply(int offer, string kind, int goods, int price)
    {
        await using var bot = await Enter(); await Gm("!lvl 10"); await Gm("!item 1 10000");
        var s = Alice; long balance = s.Progress!.Currency[1]; int oldGoods = s.Items.Where(i => i.Entry == goods).Sum(i => i.Stack);
        var opening = Cut();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Service<AccountStore>().BeforeOwnedApplyForTest = label =>
        { if (label != "economy/" + kind) return Task.CompletedTask; entered.TrySetResult(); return release.Task; };
        try
        {
            await bot.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = offer, Count = 2 });
            await entered.Task.WaitAsync(Wait);
            Assert.Equal(balance, s.Progress.Currency[1]); Assert.False(s.Shop!.Bought.ContainsKey(offer));
            Assert.Equal(oldGoods, s.Items.Where(i => i.Entry == goods).Sum(i => i.Stack));
            Assert.Equal(balance - 2 * price, Service<RoleStore>().LoadProgress(s.Role!.Id).Currency[1]);
            Assert.Equal(oldGoods + 2, Service<ItemStore>().Load(s.Role.Id).Where(i => i.Entry == goods).Sum(i => i.Stack));
            Assert.DoesNotContain(bot.Inbox.Snapshot(), e => e.IsGame(ShopService.BuyCommodity));
        }
        finally { release.TrySetResult(); Service<AccountStore>().BeforeOwnedApplyForTest = null; }
        var reply = await Next(bot, ShopService.BuyCommodity, BuyCommodityResp.Parser);
        Assert.Equal((0, 2, price), (reply.Result, reply.BuyTimes, reply.Price));
        var closing = Cut(); var ledger = Window(opening, closing);
        Assert.Equal(kind, Assert.Single(ledger.Receipts).CommandKind);
        Assert.Equal(balance - 2 * price, s.Progress.Currency[1]); Assert.Equal(2, s.Shop!.Bought[offer].Count);
        Exact(opening, closing, ledger);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
