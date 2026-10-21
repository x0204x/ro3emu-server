using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Wire coverage of Soul Echo generation, quest gate, deployment, feeding, stats, power and persistence.</summary>
public sealed partial class EmblemTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-emblem-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication _app = null!;
    private string _httpUrl = "";

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
        _httpUrl = $"http://127.0.0.1:{http}";
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
        var bot = new BotClient(_httpUrl, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
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

    private async Task Gm(string account, string command)
    {
        using var http = new HttpClient();
        var response = await http.PostAsync($"{_httpUrl}/ro3emu/gm?account={account}", new StringContent(command));
        response.EnsureSuccessStatusCode();
    }

    private static ItemBaseInfo Granted(DBItemsInfoIncrement change, int entry)
    {
        var pack = change.PackInfo[5];
        return (pack.All != null ? pack.All.Items.Values : pack.Inc.Items.Values.Select(v => v.All))
            .Single(i => i.ItemEntry == entry);
    }

    [ClientDataFact]
    public async Task GenerationGateWearUpgradeUnwearAndRelogStayCoherent()
    {
        long guid;
        long wornPower;
        await using (var alice = await InWorld("Alice"))
        {
            await using var bob = await InWorld("Bob");
            var first = await Next(alice, EmblemService.PlanSync, DBEmblemInfoIncrement.Parser);
            Assert.True(first.SyncAllMark);
            Assert.Equal(0, first.CurrentPlan);
            Assert.Empty(first.EmblePlans[0].All.SetUpEmblem);
            Assert.Equal(0, first.EmblePlans[0].All.Power.SeasonPower);

            await Gm("alice", "!item 1830101");
            var grant = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.PackInfo.ContainsKey(5));
            var generated = Granted(grant, 1830101);
            guid = generated.ItemGuid;
            Assert.Equal(1, generated.Stack);
            Assert.Equal(0, generated.ItemInfos[1001].LongVal);
            Assert.Equal(0, generated.ItemInfos[1002].LongVal);
            Assert.Equal(0, generated.ItemInfos[1003].LongVal);
            Assert.Equal(101, generated.ItemInfos[1031].LongVal);
            Assert.Equal(300, generated.ItemInfos[1041].LongVal);
            Assert.True(generated.ItemInfos.ContainsKey(1051));
            Assert.True(generated.ItemInfos.ContainsKey(1071));
            Assert.False(generated.ItemInfos.ContainsKey(1052));
            var data = _app.Services.GetRequiredService<GameData>();
            var subrow = data.Row("EmblemSubAttriGroupConfig", generated.ItemInfos[1051].LongVal)!.Value;
            Assert.Equal(3, GameData.Int(subrow, "_iGroup"));
            Assert.True(GameData.Int(subrow, "_iWeight") > 0);
            Assert.InRange(generated.ItemInfos[1071].LongVal, GameData.Int(subrow, "_iMin"), GameData.Int(subrow, "_iMax"));

            await alice.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 1, EmblemGuid = guid });
            Assert.Equal(ErrorCodes.FunctionLocked, (await Next(alice, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
            await alice.SendGameAsync(EmblemService.SetDown, new SetDownEmblemReq { EmblemPos = 1 });
            Assert.Equal(ErrorCodes.FunctionLocked, (await Next(alice, EmblemService.SetDown, SetDownEmblemResp.Parser)).Result);
            await alice.SendGameAsync(EmblemService.Upgrade, new UpgradeEmblemReq { EmblemGuid = guid, ConsumeItems = { [1257001] = 1 } });
            Assert.Equal(ErrorCodes.FunctionLocked, (await Next(alice, EmblemService.Upgrade, UpgradeEmblemResp.Parser)).Result);

            await Gm("alice", "!quest done 10103040");
            var opened = await Next(alice, FunctionService.FunctionOpenSync, FunctionOpenInfoIncrement.Parser, m => m.FunctionOpenInfos.ContainsKey(55));
            Assert.True(opened.FunctionOpenInfos[55].All);

            await alice.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 6, EmblemGuid = guid });
            Assert.Equal(ErrorCodes.EmblemSlot, (await Next(alice, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
            await alice.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 1, EmblemGuid = 999999999 });
            Assert.Equal(ErrorCodes.EmblemInvalid, (await Next(alice, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);

            await alice.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 1, EmblemGuid = guid });
            var worn = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.PackInfo.ContainsKey(5) && m.PackInfo[5].Inc?.Items.ContainsKey(guid) == true);
            Assert.Equal(2, worn.PackInfo[5].Inc.Items[guid].Inc.ItemInfos[10001].Inc.LongVal);
            Assert.Equal(1, worn.PackInfo[5].Inc.Items[guid].Inc.ItemInfos[10002].All.LongVal);
            var plan = await Next(alice, EmblemService.PlanSync, DBEmblemInfoIncrement.Parser, m => !m.SyncAllMark);
            Assert.Equal(guid, plan.EmblePlans[0].Inc.SetUpEmblem[1].All);
            var stats = await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
                m => m.Property.Propertys.Any(p => p.PropertyId == 23 && p.Data > 0));
            long wornHp = stats.Property.Propertys.Single(p => p.PropertyId == 101).Data;
            Assert.True(wornHp > 0);
            var observed = await Next(bob, CharacterService.AoiAttrChange, AOIAttrChange.Parser,
                m => m.EntityUuid == alice.Self!.Uuid && m.Property.Any(p => p.Idx == 101 && p.Data == wornHp));
            Assert.Equal(wornHp, observed.Property.Single(p => p.Idx == 101).Data);
            var power = await Next(alice, 31502, PlayerChangePowerNtf.Parser, m => m.CurPower.SeasonPower > 0);
            wornPower = power.CurPower.SeasonPower;
            Assert.Equal(225 + GameData.Int(subrow, "_iSeasonPower"), wornPower);
            Assert.Equal(wornPower, plan.EmblePlans[0].Inc.Power.Inc.SeasonPower);
            Assert.Equal(power.CurPower.SeasonPower, (await Next(alice, 31503, PlayerPowerPackageNtf.Parser, m => m.PowerPackage.EmblemScore.SeasonPower > 0)).PowerPackage.EmblemScore.SeasonPower);
            var basePower = await Next(alice, CharacterService.RoleBaseInfoSync, RoleBaseInfoIncrement.Parser,
                m => !m.SyncAllMark && m.CurPower?.Inc?.SeasonPower == wornPower);
            Assert.Equal(wornPower, basePower.CurPower.Inc.SeasonPower);
            Assert.Equal(0, (await Next(alice, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);

            await alice.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 2, EmblemGuid = guid });
            Assert.Equal(ErrorCodes.EmblemAlreadyDeployed, (await Next(alice, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
            await alice.SendGameAsync(EmblemService.Upgrade, new UpgradeEmblemReq { EmblemGuid = guid, ConsumeEmblemGuids = { guid } });
            Assert.Equal(ErrorCodes.EmblemInvalid, (await Next(alice, EmblemService.Upgrade, UpgradeEmblemResp.Parser)).Result);
            await alice.SendGameAsync(EmblemService.Upgrade, new UpgradeEmblemReq { EmblemGuid = guid, ConsumeItems = { [1257001] = 1 } });
            Assert.Equal(ErrorCodes.ItemsShort, (await Next(alice, EmblemService.Upgrade, UpgradeEmblemResp.Parser)).Result);

            await Gm("alice", "!item 1257001");
            await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.GuidToNew.Count > 0);
            await alice.SendGameAsync(EmblemService.Upgrade, new UpgradeEmblemReq { EmblemGuid = guid, ConsumeItems = { [1257001] = 1 } });
            Assert.Equal(0, (await Next(alice, EmblemService.Upgrade, UpgradeEmblemResp.Parser)).Result);
            var upgraded = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.PackInfo.ContainsKey(5) && m.PackInfo[5].Inc?.Items.ContainsKey(guid) == true && m.PackInfo[5].Inc.Items[guid].Inc?.ItemInfos.ContainsKey(1001) == true);
            Assert.Equal(2, upgraded.PackInfo[5].Inc.Items[guid].Inc.ItemInfos[1001].Inc.LongVal);
            Assert.False(upgraded.PackInfo[5].Inc.Items[guid].Inc.ItemInfos.ContainsKey(1002)); // unchanged zero
            Assert.Equal(1000, upgraded.PackInfo[5].Inc.Items[guid].Inc.ItemInfos[1003].Inc.LongVal);
            Assert.Equal(420, upgraded.PackInfo[5].Inc.Items[guid].Inc.ItemInfos[1041].Inc.LongVal);
            Assert.Equal(12, upgraded.PackInfo[5].Inc.Items[guid].Inc.ItemInfos[1042].Inc.LongVal);
            Assert.Equal(7, upgraded.PackInfo[5].Inc.Items[guid].Inc.ItemInfos[1043].Inc.LongVal);
            var upgradedPower = await Next(alice, 31502, PlayerChangePowerNtf.Parser, m => m.CurPower.SeasonPower == wornPower + 70);
            Assert.Equal(70, upgradedPower.ChangePower.SeasonPower);
            Assert.Contains(upgraded.PackInfo[1].Inc.Items.Values, i => i.Del);

            // A replacement updates both the current plan and the two item setup projections.
            await Gm("alice", "!item 1830101");
            var secondGrant = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.PackInfo.ContainsKey(5) && m.PackInfo[5].Inc?.Items.Values.Any(v => v.All?.ItemEntry == 1830101) == true);
            long replacement = Granted(secondGrant, 1830101).ItemGuid;
            await alice.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 1, EmblemGuid = replacement });
            var replaced = await Next(alice, EmblemService.SetUp, SetUpEmblemResp.Parser);
            Assert.Equal(0, replaced.Result);
            Assert.Equal(guid, replaced.OldEmblemGuid);
            var replacementPlan = await Next(alice, EmblemService.PlanSync, DBEmblemInfoIncrement.Parser,
                m => m.EmblePlans.ContainsKey(0) && m.EmblePlans[0].Inc?.SetUpEmblem.ContainsKey(1) == true);
            Assert.Equal(replacement, replacementPlan.EmblePlans[0].Inc.SetUpEmblem[1].All);

            await alice.SendGameAsync(EmblemService.SetDown, new SetDownEmblemReq { EmblemPos = 1 });
            Assert.Equal(0, (await Next(alice, EmblemService.SetDown, SetDownEmblemResp.Parser)).Result);
            var cleared = await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
                m => m.Property.Propertys.Any(p => p.PropertyId == 23 && p.Data == 0));
            Assert.Equal(0, cleared.Property.Propertys.Single(p => p.PropertyId == 23).Data);
            Assert.Equal(0, cleared.Property.Propertys.Single(p => p.PropertyId == 24).Data);
            Assert.True(cleared.Property.Propertys.Single(p => p.PropertyId == 101).Data < wornHp);
            await Next(bob, CharacterService.AoiAttrChange, AOIAttrChange.Parser,
                m => m.EntityUuid == alice.Self!.Uuid && m.Property.Any(p => p.Idx == 101 && p.Data < wornHp));
            await alice.SendGameAsync(EmblemService.SetDown, new SetDownEmblemReq { EmblemPos = 1 });
            Assert.Equal(ErrorCodes.EmblemSlotEmpty, (await Next(alice, EmblemService.SetDown, SetDownEmblemResp.Parser)).Result);

        }
        await using var again = await InWorld("Alice", create: false);
        var persisted = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        var item = persisted.PackInfo[5].All.Items[guid];
        Assert.Equal(2, item.ItemInfos[1001].LongVal);
        Assert.Equal(0, item.ItemInfos[10001].LongVal);
        var savedPlan = await Next(again, EmblemService.PlanSync, DBEmblemInfoIncrement.Parser);
        Assert.Empty(savedPlan.EmblePlans[0].All.SetUpEmblem);
        var savedPower = await Next(again, 31502, PlayerChangePowerNtf.Parser);
        Assert.Equal(0, savedPower.CurPower.SeasonPower);
        Assert.True(savedPower.MaxHistoryPower.SeasonPower >= wornPower);
    }

    [ClientDataFact]
    public async Task RefusedFeedsKeepSelectedItemsAndCapRejectsFurtherPayment()
    {
        long target, protectedWithReturns;
        await using (var alice = await InWorld("Alice"))
        {
            await using var bob = await InWorld("Bob");
            await Gm("alice", "!quest done 10103040");
            await Next(alice, FunctionService.FunctionOpenSync, FunctionOpenInfoIncrement.Parser, m => m.FunctionOpenInfos.ContainsKey(55));
            await Gm("alice", "!item 1830101 3");
            var owned = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.PackInfo.ContainsKey(5));
            var guids = owned.PackInfo[5].All.Items.Keys.Order().ToArray();
            Assert.Equal(3, guids.Length);
            target = guids[0]; long feed = guids[1], spare = guids[2];
            await Gm("bob", "!item 1830101");
            long foreign = Granted(await Next(bob, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.PackInfo.ContainsKey(5)), 1830101).ItemGuid;

            await alice.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 1, EmblemGuid = foreign });
            Assert.Equal(ErrorCodes.EmblemInvalid, (await Next(alice, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
            var startBag = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
            long dagger = startBag.PackInfo[2].All.Items.Values.Single(i => i.ItemEntry == 2010121).ItemGuid;
            await alice.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 1, EmblemGuid = dagger });
            Assert.Equal(ErrorCodes.EmblemInvalid, (await Next(alice, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
            await alice.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 1, EmblemGuid = target });
            Assert.Equal(0, (await Next(alice, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);

            async Task Refuse(UpgradeEmblemReq req, int expected)
            {
                await alice.SendGameAsync(EmblemService.Upgrade, req);
                Assert.Equal(expected, (await Next(alice, EmblemService.Upgrade, UpgradeEmblemResp.Parser)).Result);
            }

            await Refuse(new UpgradeEmblemReq { EmblemGuid = spare, ConsumeEmblemGuids = { target } }, ErrorCodes.EmblemInvalid); // worn feed
            await Refuse(new UpgradeEmblemReq { EmblemGuid = target, ConsumeEmblemGuids = { feed, feed } }, ErrorCodes.EmblemInvalid);
            await Refuse(new UpgradeEmblemReq { EmblemGuid = target, ConsumeEmblemGuids = { foreign } }, ErrorCodes.EmblemInvalid);
            await Refuse(new UpgradeEmblemReq { EmblemGuid = target, ConsumeItems = { [1257001] = 0 } }, ErrorCodes.Parameter);
            await Refuse(new UpgradeEmblemReq { EmblemGuid = target, ConsumeItems = { [1257001] = -1 } }, ErrorCodes.Parameter);
            await Refuse(new UpgradeEmblemReq { EmblemGuid = target }, ErrorCodes.Parameter);
            await Refuse(new UpgradeEmblemReq { EmblemGuid = target, ConsumeItems = { [1000000] = 1 } }, ErrorCodes.Parameter);

            await Gm("alice", "!item 1257002 3");
            await Refuse(new UpgradeEmblemReq { EmblemGuid = target, ConsumeItems = { [1257002] = 4 } }, ErrorCodes.ItemsShort);
            await alice.SendGameAsync(EmblemService.Upgrade, new UpgradeEmblemReq { EmblemGuid = target, ConsumeItems = { [1257002] = 3 } });
            Assert.Equal(0, (await Next(alice, EmblemService.Upgrade, UpgradeEmblemResp.Parser)).Result);
            var capped = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.PackInfo.ContainsKey(5) && m.PackInfo[5].Inc?.Items.ContainsKey(target) == true
                    && m.PackInfo[5].Inc.Items[target].Inc?.ItemInfos.ContainsKey(1001) == true);
            Assert.Equal(10, capped.PackInfo[5].Inc.Items[target].Inc.ItemInfos[1001].Inc.LongVal);
            Assert.Equal(9900, capped.PackInfo[5].Inc.Items[target].Inc.ItemInfos[1002].Inc.LongVal);
            Assert.Equal(30000, capped.PackInfo[5].Inc.Items[target].Inc.ItemInfos[1003].Inc.LongVal);
            await Refuse(new UpgradeEmblemReq { EmblemGuid = target, ConsumeEmblemGuids = { feed } }, ErrorCodes.EmblemCannotLevel);

            // The failed feed remains available; an ordinary free emblem can consume it.
            await alice.SendGameAsync(EmblemService.Upgrade, new UpgradeEmblemReq { EmblemGuid = spare, ConsumeEmblemGuids = { feed } });
            Assert.Equal(0, (await Next(alice, EmblemService.Upgrade, UpgradeEmblemResp.Parser)).Result);
            var fed = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.PackInfo.ContainsKey(5) && m.PackInfo[5].Inc?.Items.ContainsKey(feed) == true && m.PackInfo[5].Inc.Items[feed].Del);
            Assert.Equal(100, fed.PackInfo[5].Inc.Items[spare].Inc.ItemInfos[1002].Inc.LongVal);
            Assert.Equal(100, fed.PackInfo[5].Inc.Items[spare].Inc.ItemInfos[1003].Inc.LongVal);
            await Gm("alice", "!item 1850111"); // this configured Soul Echo has a real decompose return
            protectedWithReturns = Granted(await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.PackInfo.ContainsKey(5) && m.PackInfo[5].Inc?.Items.Values.Any(v => v.All?.ItemEntry == 1850111) == true), 1850111).ItemGuid;
            await alice.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 2, EmblemGuid = protectedWithReturns });
            Assert.Equal(0, (await Next(alice, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
            await Gm("alice", "!lvl 20"); // dismantling (function 2602) opens at Base 20
            var decompose = new ItemDecomposeReq(); decompose.DecomposeItem[protectedWithReturns] = 1;
            await alice.SendGameAsync(EquipmentService.ItemDecompose, decompose);
            Assert.Equal(ErrorCodes.ItemsShort, (await Next(alice, EquipmentService.ItemDecompose, ItemDecomposeResp.Parser)).Result);
            await Gm("alice", "!delitem 1830101 99"); // generic delete cannot remove the deployed plan GUID
        }
        await using var again = await InWorld("Alice", create: false);
        var persisted = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        Assert.Contains(target, persisted.PackInfo[5].All.Items.Keys);
        Assert.Contains(protectedWithReturns, persisted.PackInfo[5].All.Items.Keys);
        var plan = await Next(again, EmblemService.PlanSync, DBEmblemInfoIncrement.Parser);
        Assert.Equal(target, plan.EmblePlans[0].All.SetUpEmblem[1]);
        Assert.Equal(protectedWithReturns, plan.EmblePlans[0].All.SetUpEmblem[2]);
        var power = await Next(again, 31502, PlayerChangePowerNtf.Parser);
        Assert.Equal(plan.EmblePlans[0].All.Power.SeasonPower, power.CurPower.SeasonPower);
        Assert.Equal(power.CurPower.SeasonPower, (await Next(again, CharacterService.RoleBaseInfoSync, RoleBaseInfoIncrement.Parser)).CurPower.All.SeasonPower);
    }

    [ClientDataFact]
    public async Task EmptySubattributePoolStaysEmptyAndMarksUnlockAtLevelThresholds()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!quest done 10103040");
        await Next(alice, FunctionService.FunctionOpenSync, FunctionOpenInfoIncrement.Parser, m => m.FunctionOpenInfos.ContainsKey(55));
        await Gm("alice", "!item 1890112"); // group 0, no _kSubAttributeNum; marks 1001 at levels 5 and 10
        var item = Granted(await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.PackInfo.ContainsKey(5)), 1890112);
        Assert.False(item.ItemInfos.ContainsKey(1051));
        Assert.False(item.ItemInfos.ContainsKey(1021));
        Assert.False(item.ItemInfos.ContainsKey(1091));
        await alice.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 5, EmblemGuid = item.ItemGuid });
        Assert.Equal(0, (await Next(alice, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
        var worn = await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            m => m.Property.Propertys.Any(p => p.PropertyId == 23 && p.Data == 15));
        // Novice HP (30 + VIT 40 + emblem 500) * 1.002, rounded up by the existing stat pipeline.
        Assert.Equal(572, worn.Property.Propertys.Single(p => p.PropertyId == 101).Data);
        await Gm("alice", "!item 1257002 3");
        var req = new UpgradeEmblemReq { EmblemGuid = item.ItemGuid }; req.ConsumeItems[1257002] = 3;
        await alice.SendGameAsync(EmblemService.Upgrade, req);
        Assert.Equal(0, (await Next(alice, EmblemService.Upgrade, UpgradeEmblemResp.Parser)).Result);
        var changed = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.ContainsKey(5) && m.PackInfo[5].Inc?.Items.ContainsKey(item.ItemGuid) == true
                && m.PackInfo[5].Inc.Items[item.ItemGuid].Inc?.ItemInfos.ContainsKey(1021) == true);
        var attrs = changed.PackInfo[5].Inc.Items[item.ItemGuid].Inc.ItemInfos;
        Assert.Equal(1001, attrs[1021].All.LongVal);
        Assert.Equal(1001, attrs[1022].All.LongVal);
        Assert.False(attrs.ContainsKey(1023));
        Assert.False(attrs.ContainsKey(1051));
        Assert.False(attrs.ContainsKey(1091));
        Assert.Equal(11, attrs[1001].Inc.LongVal);
        Assert.Equal(4800, attrs[1002].Inc.LongVal);
        var stats = await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            m => m.Property.Propertys.Any(p => p.PropertyId == 23 && p.Data == 48));
        Assert.Equal(1674, stats.Property.Propertys.Single(p => p.PropertyId == 101).Data);
        Assert.Equal(32, stats.Property.Propertys.Single(p => p.PropertyId == 24).Data);
        Assert.Equal(1200, (await Next(alice, 31502, PlayerChangePowerNtf.Parser, m => m.CurPower.SeasonPower == 1200)).CurPower.SeasonPower);
    }

    [ClientDataTheory]
    [InlineData(5, 4700, 5700)]
    [InlineData(6, 6700, 7030)]
    [InlineData(11, 25200, 21160)]
    [InlineData(16, 69200, 49440)]
    public async Task FeedingLeveledEmblemsUsesTheCorrectExperienceBracket(int level, int invested, int expected)
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!quest done 10103040");
        await Gm("alice", "!item 1890112 2");
        var grant = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.PackInfo.ContainsKey(5));
        var guids = grant.PackInfo[5].All.Items.Keys.Order().ToArray();
        long target = guids[0], feed = guids[1];
        await Gm("alice", $"!item 1257000 {invested / 100}");
        await alice.SendGameAsync(EmblemService.Upgrade, new UpgradeEmblemReq { EmblemGuid = feed, ConsumeItems = { [1257000] = invested / 100 } });
        Assert.Equal(0, (await Next(alice, EmblemService.Upgrade, UpgradeEmblemResp.Parser)).Result);
        var leveled = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.ContainsKey(5) && m.PackInfo[5].Inc?.Items[feed].Inc?.ItemInfos.ContainsKey(1001) == true);
        Assert.Equal(level, leveled.PackInfo[5].Inc.Items[feed].Inc.ItemInfos[1001].Inc.LongVal);
        await alice.SendGameAsync(EmblemService.Upgrade, new UpgradeEmblemReq { EmblemGuid = target, ConsumeEmblemGuids = { feed } });
        Assert.Equal(0, (await Next(alice, EmblemService.Upgrade, UpgradeEmblemResp.Parser)).Result);
        var consumed = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.ContainsKey(5) && m.PackInfo[5].Inc?.Items.ContainsKey(feed) == true && m.PackInfo[5].Inc.Items[feed].Del);
        Assert.Equal(expected, consumed.PackInfo[5].Inc.Items[target].Inc.ItemInfos[1003].Inc.LongVal);
    }

    [ClientDataFact]
    public async Task FailedUpgradeWriteRollsBackMaterialsInstancesAndLiveState()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!quest done 10103040");
        await Gm("alice", "!item 1830101 2");
        await Gm("alice", "!item 1257001 2");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
        var store = _app.Services.GetRequiredService<ItemStore>();
        var emblems = session.Items.Where(i => i.Entry == 1830101).OrderBy(i => i.Guid).ToArray();
        long target = emblems[0].Guid, feed = emblems[1].Guid;
        long material = session.Items.Single(i => i.Entry == 1257001).Guid;
        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(),
            $"CREATE TRIGGER reject_emblem_update BEFORE UPDATE OF attrs ON item WHEN NEW.guid = {target} BEGIN SELECT RAISE(ABORT, 'test emblem write failure'); END");
        var req = new UpgradeEmblemReq { EmblemGuid = target, ConsumeEmblemGuids = { feed }, ConsumeItems = { [1257001] = 1 } };
        await Assert.ThrowsAsync<SqliteException>(() => _app.Services.GetRequiredService<GameLoop>().InvokeAsync(
            () => _app.Services.GetRequiredService<EmblemService>().UpgradeAsync(session, req)));
        foreach (var inventory in new[] { session.Items, store.Load(session.Role!.Id) })
        {
            Assert.Contains(inventory, i => i.Guid == feed && i.Stack == 1);
            Assert.Equal(2, inventory.Single(i => i.Guid == material).Stack);
            Assert.Equal(0, inventory.Single(i => i.Guid == target).Attrs![1003]);
        }
        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(), "DROP TRIGGER reject_emblem_update");
        await alice.SendGameAsync(EmblemService.Upgrade, req);
        Assert.Equal(0, (await Next(alice, EmblemService.Upgrade, UpgradeEmblemResp.Parser)).Result);
        var saved = store.Load(session.Role!.Id);
        Assert.DoesNotContain(saved, i => i.Guid == feed);
        Assert.Equal(1, saved.Single(i => i.Guid == material).Stack);
        Assert.Equal(1100, saved.Single(i => i.Guid == target).Attrs![1003]);
    }

    [ClientDataFact]
    public async Task MissingUnlockStateDoesNotBypassTheQuestGate()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!item 1830101");
        long guid = Granted(await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.PackInfo.ContainsKey(5)), 1830101).ItemGuid;
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
        var quests = session.Tasks;
        session.Tasks = null;
        await alice.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 1, EmblemGuid = guid });
        Assert.Equal(ErrorCodes.FunctionLocked, (await Next(alice, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
        session.Tasks = quests;
        var functions = (IDictionary<string, System.Text.Json.JsonElement>)_app.Services.GetRequiredService<GameData>().Table("FunctionConfig");
        functions.Remove("55"); // only this fixture's cache; shared config files remain read-only
        await alice.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 1, EmblemGuid = guid });
        Assert.Equal(ErrorCodes.FunctionLocked, (await Next(alice, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
        await alice.SendGameAsync(EmblemService.SetDown, new SetDownEmblemReq { EmblemPos = 1 });
        Assert.Equal(ErrorCodes.FunctionLocked, (await Next(alice, EmblemService.SetDown, SetDownEmblemResp.Parser)).Result);
        await alice.SendGameAsync(EmblemService.Upgrade, new UpgradeEmblemReq { EmblemGuid = guid });
        Assert.Equal(ErrorCodes.FunctionLocked, (await Next(alice, EmblemService.Upgrade, UpgradeEmblemResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task OtherSeasonEmblemsCannotBeWornUpgradedOrConsumed()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!quest done 10103040");
        await Gm("alice", "!item 1830101");
        long invalid = Granted(await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.PackInfo.ContainsKey(5)), 1830101).ItemGuid;
        await Gm("alice", "!item 1890112");
        long valid = Granted(await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.ContainsKey(5) && m.PackInfo[5].Inc?.Items.Values.Any(i => i.All?.ItemEntry == 1890112) == true), 1890112).ItemGuid;
        var table = (IDictionary<string, System.Text.Json.JsonElement>)_app.Services.GetRequiredService<GameData>().Table("ItemConfig");
        var row = System.Text.Json.Nodes.JsonNode.Parse(table["1830101"].GetRawText())!;
        row["_kMultiverseArray"] = new System.Text.Json.Nodes.JsonArray(_app.Services.GetRequiredService<SeasonService>().PlayBook + 10000);
        table["1830101"] = System.Text.Json.JsonSerializer.SerializeToElement(row);
        await alice.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 1, EmblemGuid = invalid });
        Assert.Equal(ErrorCodes.EmblemOtherSeason, (await Next(alice, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
        await alice.SendGameAsync(EmblemService.Upgrade, new UpgradeEmblemReq { EmblemGuid = invalid, ConsumeEmblemGuids = { valid } });
        Assert.Equal(ErrorCodes.EmblemOtherSeason, (await Next(alice, EmblemService.Upgrade, UpgradeEmblemResp.Parser)).Result);
        await alice.SendGameAsync(EmblemService.Upgrade, new UpgradeEmblemReq { EmblemGuid = valid, ConsumeEmblemGuids = { invalid } });
        Assert.Equal(ErrorCodes.EmblemInvalid, (await Next(alice, EmblemService.Upgrade, UpgradeEmblemResp.Parser)).Result);
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
        Assert.All(session.Items.Where(i => i.Guid == invalid || i.Guid == valid), i => Assert.Equal(0, i.Attrs![1003]));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

}
