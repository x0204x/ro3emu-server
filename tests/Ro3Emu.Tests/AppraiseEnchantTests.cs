using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>31401/31415 wire, persistence and stat contracts.</summary>
public sealed class AppraiseEnchantTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-appraise-enchant-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication _app = null!;
    private string _http = "";
    private const int Orange = 2010250, Gear = 2030540, Scroll = 1011201;

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

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string account, string line)
    {
        using var http = new HttpClient();
        var resp = await http.PostAsync($"{_http}/ro3emu/gm?account={account}", Operator.Command(line));
        resp.EnsureSuccessStatusCode();
    }

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private GameSession Session(string account = "alice") => Service<SessionRegistry>().All().Single(s => s.Account == account);
    private InvItem Owned(int entry, string account = "alice") => Session(account).Items.Single(i => i.Entry == entry);
    private InvItem Live(long guid, string account = "alice") => Session(account).Items.Single(i => i.Guid == guid);

    private async Task OpenEnchant()
    {
        await Gm("alice", "!quest done 10103020");
        await Gm("alice", "!lvl 50");
    }

    private async Task<InvItem> Give(int entry, int count = 1, string account = "alice")
    {
        await Gm(account, $"!item {entry} {count}");
        return Owned(entry, account);
    }

    private async Task<EnchantResp> Apply(BotClient bot, long gear, long scroll)
    {
        await bot.SendGameAsync(EnchantService.Enchant, new EnchantReq { EquipGuid = gear, ItemGuid = scroll });
        return await Next(bot, EnchantService.Enchant, EnchantResp.Parser);
    }

    private async Task<EquipItemOprResp> Wear(BotClient bot, long guid, bool remove = false)
    {
        await bot.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { ItemGUID = guid, Pos = (EquipGridType)1, Opr = (EquipItemOprType)(remove ? 1 : 0) });
        return await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser);
    }

    [ClientDataFact]
    public async Task AppraisalPreservesGuidAndRollsAndSurvivesRelog()
    {
        long guid;
        Dictionary<int, long> attrs;
        await using (var alice = await InWorld("Alice"))
        {
            await Gm("alice", "!quest done 10103020");
            var gear = await Give(Orange);
            guid = gear.Guid;
            attrs = new(gear.Attrs!);
            Assert.Equal(0, attrs[3]);
            Assert.Equal(ErrorCodes.EquipNotAppraised, (await Wear(alice, guid)).Result);
            Assert.Equal(0, gear.Grid);

            await alice.SendGameAsync(AppraisalService.Appraise, new EquipAppraiseReq { ItemGuids = { guid } });
            var sync = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.PackInfo.GetValueOrDefault(2)?.Inc?.Items.GetValueOrDefault(guid)?.Inc?.ItemInfos.GetValueOrDefault(3)?.Inc?.LongVal == 1);
            Assert.Equal(1, sync.PackInfo[2].Inc.Items[guid].Inc.ItemInfos[3].Inc.LongVal);
            var reply = await Next(alice, AppraisalService.Appraise, EquipAppraiseResp.Parser);
            Assert.Equal(0, reply.Result);
            var item = Assert.Single(reply.AppraiseResult);
            Assert.Equal(guid, item.ItemGuid);
            Assert.Equal(1, item.ItemInfos[3].LongVal);
            Assert.All(attrs.Where(p => p.Key != 3), p => Assert.Equal(p.Value, item.ItemInfos[p.Key].LongVal));
            Assert.Equal(0, (await Wear(alice, guid)).Result);
            Assert.Equal(1, Service<ItemStore>().Load((long)alice.Self!.Uuid).Single(i => i.Guid == guid).Attrs![3]);
        }
        await using var again = await InWorld("Alice", create: false);
        var equipped = await Next(again, CharacterService.EquipSync, DBEquipInfoIncrement.Parser);
        Assert.Equal(1, equipped.Items[guid].All.ItemInfos[3].LongVal);
        Assert.All(attrs.Where(p => p.Key != 3), p => Assert.Equal(p.Value, equipped.Items[guid].All.ItemInfos[p.Key].LongVal));
    }

    [ClientDataFact]
    public async Task AppraisalRejectsWholeInvalidBatchWithoutChanges()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var gear = await Give(Orange);
        var foreign = await Give(Orange, account: "bob");
        await alice.SendGameAsync(AppraisalService.Appraise, new EquipAppraiseReq { ItemGuids = { gear.Guid } });
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(alice, AppraisalService.Appraise, EquipAppraiseResp.Parser)).Result);
        await Gm("alice", "!quest done 10103020");
        var dagger = Owned(2010121);
        var potion = Owned(1000000);
        foreach (var (guids, expected) in new (long[], int)[]
        {
            ([], ErrorCodes.Parameter),
            ([gear.Guid, gear.Guid], ErrorCodes.Parameter),
            ([gear.Guid, long.MaxValue], ErrorCodes.ItemMissing),
            ([gear.Guid, foreign.Guid], ErrorCodes.ItemMissing),
            ([gear.Guid, potion.Guid], ErrorCodes.NotEquipment),
            ([gear.Guid, dagger.Guid], ErrorCodes.AppraiseParameter),
        })
        {
            string before = Snapshot();
            await alice.SendGameAsync(AppraisalService.Appraise, new EquipAppraiseReq { ItemGuids = { guids } });
            var reply = await Next(alice, AppraisalService.Appraise, EquipAppraiseResp.Parser);
            Assert.Equal(expected, reply.Result);
            Assert.Empty(reply.AppraiseResult);
            Assert.Equal(before, Snapshot());
        }
        Assert.Equal(0, Live(gear.Guid).Attrs![3]);
        Assert.Equal(0, Live(foreign.Guid, "bob").Attrs![3]);
        Assert.Equal(0, (await Wear(alice, dagger.Guid)).Result);
        await alice.SendGameAsync(AppraisalService.Appraise, new EquipAppraiseReq { ItemGuids = { gear.Guid, dagger.Guid } });
        Assert.Equal(ErrorCodes.EquipNotInBag, (await Next(alice, AppraisalService.Appraise, EquipAppraiseResp.Parser)).Result);
        Assert.Equal(0, Live(gear.Guid).Attrs![3]);
    }

    [ClientDataFact]
    public async Task LootCreatesSeparateUnappraisedInstancesAndBatchAppraisalUpdatesBoth()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!quest done 10103020");
        await Service<GameLoop>().InvokeAsync(() => Service<CharacterService>().GrantAsync(Session(), [(Orange, 2)])); // RewardService's actual loot grant path
        var gear = Session().Items.Where(i => i.Entry == Orange).ToArray();
        Assert.Equal(2, gear.Length);
        Assert.NotEqual(gear[0].Guid, gear[1].Guid);
        Assert.All(gear, i => Assert.Equal(0, i.Attrs![3]));
        await alice.SendGameAsync(AppraisalService.Appraise, new EquipAppraiseReq { ItemGuids = { gear.Select(i => i.Guid) } });
        var reply = await Next(alice, AppraisalService.Appraise, EquipAppraiseResp.Parser);
        Assert.Equal(0, reply.Result);
        Assert.Equal(2, reply.AppraiseResult.Count);
        Assert.All(reply.AppraiseResult, i => Assert.Equal(1, i.ItemInfos[3].LongVal));
        string before = Snapshot();
        await alice.SendGameAsync(AppraisalService.Appraise, new EquipAppraiseReq { ItemGuids = { gear[0].Guid } });
        Assert.Equal(ErrorCodes.AppraiseParameter, (await Next(alice, AppraisalService.Appraise, EquipAppraiseResp.Parser)).Result);
        Assert.Equal(before, Snapshot());
    }

    [ClientDataFact]
    public async Task CraftCreatesUnappraisedGearWhenItsConfigRequiresIt()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!quest done 10103020");
        await Gm("alice", "!item 1254007 1");
        await Gm("alice", "!item 1200704 5");
        // A private quality table makes the existing rank-3 orange output certain. Shared data/ is never changed.
        string privateData = Path.Combine(_dataDir, "craft-fixture");
        Directory.CreateDirectory(Path.Combine(privateData, "config"));
        var recipe = Service<GameData>().Row("EquipCraftConfig", 3001)!.Value;
        await File.WriteAllTextAsync(Path.Combine(privateData, "config", "EquipCraftConfig.json"), "{\"3001\":" + recipe.GetRawText() + "}");
        await File.WriteAllTextAsync(Path.Combine(privateData, "config", "EquipCraftQualityConfig.json"),
            "{\"35\":{\"_iID\":35,\"_iRank\":3,\"_iQuality\":5,\"_iWeight\":1}}");
        var fixtureData = new GameData(Options.Create(new ServerOptions { DataDir = privateData }), NullLogger<GameData>.Instance);
        var crafting = new EquipmentService(fixtureData, Service<CharacterService>(), Service<FunctionService>(), Service<ItemStore>(), Service<RoleStore>(), NullLogger<EquipmentService>.Instance);
        await Service<GameLoop>().InvokeAsync(() => crafting.CraftAsync(Session(), new EquipCraftReq { Rank = 3, Subtype = 1 }));
        var crafted = await Next(alice, EquipmentService.Craft, EquipCraftResp.Parser);
        Assert.Equal(0, crafted.Result);
        Assert.Equal(2010350, crafted.Item.ItemEntry);
        Assert.Equal(0, crafted.Item.ItemInfos[3].LongVal);
        Assert.Equal(0, Owned(2010350).Attrs![3]);
        Assert.Equal(ErrorCodes.EquipNotAppraised, (await Wear(alice, crafted.Item.ItemGuid)).Result);
    }

    [ClientDataFact]
    public async Task EnchantBagGearThenWearAndRelogPreservesAttributesAndStats()
    {
        long guid;
        int str;
        long equippedStr;
        long timestamp;
        Dictionary<int, long> attrs;
        await using (var alice = await InWorld("Alice"))
        {
            await OpenEnchant();
            var gear = await Give(Gear);
            guid = gear.Guid;
            attrs = new(gear.Attrs!);
            var scroll = await Give(Scroll, 2);
            await Gm("alice", "!item 2 20000");
            int mark = alice.Inbox.Snapshot().Count;
            var reply = await Apply(alice, guid, scroll.Guid);
            Assert.Equal((0, Scroll, 1), (reply.Result, reply.EnchantId, reply.Type));
            var update = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.PackInfo.GetValueOrDefault(2)?.Inc?.Items.GetValueOrDefault(guid)?.Inc?.Enchantment.ContainsKey(Scroll) == true);
            var enchant = update.PackInfo[2].Inc.Items[guid].Inc.Enchantment[Scroll].All;
            str = enchant.Attr[1];
            timestamp = enchant.GetTime;
            Assert.InRange(str, 4, 6);
            Assert.True(timestamp > 0);
            Assert.Equal(1, update.PackInfo[1].Inc.Items[scroll.Guid].Inc.Stack);
            Assert.Equal(10000, update.Currency[2].All);
            var currentGear = Live(guid);
            Assert.Equal(attrs.OrderBy(p => p.Key), currentGear.Attrs!.OrderBy(p => p.Key));
            var events = alice.Inbox.Snapshot().Skip(mark).ToList();
            Assert.True(events.FindIndex(e => e.IsGame(5009)) < events.FindIndex(e => e.IsGame(31415)));
            Assert.DoesNotContain(events, e => e.IsGame(5007) || e.IsGame(31008));
            var saved = Service<ItemStore>().Load((long)alice.Self!.Uuid).Single(i => i.Guid == guid);
            Assert.Equal(str, saved.Enchantments[Scroll].Attr[1]);

            // A generated STR affix is independent of the enchantment and contributes while the gear is worn.
            equippedStr = 1 + str + (long)EquipmentAffixes.Entries(currentGear).Where(p => p.Id == 1).Sum(p => p.Value);
            Assert.Equal(0, (await Wear(alice, guid)).Result);
            var stats = await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
                m => m.Property.Propertys.Any(p => p.PropertyId == 1 && p.Data == equippedStr));
            Assert.Equal(equippedStr, stats.Property.Propertys.Single(p => p.PropertyId == 1).Data);
        }
        await using var again = await InWorld("Alice", create: false);
        Assert.Equal(equippedStr, again.Self!.Property.Single(p => p.Idx == 1).Data);
        var equip = await Next(again, CharacterService.EquipSync, DBEquipInfoIncrement.Parser);
        Assert.Equal(str, equip.Items[guid].All.Enchantment[Scroll].Attr[1]);
        Assert.Equal(timestamp, equip.Items[guid].All.Enchantment[Scroll].GetTime);
        Assert.All(attrs, p => Assert.Equal(p.Value, equip.Items[guid].All.ItemInfos[p.Key].LongVal));
        var bag = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        Assert.DoesNotContain(guid, bag.PackInfo[2].All.Items.Keys);
        Assert.Equal(10000, bag.Currency[2].All);
        Assert.Equal(0, (await Wear(again, guid, remove: true)).Result);
        await Next(again, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            m => m.Property.Propertys.Any(p => p.PropertyId == 1 && p.Data == 1));
    }

    [ClientDataFact]
    public async Task WornEnchantAndSameGroupReplacementSyncEquipmentAndDerivedStats()
    {
        await using var alice = await InWorld("Alice");
        await OpenEnchant();
        var gear = await Give(Gear);
        Assert.Equal(0, (await Wear(alice, gear.Guid)).Result);
        var before = Service<CharacterService>().Stats(Session());
        var scroll = await Give(Scroll);
        await Gm("alice", "!item 2 30000");
        Assert.Equal(0, (await Apply(alice, gear.Guid, scroll.Guid)).Result);
        var equip = await Next(alice, CharacterService.EquipSync, DBEquipInfoIncrement.Parser,
            m => m.Items.GetValueOrDefault(gear.Guid)?.Inc?.Enchantment.ContainsKey(Scroll) == true);
        var enchant = equip.Items[gear.Guid].Inc.Enchantment[Scroll].All;
        int str = enchant.Attr[1];
        var stats = await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            m => m.Property.Propertys.Any(p => p.PropertyId == 1 && p.Data == before[1] + str));
        Assert.True(stats.Property.Propertys.Single(p => p.PropertyId == 103).Data > before[103]);

        var replacement = await Give(1011301);
        Assert.Equal(0, (await Apply(alice, gear.Guid, replacement.Guid)).Result);
        var changed = await Next(alice, CharacterService.EquipSync, DBEquipInfoIncrement.Parser,
            m => m.Items.GetValueOrDefault(gear.Guid)?.Inc?.Enchantment.ContainsKey(1011301) == true);
        Assert.True(changed.Items[gear.Guid].Inc.Enchantment[Scroll].Del);
        var next = changed.Items[gear.Guid].Inc.Enchantment[1011301].All;
        Assert.True(next.GetTime > enchant.GetTime);
        Assert.Single(Live(gear.Guid).Enchantments);
        Assert.Single(Service<ItemStore>().Load(Session().Role!.Id).Single(i => i.Guid == gear.Guid).Enchantments);
        await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            m => m.Property.Propertys.Any(p => p.PropertyId == 1 && p.Data == before[1] + next.Attr[1]));
        Assert.DoesNotContain(gear.Guid, ((DBItemsInfo)Session().Synced[CharacterService.ItemsSync]).PackInfo[2].Items.Keys);
        string snapshot = Snapshot();
        Assert.Equal(ErrorCodes.ItemMissing, (await Apply(alice, gear.Guid, replacement.Guid)).Result);
        Assert.Equal(snapshot, Snapshot());
    }

    [ClientDataFact]
    public async Task FullSlotsEvictOneAndSameIdRerollKeepsCapacity()
    {
        await using var alice = await InWorld("Alice");
        await OpenEnchant();
        var gear = await Give(Gear);
        await Gm("alice", "!item 2 70000");
        foreach (int entry in new[] { 1011201, 1011202, 1011203, 1011204 })
        {
            var scroll = await Give(entry);
            Assert.Equal(0, (await Apply(alice, gear.Guid, scroll.Guid)).Result);
        }
        var old = Live(gear.Guid).Enchantments.ToDictionary(p => p.Key, p => JsonSerializer.Serialize(p.Value));
        var fifth = await Give(1011205, 2);
        Assert.Equal(0, (await Apply(alice, gear.Guid, fifth.Guid)).Result);
        var currentGear = Live(gear.Guid);
        Assert.Equal(4, currentGear.Enchantments.Count);
        Assert.Single(old.Keys.Except(currentGear.Enchantments.Keys));
        Assert.All(old.Keys.Intersect(currentGear.Enchantments.Keys), id => Assert.Equal(old[id], JsonSerializer.Serialize(currentGear.Enchantments[id])));
        long time = currentGear.Enchantments[1011205].GetTime;
        Assert.Equal(0, (await Apply(alice, gear.Guid, fifth.Guid)).Result);
        var rerolledGear = Live(gear.Guid);
        Assert.Equal(4, rerolledGear.Enchantments.Count);
        Assert.True(rerolledGear.Enchantments[1011205].GetTime > time);
        var reroll = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.GetValueOrDefault(2)?.Inc?.Items.GetValueOrDefault(gear.Guid)?.Inc?.Enchantment.GetValueOrDefault(1011205)?.Inc != null);
        Assert.True(reroll.PackInfo[2].Inc.Items[gear.Guid].Inc.Enchantment[1011205].Inc.GetTime > time);
        Assert.Equal(10000, Session().Progress!.Currency[2]);
    }

    [ClientDataTheory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public async Task FailedEnchantWriteRollsBackScrollCurrencyAndGear(int stack, bool failCurrency)
    {
        await using var alice = await InWorld("Alice");
        await OpenEnchant();
        var gear = await Give(Gear);
        var scroll = await Give(Scroll, stack);
        await Gm("alice", "!item 2 10000");
        string before = Snapshot();
        long version = Session().SyncVersions[CharacterService.ItemsSync];
        string target = failCurrency ? $"role WHEN NEW.id = {Session().Role!.Id}" : $"item WHEN NEW.guid = {gear.Guid}";
        ExecuteSql($"CREATE TRIGGER fail_enchant BEFORE UPDATE ON {target} BEGIN SELECT RAISE(ABORT, 'injected enchant write failure'); END;");
        Assert.Equal(ErrorCodes.Unknown, (await Apply(alice, gear.Guid, scroll.Guid)).Result);
        Assert.Equal(before, Snapshot());
        Assert.Equal(version, Session().SyncVersions[CharacterService.ItemsSync]);
        ExecuteSql("DROP TRIGGER fail_enchant;");
        Assert.Equal(0, (await Apply(alice, gear.Guid, scroll.Guid)).Result);
        // A completed task award may have replaced the inventory snapshot since Give returned.
        // Verify the successful retry against its live and durable item, identified by the stable GUID.
        Assert.Single(Session().Items.Single(i => i.Guid == gear.Guid).Enchantments);
        Assert.Single(Service<ItemStore>().Load(Session().Role!.Id).Single(i => i.Guid == gear.Guid).Enchantments);
    }

    [ClientDataFact]
    public async Task FailedSecondAppraisalWriteRollsBackWholeBatch()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!quest done 10103020");
        await Service<GameLoop>().InvokeAsync(() => Service<CharacterService>().GrantAsync(Session(), [(Orange, 2)]));
        var gear = Session().Items.Where(i => i.Entry == Orange).ToArray();
        var request = new EquipAppraiseReq { ItemGuids = { gear.Select(i => i.Guid) } };
        string before = Snapshot();
        ExecuteSql($"CREATE TRIGGER fail_appraise BEFORE UPDATE ON item WHEN NEW.guid = {gear[1].Guid} BEGIN SELECT RAISE(ABORT, 'injected second appraisal failure'); END;");
        await alice.SendGameAsync(AppraisalService.Appraise, request);
        var refused = await Next(alice, AppraisalService.Appraise, EquipAppraiseResp.Parser);
        Assert.Equal(ErrorCodes.Unknown, refused.Result);
        Assert.Empty(refused.AppraiseResult);
        Assert.Equal(before, Snapshot());
        ExecuteSql("DROP TRIGGER fail_appraise;");
        await alice.SendGameAsync(AppraisalService.Appraise, request);
        Assert.Equal(0, (await Next(alice, AppraisalService.Appraise, EquipAppraiseResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task EnchantRefusalsPreserveBothPlayersItemsCurrencyAndStats()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var gear = await Give(Gear);
        var scroll = await Give(Scroll, 2);
        var foreignGear = await Give(Gear, account: "bob");
        var foreignScroll = await Give(Scroll, account: "bob");
        var noSlots = await Give(2010240);
        var wrongType = await Give(2010540);
        var overLevel = await Give(2030640);
        var unappraised = await Give(2030550);
        var skillOnly = await Give(1011607);
        await Gm("alice", "!item 2 10000");
        await Refuse(gear.Guid, scroll.Guid, ErrorCodes.FeatureUnavailable);
        await Gm("alice", "!lvl 50"); // enchant's own condition holds, its parent is still closed
        await Refuse(gear.Guid, scroll.Guid, ErrorCodes.FeatureUnavailable);
        await Gm("alice", "!quest done 10103020");
        foreach (var (target, material, error) in new[]
        {
            (long.MaxValue, scroll.Guid, ErrorCodes.ItemMissing),
            (foreignGear.Guid, scroll.Guid, ErrorCodes.ItemMissing),
            (gear.Guid, foreignScroll.Guid, ErrorCodes.ItemMissing),
            (gear.Guid, long.MaxValue, ErrorCodes.ItemMissing),
            (Owned(1000000).Guid, scroll.Guid, ErrorCodes.NotEquipment),
            (gear.Guid, Owned(1000000).Guid, ErrorCodes.EnchantParameter),
            (noSlots.Guid, scroll.Guid, ErrorCodes.EnchantParameter),
            (wrongType.Guid, scroll.Guid, ErrorCodes.EnchantParameter),
            (overLevel.Guid, scroll.Guid, ErrorCodes.EnchantParameter),
            (unappraised.Guid, scroll.Guid, ErrorCodes.EquipNotAppraised),
            (gear.Guid, skillOnly.Guid, ErrorCodes.FeatureUnavailable),
        }) await Refuse(target, material, error);
        await Gm("alice", "!delitem 2 1");
        await Refuse(gear.Guid, scroll.Guid, ErrorCodes.EnchantMaterials);
        Assert.Equal(2, Live(scroll.Guid).Stack);
        Assert.Empty(Live(gear.Guid).Enchantments);

        async Task Refuse(long target, long material, int error)
        {
            string before = Snapshot(), other = Snapshot("bob");
            var reply = await Apply(alice, target, material);
            Assert.Equal(error, reply.Result);
            Assert.Equal(0, reply.EnchantId);
            Assert.Equal(before, Snapshot());
            Assert.Equal(other, Snapshot("bob"));
        }
    }

    [ClientDataFact]
    public async Task LegacyItemsKeepTheirPreviouslyAdvertisedAppraisalState()
    {
        long guid;
        await using (var alice = await InWorld("Alice"))
        {
            var gear = await Give(Orange);
            guid = gear.Guid;
            await Service<GameLoop>().InvokeAsync(async () =>
            {
                using var held = await CharacterService.Lock(Session());
                var current = Live(guid);
                current.Attrs!.Remove(3); // pre-appraisal implementation persisted rolls, advertised flag 1 only on the wire
                current.Grid = 1;
                Service<ItemStore>().Update(current);
            });
        }
        await using var again = await InWorld("Alice", create: false);
        var equip = await Next(again, CharacterService.EquipSync, DBEquipInfoIncrement.Parser);
        Assert.Equal(1, equip.Items[guid].All.ItemInfos[3].LongVal);
        Assert.Equal(1, Service<ItemStore>().Load((long)again.Self!.Uuid).Single(i => i.Guid == guid).Attrs![3]);
    }

    [ClientDataFact]
    public async Task LegacyWornStackSplitsIntoIndependentlyEnchantableInstances()
    {
        const int LegacyGear = 2030550;
        long original;
        Dictionary<int, long> oldAttrs = null!;
        await using (var alice = await InWorld("Alice"))
        {
            await OpenEnchant();
            var gear = await Give(LegacyGear);
            original = gear.Guid;
            oldAttrs = await Service<GameLoop>().InvokeAsync(async () =>
            {
                using var held = await CharacterService.Lock(Session());
                var current = Live(original);
                current.Attrs!.Remove(3);
                var attributes = new Dictionary<int, long>(current.Attrs);
                current.Stack = 2;
                current.Grid = 1;
                Service<ItemStore>().Update(current);
                return attributes;
            });
        }
        await using (var again = await InWorld("Alice", create: false))
        {
            var pieces = Session().Items.Where(i => i.Entry == LegacyGear).ToArray();
            Assert.Equal(2, pieces.Length);
            Assert.All(pieces, i =>
            {
                Assert.Equal(1, i.Stack);
                Assert.Equal(1, i.Attrs![3]);
                Assert.All(oldAttrs, p => Assert.Equal(p.Value, i.Attrs[p.Key]));
            });
            Assert.Equal(1, pieces.Single(i => i.Guid == original).Grid);
            var extra = pieces.Single(i => i.Guid != original);
            Assert.Equal(0, extra.Grid);
            var bag = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
            Assert.Contains(extra.Guid, bag.PackInfo[2].All.Items.Keys);
            Assert.DoesNotContain(original, bag.PackInfo[2].All.Items.Keys);
            var scroll = await Give(Scroll, 2);
            await Gm("alice", "!item 2 20000");
            Assert.Equal(0, (await Apply(again, original, scroll.Guid)).Result);
            Assert.Empty(Live(extra.Guid).Enchantments);
            Assert.Equal(0, (await Apply(again, extra.Guid, scroll.Guid)).Result);
        }
        await using var third = await InWorld("Alice", create: false);
        var persisted = Session().Items.Where(i => i.Entry == LegacyGear).ToArray();
        Assert.Equal(2, persisted.Length); // migration is idempotent
        Assert.All(persisted, i => Assert.Single(i.Enchantments));
    }

    [Fact]
    public void ItemStoreAddsEnchantColumnToOldDatabaseAndRoundTripsInstances()
    {
        var options = Options.Create(new ServerOptions { DataDir = Path.Combine(_dataDir, "legacy") });
        long roleId;
        using (var legacy = new AccountStore(options))
        {
            roleId = new RoleStore(legacy).Create("legacy-enchant", 1, "LegacyEnchant", 1, 1000, 1)!.Id;
            FixtureSql.Execute(legacy, """
                    DROP TABLE item;
                    CREATE TABLE item (guid INTEGER PRIMARY KEY AUTOINCREMENT, role_id INTEGER NOT NULL,
                        entry INTEGER NOT NULL, stack INTEGER NOT NULL, pack INTEGER NOT NULL, sort INTEGER NOT NULL,
                        grid INTEGER NOT NULL DEFAULT 0, attrs TEXT);
                    INSERT INTO item (role_id, entry, stack, pack, sort, attrs) VALUES ($role, 2030540, 1, 2, 1, '{"3":1,"30":103,"60":100}');
                    """, ("$role", roleId));
        }
        new AccountStore(options).Dispose(); // opening the old database migrates it; a second open must be harmless
        using var accounts = new AccountStore(options);
        var store = new ItemStore(accounts);
        var old = Assert.Single(store.Load(roleId));
        Assert.Empty(old.Enchantments);
        old.Enchantments[Scroll] = new ItemEnchantment { Attr = { [1] = 5 }, GetTime = 1780000000 };
        store.Update(old);
        var second = new InvItem { Entry = Gear, Stack = 1, Pack = 2, Sort = 2, Attrs = new() { [3] = 1 } };
        second.Enchantments[1011202] = new ItemEnchantment { Attr = { [2] = 6 }, GetTime = 1780000001 };
        store.Insert(roleId, second);
        var reloaded = new ItemStore(accounts).Load(roleId);
        Assert.Equal(2, reloaded.Count);
        Assert.Equal(100, reloaded[0].Attrs![60]);
        Assert.Equal(5, reloaded[0].Enchantments[Scroll].Attr[1]);
        Assert.Equal(1780000000, reloaded[0].Enchantments[Scroll].GetTime);
        Assert.Equal(6, reloaded[1].Enchantments[1011202].Attr[2]);
        Assert.NotEqual(reloaded[0].Guid, reloaded[1].Guid);
    }

    private string Snapshot(string account = "alice")
    {
        var session = Session(account);
        return JsonSerializer.Serialize(new
        {
            session.Items,
            session.Progress!.Currency,
            StoredItems = Service<ItemStore>().Load(session.Role!.Id),
            StoredCurrency = Service<RoleStore>().LoadProgress(session.Role.Id).Currency,
            Stats = Service<CharacterService>().Stats(session).Data,
        });
    }

    private void ExecuteSql(string sql)
    {
        FixtureSql.Execute(Service<AccountStore>(), sql);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

}
