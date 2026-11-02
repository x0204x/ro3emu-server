using System.Net.Sockets;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Wire/persistence coverage for equipment plans, instance locks and loot dismantling.</summary>
public sealed class EquipmentPlansLockTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-equipment-plans-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication _app = null!;
    private string _http = "";
    private const int Dagger = 2010121, Shirt = 2550120, PurpleWeapon = 2010240, Crystal = 1200001, Shard = 1200000;

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
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

    private static async Task<T> Next<T>(BotClient bot, uint msgId, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string line)
    {
        using var http = new HttpClient();
        var resp = await http.PostAsync($"{_http}/ro3emu/gm?account=alice", Operator.Command(line));
        resp.EnsureSuccessStatusCode();
    }

    private GameSession Session => _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
    private Task OnLoop(Func<GameSession, Task> action) => _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() => action(Session));
    private List<InvItem> Stored(BotClient bot) => _app.Services.GetRequiredService<ItemStore>().Load((long)bot.Self!.Uuid);
    private DBEquipInfo StoredPlans(BotClient bot) => DBEquipInfo.Parser.ParseFrom(_app.Services.GetRequiredService<RoleStore>().LoadEquipPlans((long)bot.Self!.Uuid)!);

    private static async Task Equip(BotClient bot, long guid, int grid, bool remove = false)
    {
        await bot.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { Opr = (EquipItemOprType)(remove ? 1 : 0), Pos = (EquipGridType)grid, ItemGUID = guid });
        Assert.Equal(0, (await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
    }

    private static async Task Switch(BotClient bot, int index, int result = 0)
    {
        await bot.SendGameAsync(CharacterService.EquipSwitchPlan, new EquipSwitchPlanReq { TargetIndex = index });
        var reply = await Next(bot, CharacterService.EquipSwitchPlan, EquipSwitchPlanResp.Parser);
        Assert.Equal(result, reply.Result);
        if (result == 0) Assert.Equal(index, reply.CurrentPlan);
    }

    private static async Task Lock(BotClient bot, long guid, bool value, int result = 0)
    {
        await bot.SendGameAsync(EquipmentService.ItemLockDecompose, new ItemLockDecomposeReq { ItemGuid = guid, IsLocked = value });
        var reply = await Next(bot, EquipmentService.ItemLockDecompose, ItemLockDecomposeResp.Parser);
        Assert.Equal(result, reply.Result);
        Assert.Equal(guid, reply.ItemGuid);
        if (result == 0) Assert.Equal(value, reply.IsLocked);
    }

    private static async Task Decompose(BotClient bot, int result, params (long Guid, int Count)[] items)
    {
        var req = new ItemDecomposeReq();
        foreach (var (guid, count) in items) req.DecomposeItem[guid] = count;
        await bot.SendGameAsync(EquipmentService.ItemDecompose, req);
        Assert.Equal(result, (await Next(bot, EquipmentService.ItemDecompose, ItemDecomposeResp.Parser)).Result);
    }

    private static ItemAutoDecomposeSettingReq Filter(bool open = true, params int[] qualities) => new()
    {
        TabId = 2, IsOpen = open, OptionSetting = { [1] = new OptionGroup { Options = { qualities } } },
    };

    private static async Task Setting(BotClient bot, ItemAutoDecomposeSettingReq req, int result = 0)
    {
        await bot.SendGameAsync(EquipmentService.ItemAutoDecomposeSetting, req);
        Assert.Equal(result, (await Next(bot, EquipmentService.ItemAutoDecomposeSetting, ItemAutoDecomposeSettingResp.Parser)).Result);
    }

    private Task Loot(params (int Entry, int Count)[] items) => OnLoop(s => _app.Services.GetRequiredService<CharacterService>().GrantAsync(s, items));

    [ClientDataFact]
    public async Task PlansMoveSharedInstancesAndPersistNamesGridsAndCurrentIndex()
    {
        await using var alice = await InWorld("Alice");
        var bag = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        var initial = await Next(alice, CharacterService.EquipSync, DBEquipInfoIncrement.Parser);
        Assert.Equal([0, 1, 2], initial.EquipPlans.Keys.Order());
        long dagger = bag.PackInfo[2].All.Items.Values.Single(i => i.ItemEntry == Dagger).ItemGuid;
        long shirt = bag.PackInfo[2].All.Items.Values.Single(i => i.ItemEntry == Shirt).ItemGuid;
        await Equip(alice, dagger, 1);
        await Equip(alice, shirt, 3);
        await Switch(alice, 1);
        var emptied = await Next(alice, CharacterService.EquipSync, DBEquipInfoIncrement.Parser, m => m.HasCurrentEquipPlan && m.CurrentEquipPlan == 1);
        Assert.True(emptied.Items[dagger].Del);
        Assert.True(emptied.Items[shirt].Del);
        var returned = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.GetValueOrDefault(2)?.Inc?.Items.GetValueOrDefault(dagger)?.All != null);
        Assert.Equal(shirt, returned.PackInfo[2].Inc.Items[shirt].All.ItemGuid);

        // The same dagger may be referenced by both plans, but is still only one owned instance.
        await Equip(alice, dagger, 1);
        await Switch(alice, 0);
        var sharedIn = await Next(alice, CharacterService.EquipSync, DBEquipInfoIncrement.Parser, m => m.HasCurrentEquipPlan && m.CurrentEquipPlan == 0);
        Assert.False(sharedIn.Items.ContainsKey(dagger)); // an already-worn shared GUID needs no insertion/deletion
        var sharedPlans = StoredPlans(alice);
        Assert.Equal(sharedPlans.EquipPlans[0].ItemsEquipGrid[1], sharedPlans.EquipPlans[1].ItemsEquipGrid[1]);
        await Switch(alice, 1);
        var sharedOut = await Next(alice, CharacterService.EquipSync, DBEquipInfoIncrement.Parser, m => m.HasCurrentEquipPlan && m.CurrentEquipPlan == 1);
        Assert.False(sharedOut.Items.ContainsKey(dagger));
        Assert.Equal(1, Stored(alice).Single(i => i.Guid == dagger).Grid);
        await Gm($"!item {PurpleWeapon}");
        long weapon = Stored(alice).Single(i => i.Entry == PurpleWeapon).Guid;
        await Equip(alice, weapon, 1);
        await alice.SendGameAsync(CharacterService.EquipChangePlanName, new EquipChangePlanNameReq { TargetIndex = 1, Name = "Boss" });
        Assert.Equal(0, (await Next(alice, CharacterService.EquipChangePlanName, EquipChangePlanNameResp.Parser)).Result);
        var renamed = await Next(alice, CharacterService.EquipSync, DBEquipInfoIncrement.Parser, m => m.EquipPlans.GetValueOrDefault(1)?.Inc?.PlanName == "Boss");
        Assert.True(renamed.EquipPlans[1].Inc.HasPlanName);

        await Switch(alice, 0);
        var switched = await Next(alice, CharacterService.EquipSync, DBEquipInfoIncrement.Parser, m => m.HasCurrentEquipPlan && m.CurrentEquipPlan == 0);
        Assert.Equal(dagger, switched.Items[dagger].All.ItemGuid);
        Assert.True(switched.Items[weapon].Del);
        for (int i = 0; i < 3; i++) { await Switch(alice, 1); await Switch(alice, 0); }
        await Switch(alice, 0); // replay
        var persisted = StoredPlans(alice);
        Assert.Equal(dagger, persisted.EquipPlans[0].ItemsEquipGrid[1]);
        Assert.Equal(shirt, persisted.EquipPlans[0].ItemsEquipGrid[3]);
        Assert.Equal(weapon, persisted.EquipPlans[1].ItemsEquipGrid[1]);
        Assert.Equal("Boss", persisted.EquipPlans[1].PlanName);
        var stored = Stored(alice);
        Assert.Equal(1, stored.Single(i => i.Guid == dagger).Grid);
        Assert.Equal(3, stored.Single(i => i.Guid == shirt).Grid);
        Assert.Equal(0, stored.Single(i => i.Guid == weapon).Grid);
        Assert.Equal(stored.Count, stored.Select(i => i.Guid).Distinct().Count());

        await using var again = await InWorld("Alice", create: false);
        var loginBag = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        var loginEquip = await Next(again, CharacterService.EquipSync, DBEquipInfoIncrement.Parser);
        Assert.Equal(0, loginEquip.CurrentEquipPlan);
        Assert.Equal("Boss", loginEquip.EquipPlans[1].All.PlanName);
        var bagGuids = loginBag.PackInfo.Values.SelectMany(p => p.All.Items.Keys).ToHashSet();
        Assert.Empty(loginEquip.Items.Keys.Intersect(bagGuids));
        Assert.Equal(stored.Count, bagGuids.Count + loginEquip.Items.Count);
        await Switch(again, 1);
        var look = await Next(again, CharacterService.EntityBaseChange, EntityBase.Parser, m => m.NotifyType == EntityBase.Types.NotifyType.Equip);
        Assert.Equal(PurpleWeapon, look.EquipInfo[0]);
        await Next(again, CharacterService.OnePropertyChange, OnePropertyChange.Parser);
    }

    [ClientDataFact]
    public async Task BadPlansAndNamesLeaveTheSelectedPlanUnchanged()
    {
        await using var alice = await InWorld("Alice");
        long dagger = Stored(alice).Single(i => i.Entry == Dagger).Guid;
        await Equip(alice, dagger, 1);
        foreach (int index in new[] { -1, 3, int.MaxValue }) await Switch(alice, index, ErrorCodes.Parameter);
        foreach (var (index, name) in new[] { (-1, "Valid"), (3, "Valid"), (0, ""), (0, "   "), (0, "123456789"), (0, "a\nb") })
        {
            await alice.SendGameAsync(CharacterService.EquipChangePlanName, new EquipChangePlanNameReq { TargetIndex = index, Name = name });
            Assert.Equal(ErrorCodes.Parameter, (await Next(alice, CharacterService.EquipChangePlanName, EquipChangePlanNameResp.Parser)).Result);
        }
        Assert.Equal(0, StoredPlans(alice).CurrentEquipPlan);
        Assert.Equal(1, Stored(alice).Single(i => i.Guid == dagger).Grid);
        Assert.Equal("", StoredPlans(alice).EquipPlans[0].PlanName);
    }

    [ClientDataFact]
    public async Task InvalidTargetReferencesAndFullBagRefuseBeforeAnyMove()
    {
        await using var alice = await InWorld("Alice");
        long dagger = Stored(alice).Single(i => i.Entry == Dagger).Guid;
        await Equip(alice, dagger, 1);
        await OnLoop(s => { s.EquipmentPlans.EquipPlans[1].ItemsEquipGrid[1] = long.MaxValue; return Task.CompletedTask; });
        await Switch(alice, 1, ErrorCodes.ItemMissing);
        await OnLoop(s =>
        {
            var grid = s.EquipmentPlans.EquipPlans[1].ItemsEquipGrid;
            grid[1] = dagger;
            grid[2] = dagger;
            return Task.CompletedTask;
        });
        await Switch(alice, 1, ErrorCodes.EquipSlot);
        Assert.Equal(1, Stored(alice).Single(i => i.Guid == dagger).Grid);
        await OnLoop(s => { s.EquipmentPlans.EquipPlans[1].ItemsEquipGrid.Clear(); return Task.CompletedTask; });
        await Gm($"!item {Shirt} 99"); // shirt already in bag: 100 occupied gear cells
        await Switch(alice, 1, ErrorCodes.BagFull);
        Assert.Equal(0, StoredPlans(alice).CurrentEquipPlan);
        Assert.Equal(1, Stored(alice).Single(i => i.Guid == dagger).Grid);
    }

    [ClientDataFact]
    public async Task LocksSyncThroughBagEquipmentAndReconnectAndProtectWholeDismantleBatch()
    {
        await using var alice = await InWorld("Alice");
        await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        await Next(alice, CharacterService.EquipSync, DBEquipInfoIncrement.Parser);
        await Gm("!lvl 20");
        await Gm($"!item {PurpleWeapon} 2");
        var weapons = Stored(alice).Where(i => i.Entry == PurpleWeapon).ToArray();
        long locked = weapons[0].Guid, other = weapons[1].Guid;
        await Lock(alice, locked, true);
        await Lock(alice, locked, true); // a duplicate request must not toggle it off
        var protectedBag = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.GetValueOrDefault(2)?.Inc?.Items.GetValueOrDefault(locked)?.Inc?.IsLocked == true);
        Assert.True(protectedBag.PackInfo[2].Inc.Items[locked].Inc.HasIsLocked);
        await Decompose(alice, ErrorCodes.ItemLocked, (other, 1), (locked, 1));
        Assert.Equal(2, Stored(alice).Count(i => i.Entry == PurpleWeapon));
        await Equip(alice, locked, 1);
        var worn = await Next(alice, CharacterService.EquipSync, DBEquipInfoIncrement.Parser, m => m.Items.GetValueOrDefault(locked)?.All != null);
        Assert.True(worn.Items[locked].All.IsLocked);
        await Lock(alice, locked, false);
        var unlocked = await Next(alice, CharacterService.EquipSync, DBEquipInfoIncrement.Parser, m => m.Items.GetValueOrDefault(locked)?.Inc?.HasIsLocked == true);
        Assert.False(unlocked.Items[locked].Inc.IsLocked);
        await Lock(alice, locked, true);
        await Switch(alice, 1); // protected instance returns to the bag

        await using var again = await InWorld("Alice", create: false);
        var login = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        Assert.True(login.PackInfo[2].All.Items[locked].IsLocked);
        await Lock(again, locked, false);
        await Decompose(again, 0, (other, 1), (locked, 1));
        Assert.DoesNotContain(Stored(again), i => i.Entry == PurpleWeapon);
        Assert.Empty(StoredPlans(again).EquipPlans[0].ItemsEquipGrid); // removed GUIDs cannot be resurrected by a plan
        await Switch(again, 0);
    }

    [ClientDataFact]
    public async Task OwnershipCountsEquippedItemsAndFeatureGateAreEnforced()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        long foreign = Stored(bob).Single(i => i.Entry == Dagger).Guid;
        await Lock(alice, foreign, true, ErrorCodes.ItemMissing);
        await Lock(alice, long.MaxValue, true, ErrorCodes.ItemMissing);
        await Setting(alice, Filter(true, 4), ErrorCodes.FeatureUnavailable);
        long dagger = Stored(alice).Single(i => i.Entry == Dagger).Guid;
        await Decompose(alice, ErrorCodes.FeatureUnavailable, (dagger, 1));
        await Gm("!lvl 20");
        await Decompose(alice, ErrorCodes.ItemMissing, (foreign, 1));
        await Gm($"!item {Crystal} 2");
        long crystal = Stored(alice).Single(i => i.Entry == Crystal).Guid;
        await Decompose(alice, ErrorCodes.Parameter, (crystal, 0));
        await Decompose(alice, ErrorCodes.Parameter, (crystal, -1));
        await Decompose(alice, ErrorCodes.ItemsShort, (crystal, 3));
        await Equip(alice, dagger, 1);
        await Decompose(alice, ErrorCodes.CannotDismantleEquipped, (dagger, 1));
        Assert.Equal(2, Stored(alice).Single(i => i.Guid == crystal).Stack);
        Assert.False(Stored(bob).Single(i => i.Guid == foreign).IsLocked);
        await Decompose(alice, 0, (crystal, 1));
        Assert.Equal(5, Stored(alice).Single(i => i.Entry == Shard).Stack);
    }

    [ClientDataFact]
    public async Task AutoSettingsSyncPersistAndRejectInvalidTabsGroupsAndOptions()
    {
        await using var alice = await InWorld("Alice");
        await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        await Gm("!lvl 20");
        var valid = Filter(true, 2, 4);
        valid.OptionSetting[3] = new OptionGroup { Options = { 1, 2 } }; // equip part, not rank
        valid.OptionSetting[4] = new OptionGroup { Options = { 0 } };
        valid.OptionSetting[5] = new OptionGroup { Options = { 0 } };
        await Setting(alice, valid);
        var delta = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.AutoDecomposeSetting?.Inc != null);
        Assert.Equal(valid, delta.AutoDecomposeSetting.Inc.TabOptionSetting[2].All);
        var wrongTab = valid.Clone(); wrongTab.TabId = 999;
        await Setting(alice, wrongTab, ErrorCodes.DecomposeTabMissing);
        var noAutomatic = valid.Clone(); noAutomatic.TabId = 1;
        await Setting(alice, noAutomatic, ErrorCodes.Parameter);
        var wrongGroup = valid.Clone(); wrongGroup.OptionSetting[101] = new OptionGroup { Options = { 4 } };
        await Setting(alice, wrongGroup, ErrorCodes.Parameter);
        await Setting(alice, Filter(true, 5), ErrorCodes.Parameter); // orange excluded by automatic condition 1
        await Setting(alice, Filter(true, 4, 4), ErrorCodes.Parameter);
        var saved = AutoDecomposeSetting.Parser.ParseFrom(_app.Services.GetRequiredService<RoleStore>().LoadAutoDecompose((long)alice.Self!.Uuid)!);
        Assert.Equal(valid, saved.TabOptionSetting[2]);
        await using var again = await InWorld("Alice", create: false);
        var login = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        Assert.Equal(valid, login.AutoDecomposeSetting.All.TabOptionSetting[2]);
        valid.IsOpen = false;
        await Setting(again, valid);
        var disabled = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.AutoDecomposeSetting?.Inc != null);
        Assert.False(disabled.AutoDecomposeSetting.Inc.TabOptionSetting[2].Inc.IsOpen);
    }

    [ClientDataFact]
    public async Task MatchingLootIsConvertedOnceWithCompleteNoticeAndLockedExistingGearSurvives()
    {
        const int PurpleArmor = 2550240;
        await using var alice = await InWorld("Alice");
        await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        await Gm("!lvl 20");
        await Gm($"!item {PurpleWeapon}");
        long protectedGuid = Stored(alice).Single(i => i.Entry == PurpleWeapon).Guid;
        await Lock(alice, protectedGuid, true);
        var filter = Filter(true, 4);
        filter.OptionSetting[3] = new OptionGroup { Options = { 1, 2, 4 } };
        filter.OptionSetting[4] = new OptionGroup { Options = { 1 } }; // quality 4 now rolls its configured one affix
        filter.OptionSetting[5] = new OptionGroup { Options = { 0 } };
        await Setting(alice, filter);
        var before = _app.Services.GetRequiredService<RoleStore>().LoadProgress((long)alice.Self!.Uuid).Currency.GetValueOrDefault(2);
        await Loot((PurpleWeapon, 2), (PurpleWeapon, 1), (PurpleArmor, 2), (Shirt, 1));
        var notice = await Next(alice, EquipmentService.ItemAutoDecomposeNotice, ItemAutoDecomposeTipsNtf.Parser);
        Assert.Equal(2, notice.DecomposeItemInfo.Count);
        Assert.Equal(3, notice.DecomposeItemInfo[PurpleWeapon]);
        Assert.Equal(2, notice.DecomposeItemInfo[PurpleArmor]);
        Assert.Equal(750, notice.DecomposeGainItemInfo[PurpleWeapon].ItemInfo[2]); // 250 per item
        Assert.Equal(500, notice.DecomposeGainItemInfo[PurpleArmor].ItemInfo[2]);
        int gain = notice.DecomposeGainItemInfo.Values.Sum(g => g.ItemInfo[2]);
        var bag = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.Currency.GetValueOrDefault(2)?.All == before + gain);
        Assert.False(bag.SyncAllMark);
        var stored = Stored(alice);
        Assert.Equal(protectedGuid, Assert.Single(stored, i => i.Entry == PurpleWeapon).Guid);
        Assert.True(stored.Single(i => i.Guid == protectedGuid).IsLocked);
        Assert.DoesNotContain(stored, i => i.Entry == PurpleArmor);
        Assert.Equal(2, stored.Count(i => i.Entry == Shirt)); // other quality/part kept
        Assert.Equal(before + gain, _app.Services.GetRequiredService<RoleStore>().LoadProgress((long)alice.Self!.Uuid).Currency[2]);
    }

    [ClientDataFact]
    public async Task EmptyDisabledAndNonmatchingFiltersKeepLootAndGmNeverTriggersAutoDismantle()
    {
        await using var alice = await InWorld("Alice");
        await Gm("!lvl 20");
        await Setting(alice, Filter());
        await Loot((PurpleWeapon, 1));
        await Setting(alice, Filter(false, 4));
        await Loot((PurpleWeapon, 1));
        var wrongPart = Filter(true, 4);
        wrongPart.OptionSetting[3] = new OptionGroup { Options = { 3 } };
        await Setting(alice, wrongPart);
        await Loot((PurpleWeapon, 1));
        await Setting(alice, Filter(true, 4));
        await Gm($"!item {PurpleWeapon}");
        Assert.Equal(4, Stored(alice).Count(i => i.Entry == PurpleWeapon));
        await Loot((PurpleWeapon, 1));
        var notice = await Next(alice, EquipmentService.ItemAutoDecomposeNotice, ItemAutoDecomposeTipsNtf.Parser);
        Assert.Equal(1, notice.DecomposeItemInfo[PurpleWeapon]);
        Assert.Equal(4, Stored(alice).Count(i => i.Entry == PurpleWeapon));
    }

    [ClientDataFact]
    public async Task LockedCardSplitAndSocketRemovalPreserveProtection()
    {
        const int Lunatic = 1420100;
        await using var alice = await InWorld("Alice");
        await Gm("!lvl 20");
        await Gm($"!item {Lunatic} 2");
        long card = Stored(alice).Single(i => i.Entry == Lunatic).Guid;
        await Lock(alice, card, true);
        await Equip(alice, Stored(alice).Single(i => i.Entry == Dagger).Guid, 1);
        await alice.SendGameAsync(CardService.SlotInsert, new EquipmentCardInsertReq { EquipPos = (EquipGridType)1, SlotIndex = 1, CardGuid = card });
        var inserted = await Next(alice, CardService.SlotInsert, EquipmentCardInsertResp.Parser);
        Assert.Equal(0, inserted.Result);
        Assert.True(inserted.CardInfo.IsLocked);
        await Gm($"!item {Lunatic}"); // a new unlocked stack must not merge with either protected instance
        Assert.Equal(3, Stored(alice).Count(i => i.Entry == Lunatic));
        await Lock(alice, inserted.CardGuid, false); // socket ownership is also a valid lock target
        await alice.SendGameAsync(CardService.SlotRemove, new EquipmentCardRemoveReq { EquipPos = (EquipGridType)1, SlotIndex = 1 });
        Assert.Equal(0, (await Next(alice, CardService.SlotRemove, EquipmentCardRemoveResp.Parser)).Result);
        var cards = Stored(alice).Where(i => i.Entry == Lunatic).ToArray();
        Assert.Equal(1, cards.Single(i => i.IsLocked).Stack);
        Assert.Equal(2, cards.Single(i => !i.IsLocked).Stack);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

}
