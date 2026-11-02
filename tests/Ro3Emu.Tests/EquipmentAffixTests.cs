using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>Equipment instance generation, appraisal, stats and lifecycle over the normal encrypted game connection.</summary>
public sealed class EquipmentAffixTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-affixes-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";
    private const int Cutter = 2010250, Purple = 2010240, Red = 2010460, FixedArmor = 2550561;

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = http;
            o.MicroGatePort = micro;
            o.GameGatePort = game;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{http}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private GameData Data => Service<GameData>();
    private GameSession Session => Service<SessionRegistry>().All().Single(s => s.Account == "alice");
    private Task OnLoop(Func<GameSession, Task> action) => Service<GameLoop>().InvokeAsync(() => action(Session));
    private InvItem Stored(long guid) => Service<ItemStore>().Load(Session.Role!.Id).Single(i => i.Guid == guid);

    private async Task<BotClient> InWorld(bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("alice", "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint msg, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(msg) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(packet.Body);
    }

    private async Task Gm(string command)
    {
        using var http = new HttpClient();
        var reply = await http.PostAsync($"{_http}/ro3emu/gm?account=alice", Operator.Command(command));
        reply.EnsureSuccessStatusCode();
    }

    private async Task<ItemBaseInfo[]> Granted(BotClient bot, int entry, Func<Task> grant)
    {
        var before = Session.Items.Select(i => i.Guid).ToHashSet();
        await grant();
        var bag = await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, b =>
            b.PackInfo.GetValueOrDefault(2)?.Inc?.Items.Values.Any(i => i.All?.ItemEntry == entry && !before.Contains(i.All.ItemGuid)) == true);
        return [.. bag.PackInfo[2].Inc.Items.Values.Where(i => i.All?.ItemEntry == entry && !before.Contains(i.All.ItemGuid)).Select(i => i.All)];
    }

    private Task<ItemBaseInfo[]> Give(BotClient bot, int entry, int count = 1) => Granted(bot, entry, () => Gm($"!item {entry} {count}"));

    private static async Task<EquipAppraiseResp> Appraise(BotClient bot, params long[] guids)
    {
        await bot.SendGameAsync(AppraisalService.Appraise, new EquipAppraiseReq { ItemGuids = { guids } });
        return await Next(bot, AppraisalService.Appraise, EquipAppraiseResp.Parser);
    }

    private static async Task<EquipItemOprResp> Wear(BotClient bot, long guid, int grid = 1, bool remove = false)
    {
        await bot.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq
        {
            ItemGUID = guid, Pos = (EquipGridType)grid, Opr = (EquipItemOprType)(remove ? 1 : 0),
        });
        var reply = await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser);
        Assert.Equal(0, reply.Result);
        return reply;
    }

    private static Dictionary<int, long> Entries(ItemBaseInfo item) => Enumerable.Range(0, 20)
        .TakeWhile(i => item.ItemInfos.ContainsKey(100 + i) && item.ItemInfos.ContainsKey(140 + i))
        .ToDictionary(i => (int)item.ItemInfos[100 + i].LongVal, i => item.ItemInfos[140 + i].LongVal);

    private static long[] Specials(ItemBaseInfo item) => [.. Enumerable.Range(80, 10)
        .TakeWhile(item.ItemInfos.ContainsKey).Select(i => item.ItemInfos[i].LongVal)];

    private void AssertRoll(ItemBaseInfo item)
    {
        var config = Data.Row("ItemConfig", item.ItemEntry)!.Value;
        var gear = Data.Row("EquipConfig", GameData.Int(config, "_iEquipId"))!.Value;
        var entries = Entries(item); // ToDictionary also detects duplicate attribute IDs.
        long group = GameData.Int(gear, "_iEntries");
        if (group > 0)
        {
            var counts = Data.Table("EquipEntryRandom").Values.Where(r => GameData.Int(r, "_iQuality") == GameData.Int(config, "_iQuality"))
                .Select(r => (int)GameData.Int(r, "_iNum")).ToArray();
            Assert.Contains(entries.Count, counts);
            var pool = Data.Table("EquipAttriGroupConfig").Values.Where(r => GameData.Int(r, "_iGroup") == group).ToArray();
            foreach (var (id, value) in entries)
            {
                var row = Assert.Single(pool, r => GameData.Int(r, "_iAttriID") == id);
                Assert.InRange(value, GameData.Int(row, "_iMin"), GameData.Int(row, "_iMax"));
            }
        }
        else
            Assert.Equal(GameData.Pairs(gear, "_kFixedEntries").Select(p => ((int)p.Id, (long)p.Value)).Order(), entries.Select(p => (p.Key, p.Value)).Order());
        var specials = Specials(item);
        Assert.Equal(specials.Length, specials.Distinct().Count());
        Assert.All(specials, id => Assert.NotNull(Data.Row("SpecialEffectConfig", id)));
        var families = specials.Select(id => GameData.Int(Data.Row("SpecialEffectConfig", id)!.Value, "_iGroupID")).Where(g => g != 0).ToArray();
        Assert.Equal(families.Length, families.Distinct().Count());
        Assert.Equal(1, item.ItemInfos[3].LongVal);
        Assert.True(item.ItemInfos[2].LongVal > 0);
        Assert.All(item.ItemInfos.Where(p => p.Key is >= 80 and <= 89 or >= 100 and <= 119 or >= 140 and <= 159), p => Assert.True(p.Value.HasLongVal));
    }

    [ClientDataFact]
    public async Task CutterAppraisalRollsConfiguredAffixesAndStatsAndPersistsTheSameWireState()
    {
        ItemBaseInfo chosen;
        await using (var bot = await InWorld())
        {
            var unappraised = await Give(bot, Cutter, 24);
            Assert.Equal(24, unappraised.Length);
            Assert.All(unappraised, item =>
            {
                Assert.Equal(0, item.ItemInfos[3].LongVal);
                Assert.Equal(0, item.ItemInfos[2].LongVal);
                Assert.Empty(Entries(item));
                Assert.Empty(Specials(item));
            });
            Assert.Equal(ErrorCodes.FeatureUnavailable, (await Appraise(bot, unappraised[0].ItemGuid)).Result);
            await Gm("!quest done 10103020");
            var reply = await Appraise(bot, [.. unappraised.Select(i => i.ItemGuid)]);
            Assert.Equal(0, reply.Result);
            Assert.Equal(24, reply.AppraiseResult.Count);
            foreach (var item in reply.AppraiseResult)
            {
                AssertRoll(item);
                Assert.Empty(Specials(item)); // T2 Cutter's _kSpecial is empty: the correct result is No Stunt.
                var before = unappraised.Single(i => i.ItemGuid == item.ItemGuid);
                foreach (int key in new[] { 30, 31, 60, 61 }) Assert.Equal(before.ItemInfos[key], item.ItemInfos[key]);
                Assert.Equal(1, Stored(item.ItemGuid).Attrs![3]);
            }
            chosen = reply.AppraiseResult[0];
            var delta = await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                b => b.PackInfo.GetValueOrDefault(2)?.Inc?.Items.GetValueOrDefault(chosen.ItemGuid)?.Inc?.ItemInfos.ContainsKey(100) == true);
            var attrs = delta.PackInfo[2].Inc.Items[chosen.ItemGuid].Inc.ItemInfos;
            Assert.Equal(chosen.ItemInfos[100], attrs[100].All);
            Assert.Equal(chosen.ItemInfos[2].LongVal, attrs[2].Inc.LongVal);

            var beforeStats = Service<CharacterService>().Stats(Session);
            var worn = await Wear(bot, chosen.ItemGuid);
            Assert.Equal(chosen.ItemInfos, worn.Item.ItemInfos);
            var entries = Entries(chosen);
            var properties = await Next(bot, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
                p => p.Property.Propertys.Any(v => v.PropertyId == entries.First().Key && v.Data == beforeStats[entries.First().Key] + entries.First().Value));
            foreach (var (id, value) in entries)
                Assert.Equal(beforeStats[id] + value, properties.Property.Propertys.Single(p => p.PropertyId == id).Data);
            double conversion = entries.Sum(p => GameData.Pairs(Data.Row("AttributeConversionConfig", p.Key)!.Value, "_kAttribute1")
                .Where(a => a.Id == 103).Sum(a => a.Value * p.Value));
            double percent = beforeStats[1031] + entries.Sum(p => GameData.Pairs(Data.Row("AttributeConversionConfig", p.Key)!.Value, "_kAttribute1")
                .Where(a => a.Id == 1031).Sum(a => a.Value * p.Value));
            long expectedAttack = (long)Math.Ceiling((beforeStats.Base[103] + chosen.ItemInfos[60].LongVal + conversion) * (1 + percent / 10000));
            Assert.Equal(expectedAttack, properties.Property.Propertys.Single(p => p.PropertyId == 103).Data);

            var storedBefore = JsonSerializer.Serialize(Stored(chosen.ItemGuid).Attrs);
            Assert.Equal(ErrorCodes.EquipNotInBag, (await Appraise(bot, chosen.ItemGuid)).Result);
            Assert.Equal(storedBefore, JsonSerializer.Serialize(Stored(chosen.ItemGuid).Attrs));
            var bagItem = reply.AppraiseResult[1];
            Assert.Equal(ErrorCodes.AppraiseParameter, (await Appraise(bot, bagItem.ItemGuid)).Result);
        }
        await using var again = await InWorld(create: false);
        var equipped = await Next(again, CharacterService.EquipSync, DBEquipInfoIncrement.Parser);
        Assert.Equal(chosen.ItemInfos, equipped.Items[chosen.ItemGuid].All.ItemInfos);
    }

    [ClientDataFact]
    public async Task GmLootCraftShopAndCombineAllCreateAppraisedInstanceAffixes()
    {
        await using var bot = await InWorld();
        AssertRoll(Assert.Single(await Give(bot, Purple)));
        var loot = await Granted(bot, Purple, () => OnLoop(s => Service<CharacterService>().GrantAsync(s, [(Purple, 2)])));
        Assert.Equal(2, loot.Length);
        Assert.All(loot, AssertRoll);
        await Gm("!quest done 10103020");
        await Gm("!item 1254001 1");
        await Gm("!item 1200704 2");
        await bot.SendGameAsync(EquipmentService.Craft, new EquipCraftReq { Rank = 2, Subtype = 1 });
        var craft = await Next(bot, EquipmentService.Craft, EquipCraftResp.Parser);
        Assert.Equal(0, craft.Result);
        Assert.Equal(Purple, craft.Item.ItemEntry);
        AssertRoll(craft.Item);

        // The current catalogue has no gear shop/combine recipes. Private per-host config fixtures exercise the
        // actual routed requests without writing shared data/ or claiming these synthetic offers exist in the game.
        SetRow("ShopCommodityConfig", 118, ChangedRow("ShopCommodityConfig", 118, ("_iItemId", Purple)));
        SetRow("ItemConfig", Purple, ChangedRow("ItemConfig", Purple, ("_kCombine", new[] { new[] { 1200000, 5 } })));
        await Gm("!lvl 20");
        var bought = await Granted(bot, Purple, async () =>
        {
            await bot.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = 118, Count = 1, Price = 100 });
            Assert.Equal(0, (await Next(bot, ShopService.BuyCommodity, BuyCommodityResp.Parser)).Result);
        });
        AssertRoll(Assert.Single(bought));
        await Gm("!item 1200000 5");
        var combined = await Granted(bot, Purple, async () =>
        {
            await bot.SendGameAsync(EquipmentService.ItemCombine, new ItemCombineReq { ItemId = Purple, ItemCount = 1, CostItem = { [1200000] = 5 } });
            Assert.Equal(0, (await Next(bot, EquipmentService.ItemCombine, ItemCombineResp.Parser)).Result);
        });
        AssertRoll(Assert.Single(combined));
        Assert.Equal(6, Session.Items.Count(i => i.Entry == Purple));
    }

    [ClientDataFact]
    public async Task RedAppraisalCarriesOnlyConfiguredStuntsAndDistinctValidEntries()
    {
        await using var bot = await InWorld();
        await Gm("!quest done 10103020");
        var gear = await Give(bot, Red, 24);
        var reply = await Appraise(bot, [.. gear.Select(i => i.ItemGuid)]);
        Assert.Equal(0, reply.Result);
        Assert.Equal(24, reply.AppraiseResult.Count);
        var guaranteed = Data.Table("EquipSpecialGroupConfig").Values.Where(r => GameData.Int(r, "_iGroup") == 40101)
            .Select(r => GameData.Int(r, "_iSpecialID")).ToArray();
        var optional = Data.Table("EquipSpecialGroupConfig").Values.Where(r => GameData.Int(r, "_iGroup") == 40103)
            .Select(r => GameData.Int(r, "_iSpecialID")).ToArray();
        foreach (var item in reply.AppraiseResult)
        {
            AssertRoll(item);
            Assert.Equal(2, Entries(item).Count);
            var specials = Specials(item);
            Assert.InRange(specials.Length, 1, 2);
            Assert.Contains(specials[0], guaranteed);
            if (specials.Length == 2) Assert.Contains(specials[1], optional);
        }
    }

    [ClientDataFact]
    public async Task FixedEntriesStuntRefineAndPlansPreserveTheInstanceUntilConfiguredDismantling()
    {
        await using var bot = await InWorld();
        await Gm("!lvl 50");
        var item = Assert.Single(await Give(bot, FixedArmor));
        AssertRoll(item);
        Assert.Equal(new Dictionary<int, long> { [1] = 7, [2] = 7 }, Entries(item));
        Assert.Equal([10010505], Specials(item)); // guaranteed 50303 -> refine-step Hit +25
        await Wear(bot, item.ItemGuid, 3);
        long hit = Service<CharacterService>().Stats(Session)[209];
        var character = Service<CharacterService>();
        foreach (var row in new[] { character.StrengthenRow(3, 1)!.Value, character.RefineRow(3, 1)!.Value })
            foreach (var (id, amount) in GameData.Pairs(row, "_kDeplete")) await Gm($"!item {id} {(long)amount}");
        await bot.SendGameAsync(EquipmentService.Strengthen, new StrengthenEquipmentReq { EquipPos = (EquipGridType)3, StrengthenNum = 1 });
        Assert.Equal(0, (await Next(bot, EquipmentService.Strengthen, StrengthenEquipmentRsp.Parser)).Result);
        await bot.SendGameAsync(EquipmentService.Refine, new RefineEquipmentReq { EquipPos = (EquipGridType)3 });
        var refined = await Next(bot, EquipmentService.Refine, RefineEquipmentRsp.Parser);
        Assert.Equal((0, 1), (refined.Result, refined.RefineLv));
        await Next(bot, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            p => p.Property.Propertys.Any(v => v.PropertyId == 209 && v.Data == hit + 25));
        foreach (int plan in new[] { 1, 0 })
        {
            await bot.SendGameAsync(CharacterService.EquipSwitchPlan, new EquipSwitchPlanReq { TargetIndex = plan });
            Assert.Equal(0, (await Next(bot, CharacterService.EquipSwitchPlan, EquipSwitchPlanResp.Parser)).Result);
        }
        Assert.Equal(item.ItemInfos, character.ItemInfoOf(Stored(item.ItemGuid)).ItemInfos);
        Assert.Equal(hit + 25, character.Stats(Session)[209]);

        await Wear(bot, item.ItemGuid, 3, remove: true);
        var returns = Data.Table("EquipReturnConfig").Values.Single(r => GameData.Int(r, "_iRank") == 5
            && GameData.Int(r, "_iQuality") == 6 && GameData.Int(r, "_iEntriesNum") == 2 && GameData.Int(r, "_iSpecialNum") == 1);
        long before = Session.Progress!.Currency.GetValueOrDefault(2);
        await bot.SendGameAsync(EquipmentService.ItemDecompose, new ItemDecomposeReq { DecomposeItem = { [item.ItemGuid] = 1 } });
        Assert.Equal(0, (await Next(bot, EquipmentService.ItemDecompose, ItemDecomposeResp.Parser)).Result);
        Assert.DoesNotContain(Service<ItemStore>().Load(Session.Role!.Id), i => i.Guid == item.ItemGuid);
        long refund = (long)GameData.Pairs(returns, "_kReturn").Single(p => p.Id == 2).Value;
        Assert.Equal(before + refund, Session.Progress.Currency[2]);
        var plans = DBEquipInfo.Parser.ParseFrom(Service<RoleStore>().LoadEquipPlans(Session.Role.Id)!);
        Assert.DoesNotContain(plans.EquipPlans.Values.SelectMany(p => p.ItemsEquipGrid.Values), g => g == item.ItemGuid);
    }

    [ClientDataFact]
    public async Task DuplicateAffixesSpecialFamiliesAndZeroSentinelsAreExcludedBeforeWireProjection()
    {
        const int entry = 2010160;
        // Explicit synthetic exclusion fixture, not evidence of the unavailable original exclusion-table contents.
        SetRow("EquipConfig", entry, ChangedRow("EquipConfig", entry,
            ("_iEntries", 99100), ("_kFixedEntries", new[] { new[] { 1, 9 } }), ("_kFixedSpecialAttribute", new[] { 1 }),
            ("_kSpecial", new[] { new[] { 99100, 10000 }, [99101, 10000], [1, 10000] })));
        SetRow("EquipAttriGroupConfig", 99100, new { _iID = 99100, _iGroup = 99100, _iAttriID = 1, _iMin = 99, _iMax = 99, _iWeight = 1000 });
        SetRow("EquipAttriGroupConfig", 99101, new { _iID = 99101, _iGroup = 99100, _iAttriID = 2, _iMin = 7, _iMax = 7, _iWeight = 1 });
        SetRow("EquipAttriGroupConfig", 99102, new { _iID = 99102, _iGroup = 99100, _iAttriID = 2, _iMin = 7, _iMax = 7, _iWeight = 1000 });
        SetRow("EquipAttriGroupConfig", 99103, new { _iID = 99103, _iGroup = 99100, _iAttriID = 3, _iMin = 8, _iMax = 8, _iWeight = 1 });
        SetRow("EquipAttriGroupConfig", 99104, new { _iID = 99104, _iGroup = 99100, _iAttriID = 4, _iMin = 99, _iMax = 99, _iWeight = 0 });
        SetRow("EquipSpecialGroupConfig", 99100, new { _iID = 99100, _iGroup = 99100, _iSpecialID = 2, _iWeight = 1 });
        SetRow("EquipSpecialGroupConfig", 99101, new { _iID = 99101, _iGroup = 99101, _iSpecialID = 3, _iWeight = 1 });
        await using var bot = await InWorld();
        var item = Assert.Single(await Give(bot, entry));
        Assert.Equal(new Dictionary<int, long> { [1] = 9, [2] = 7, [3] = 8 }, Entries(item));
        Assert.Equal([1, 3], Specials(item)); // special 2 conflicts with fixed 1; zero never occupies a slot
        Assert.False(item.ItemInfos.ContainsKey(82));
        Assert.False(item.ItemInfos.ContainsKey(103));
        Assert.True(item.ItemInfos[2].LongVal > 0);
    }

    [ClientDataFact]
    public async Task ConversionStuntsAndPotionBonusApplyOnlyWhileEquipped()
    {
        // Raise MATK above the conversion step so the test exercises a nonzero type-8 bonus.
        var basic = EquipmentAffixes.Rows(Data.Row("EquipConfig", FixedArmor)!.Value, "_kBasicAttribute")
            .Append([104, 300, 300]).ToArray();
        SetRow("EquipConfig", FixedArmor, ChangedRow("EquipConfig", FixedArmor,
            ("_kBasicAttribute", basic), ("_kSpecial", Array.Empty<int[]>()),
            ("_kFixedSpecialAttribute", new[] { 10010404, 10020402, 10020404, 10020401 })));
        SetRow("SpecialEffectConfig", 10020401, ChangedRow("SpecialEffectConfig", 10020401,
            ("_kSpecialEffect", new[] { new[] { 13, 10000, 5000 } }))); // force chance for an exact recovery assertion
        await using var bot = await InWorld();
        await Gm("!lvl 50");
        var item = Assert.Single(await Give(bot, FixedArmor));
        var empty = Service<CharacterService>().Stats(Session);
        await Wear(bot, item.ItemGuid, 3);
        var props = await Next(bot, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            p => p.Property.Propertys.Any(v => v.PropertyId == 1033 && v.Data > 0));
        var now = props.Property.Propertys.ToDictionary(v => (int)v.PropertyId, v => v.Data);
        // STR/AGI fixed entries feed conversion; type 4 adds 4 active stunts * 3 PATK before types 8 and 14.
        double fixedAttack = new[] { 1, 2 }.Sum(id => GameData.Pairs(Data.Row("AttributeConversionConfig", id)!.Value, "_kAttribute1")
            .Where(p => p.Id == 103).Sum(p => p.Value * 7));
        double percentAttack = empty[1031] + 7 * 20; // STR also contributes per_atk; preserve the normal pipeline.
        long sourceAtk = (long)Math.Ceiling((empty.Base[103] + fixedAttack + 12) * (1 + percentAttack / 10000));
        long sourceMatk = (long)Math.Ceiling((empty.Base[104] + 300) * (1 + empty[1041] / 10000d));
        Assert.Equal(Math.Min(sourceMatk / 100, 160), now[1033]);
        Assert.Equal(Math.Min((sourceAtk + sourceMatk) / 10 * 4, 4000), now[1013]);
        Assert.Equal(sourceAtk + now[1033], now[103]);

        long potion = Session.Items.Single(i => i.Entry == 1000000).Guid;
        await OnLoop(s => Service<OwnerRegistry>().RunSessionAsync("affix potion fixture",
            [ReservationKey.Role(s.Role!.Id)], s, async () =>
            {
                // The exact recovery assertion starts at 1 HP; exclude unrelated five-second combat regeneration.
                s.LastCombatMs = MonsterService.NowMs;
                s.StatusBits |= 1L << 15;
                s.NoRegenUntilMs = MonsterService.NowMs + 10000; // fighting no longer pauses regeneration
                await Service<CharacterService>().SetHpSpAsync(s, 1, null);
            }));
        await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = potion, ItemCount = 1 });
        Assert.Equal(0, (await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        long recovery = (long)EquipmentAffixes.Rows(Data.Row("ItemConfig", 1000000)!.Value, "_iUseEffect").Single(e => e[0] == 100)[2];
        await Next(bot, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            p => p.Property.Propertys.Any(v => v.PropertyId == 2000 && v.Data == 1 + recovery * 3 / 2));
        await Wear(bot, item.ItemGuid, 3, remove: true);
        await Next(bot, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            p => p.Property.Propertys.Any(v => v.PropertyId == 1033 && v.Data == 0)
                && p.Property.Propertys.Any(v => v.PropertyId == 1013 && v.Data == 0));
        var after = Service<CharacterService>().Stats(Session);
        Assert.Equal(empty[103], after[103]);
        Assert.Equal(0, after[1033]);
        Assert.Equal(0, after[1013]);
        Assert.Equal(101, EquipmentStunts.PotionRecovery(Session, 101, 1, Data));
    }

    [ClientDataFact]
    public async Task LegacyAppraisedGearGainsMissingEntriesOnceWithoutReplacingItsSavedStunt()
    {
        long guid;
        Dictionary<int, long> oldAttrs = null!;
        await using (var bot = await InWorld())
        {
            guid = Assert.Single(await Give(bot, Red)).ItemGuid;
            var session = Session;
            await Service<GameLoop>().InvokeAsync(() => Service<OwnerRegistry>().RunSessionAsync(
                "legacy affix fixture", [ReservationKey.Role(session.Role!.Id)], session, () =>
                {
                    var old = session.Items.Single(i => i.Guid == guid);
                    old.Attrs![3] = 1;
                    old.Attrs[80] = 10010404; // existing, valid 40101 stunt; the old emulator omitted entries
                    old.IsLocked = true;
                    old.BindType = 1;
                    old.StringAttrs = new() { [10000] = "legacy creator" };
                    old.Enchantments[1] = new ItemEnchantment { Attr = new() { [1] = 3 }, GetTime = 12345 };
                    oldAttrs = new(old.Attrs);
                    Service<ItemStore>().Update(old);
                    return Task.CompletedTask;
                }));
        }

        ItemBaseInfo repaired;
        await using (var again = await InWorld(create: false))
        {
            var bag = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
            repaired = bag.PackInfo[2].All.Items[guid];
            AssertRoll(repaired);
            Assert.Equal([10010404], Specials(repaired));
            Assert.All(oldAttrs, p => Assert.Equal(p.Value, repaired.ItemInfos[p.Key].LongVal));
            Assert.Equal("legacy creator", repaired.ItemInfos[10000].StrVal);
            var saved = Stored(guid);
            Assert.True(saved.IsLocked);
            Assert.Equal(1, saved.BindType);
            Assert.Equal(3, saved.Enchantments[1].Attr[1]);
            Assert.Equal(12345, saved.Enchantments[1].GetTime);
            Assert.Equal(2, EquipmentAffixes.Entries(saved).Count());
        }
        await using var third = await InWorld(create: false);
        var unchanged = await Next(third, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        Assert.Equal(repaired, unchanged.PackInfo[2].All.Items[guid]);
    }

    private object ChangedRow(string table, long id, params (string Key, object Value)[] changes)
    {
        var row = JsonSerializer.Deserialize<Dictionary<string, object>>(Data.Row(table, id)!.Value.GetRawText())!;
        foreach (var (key, value) in changes) row[key] = value;
        return row;
    }

    private void SetRow(string table, long id, object row) =>
        ((Dictionary<string, JsonElement>)Data.Table(table))[id.ToString()] = JsonSerializer.SerializeToElement(row);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

}
