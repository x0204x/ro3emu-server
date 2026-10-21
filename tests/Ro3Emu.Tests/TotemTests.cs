using Google.Protobuf;
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

/// <summary>Real Divine Totem requests, inventory GUIDs, plan markers and 5032 snapshots.</summary>
public sealed class TotemTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-totem-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    public async Task InitializeAsync()
    {
        var (h, m, g) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o => { o.DataDir = _dir; o.GameDataDir = Path.Combine(RepoRoot(), "data"); o.HttpPort = h; o.MicroGatePort = m; o.GameGatePort = g; });
        await _app.StartAsync(); _http = $"http://127.0.0.1:{h}";
    }
    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
    private async Task<BotClient> InWorld(bool create = true)
    {
        var b = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await b.LoginAsync("alice", "pw"); await b.EnterGateAsync();
        long id = create ? (await b.CreateRoleAsync("Alice")).RoleBaseInfo.Id : b.Roles.Single().Id;
        await b.EnterWorldAsync(id); return b;
    }
    private static async Task<T> Next<T>(BotClient b, uint id, MessageParser<T> parser) where T : IMessage<T>
    { var e = await b.Inbox.WaitAsync(e => e.IsGame(id), Wait); return parser.ParseFrom(e.Body); }
    private async Task Gm(string command)
    { using var h = new HttpClient(); (await h.PostAsync($"{_http}/ro3emu/gm?account=alice", new StringContent(command))).EnsureSuccessStatusCode(); }
    private GameSession Session => _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice" && s.Role != null);
    private InvItem Item(long id) => Session.Items.Single(i => i.Guid == id);
    private async Task<long> Give(int entry)
    { await Gm($"!item {entry} 1"); return Session.Items.Last(i => i.Entry == entry).Guid; }
    private async Task<SetUpTotemResp> Equip(BotClient b, int slot, long guid)
    { await b.SendGameAsync(82362, new SetUpTotemReq { TotemPos = slot, TotemGuid = guid }); return await Next(b, 82362, SetUpTotemResp.Parser); }

    [ClientDataFact]
    public async Task PlansHaveSafeSlotsAndEquipSwapRemoveAndRelogPersistRealInstances()
    {
        long a, b;
        await using (var bot = await InWorld())
        {
            var initial = await Next(bot, 5032, DBTotemInfoIncrement.Parser);
            Assert.True(initial.SyncAllMark); Assert.Equal(5, initial.TotemPlans.Count);
            Assert.All(Session.Totems!.TotemPlans.Values, p => Assert.Equal(3, p.SetUpTotem.Count));
            await Gm("!lvl 60"); a = await Give(17300001); b = await Give(17300002);
            long hp = Session.EntityProperties.Single(p => p.Id == 101).Value;
            Assert.Equal(0, (await Equip(bot, 1, a)).Result);
            Assert.Equal(4, Item(a).Pack); Assert.Equal(0, Item(a).Grid); Assert.Equal(1, Item(a).Attrs![10001]);
            Assert.True(Session.EntityProperties.Single(p => p.Id == 101).Value > hp);
            await bot.SendGameAsync(82369, new TotemSwapReq { SwapFromGuid = a, SwapToGuid = b });
            Assert.Equal(0, (await Next(bot, 82369, TotemSwapResp.Parser)).Result);
            Assert.Equal(b, Session.Totems.TotemPlans[0].SetUpTotem[1]); Assert.Equal(0, Item(a).Attrs![10001]);
            Assert.Equal(0, (await Equip(bot, 2, a)).Result);
            Assert.Equal(0, (await Equip(bot, 3, a)).Result);
            Assert.Equal(0, Session.Totems.TotemPlans[0].SetUpTotem[2]);
            await bot.SendGameAsync(82368, new TotemChangeTotemNameReq { TargetIndex = 0, Name = "Boss" });
            Assert.Equal(0, (await Next(bot, 82368, TotemChangeTotemNameResp.Parser)).Result);
        }
        await using (var bot = await InWorld(false))
        {
            Assert.Equal(b, Session.Totems!.TotemPlans[0].SetUpTotem[1]);
            Assert.Equal(a, Session.Totems.TotemPlans[0].SetUpTotem[3]);
            Assert.Equal(3, Item(a).Attrs![10002]);
            await bot.SendGameAsync(82367, new TotemSwitchPlanReq { TargetIndex = 1 });
            Assert.Equal(0, (await Next(bot, 82367, TotemSwitchPlanResp.Parser)).Result);
            Assert.Equal(0, Item(a).Attrs![10001]);
            Assert.True(TotemService.IsReserved(Session, Item(a))); // An inactive saved plan still reserves it.
            Assert.Equal(0, (await Equip(bot, 1, b)).Result);
            Assert.Equal(b, (await Equip(bot, 1, 0)).OldTotemGuid);
        }
    }

    [ClientDataFact]
    public async Task AReturningLoginWithNothingToNormalizeWritesNoTotemState()
    {
        // Stage G: the totem normalization at login committed a transaction every time, a third of a role's
        // initialization in a login storm. With nothing to change, nothing is written.
        long a, role;
        await using (var bot = await InWorld())
        {
            await Next(bot, 5032, DBTotemInfoIncrement.Parser);
            await Gm("!lvl 60"); a = await Give(17300001);
            Assert.Equal(0, (await Equip(bot, 1, a)).Result);
            role = Session.Role!.Id;
        }
        // The logout's own saves are awaited first: only what the next login writes is compared.
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        var registry = _app.Services.GetRequiredService<SessionRegistry>();
        var deadline = DateTime.UtcNow + Wait;
        while (registry.All().Any(s => s.Account == "alice" && s.Role != null) && DateTime.UtcNow < deadline) await Task.Delay(20);
        var before = accounts.RoleVector(role, sqlOnly: true);
        await using (var bot = await InWorld(false))
        {
            await Next(bot, 5032, DBTotemInfoIncrement.Parser);
            Assert.Equal(1, Item(a).Attrs![10001]);
            Assert.Equal(a, Session.Totems!.TotemPlans[0].SetUpTotem[1]);
            var after = accounts.RoleVector(role, sqlOnly: true);
            foreach (var part in new[] { "totems", "totem_refine_epoch" })
                Assert.True(before.Revision(part) == after.Revision(part), $"{part}: {before.Revision(part)} -> {after.Revision(part)}");
        }
    }

    [ClientDataFact]
    public async Task InvalidOwnershipLockedSlotsDuplicateInputsAndSavedPlanConsumptionAreRefused()
    {
        await using var bot = await InWorld();
        Assert.Equal(ErrorCodes.FunctionLocked, (await Equip(bot, 1, 987654321)).Result);
        await Gm("!lvl 40"); long id = await Give(17300001);
        Assert.Equal(ErrorCodes.SlotSealed, (await Equip(bot, 2, id)).Result);
        Assert.Equal(ErrorCodes.TotemInvalid, (await Equip(bot, 1, 987654321)).Result);
        Assert.Equal(0, (await Equip(bot, 1, id)).Result);
        long book = await Give(10051001);
        await bot.SendGameAsync(82364, new RefineTotemSkillReq { TotemPos = 1, TotemGuid = id, SkillBookGuid = book });
        Assert.Equal(ErrorCodes.TotemInvalid, (await Next(bot, 82364, RefineTotemSkillResp.Parser)).Result);
        Assert.NotNull(Item(book)); // Blue totems cannot learn, even through a forged direct request.
        var exp = Session.Totems!.TotemTotalExp;
        await bot.SendGameAsync(82365, new UpgradeTotemSystemReq { TotemGuids = { id } });
        Assert.NotEqual(0, (await Next(bot, 82365, UpgradeTotemSystemResp.Parser)).Result);
        Assert.Equal(exp, Session.Totems.TotemTotalExp); Assert.NotNull(Item(id));
        await bot.SendGameAsync(82365, new UpgradeTotemSystemReq { TotemGuids = { id, id } });
        Assert.NotEqual(0, (await Next(bot, 82365, UpgradeTotemSystemResp.Parser)).Result);
        await bot.SendGameAsync(82367, new TotemSwitchPlanReq { TargetIndex = 999 });
        Assert.Equal(ErrorCodes.TotemPlan, (await Next(bot, 82367, TotemSwitchPlanResp.Parser)).Result);
        await bot.SendGameAsync(82368, new TotemChangeTotemNameReq { TargetIndex = 0, Name = "123456789" });
        Assert.NotEqual(0, (await Next(bot, 82368, TotemChangeTotemNameResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task AppraisalSkillLearningRefiningAndExperienceConsumeOnlyValidatedMaterials()
    {
        await using var bot = await InWorld(); await Gm("!lvl 60");
        long opaque = await Give(17500001);
        Assert.Equal(ErrorCodes.EquipNotAppraised, (await Equip(bot, 1, opaque)).Result);
        await bot.SendGameAsync(82370, new TotemAppraiseReq { ItemGuids = { opaque } });
        var appraised = await Next(bot, 82370, TotemAppraiseResp.Parser);
        Assert.Equal(0, appraised.Result); Assert.Single(appraised.AppraiseResult); Assert.Equal(1, Item(opaque).Attrs![3]);
        long simple = await Give(17400001);
        var data = _app.Services.GetRequiredService<GameData>();
        int bookEntry = (int)data.Table("ItemConfig").Values.First(row => row.TryGetProperty("_iUseEffect", out var e)
            && e.ValueKind == System.Text.Json.JsonValueKind.Array && e.EnumerateArray().Any(op => op.ValueKind == System.Text.Json.JsonValueKind.Array && op.GetArrayLength() >= 2 && GameData.Number(op[0]) == 206)).GetProperty("_iID").GetInt32();
        long book = await Give(bookEntry);
        await bot.SendGameAsync(82364, new RefineTotemSkillReq { TotemGuid = simple, SkillBookGuid = book });
        Assert.Equal(0, (await Next(bot, 82364, RefineTotemSkillResp.Parser)).Result);
        Assert.True(Item(simple).Attrs![240] > 0); Assert.DoesNotContain(Session.Items, i => i.Guid == book);
        await bot.SendGameAsync(82363, new RefineTotemAttrReq { TotemGuid = simple, AttrId = 101 });
        Assert.NotEqual(0, (await Next(bot, 82363, RefineTotemAttrResp.Parser)).Result); Assert.Equal(0, Item(simple).Attrs![270]);
        await Gm("!item 1256004 110");
        for (int n = 0; n < 10; n++)
        {
            await bot.SendGameAsync(82363, new RefineTotemAttrReq { TotemGuid = simple, AttrId = 101 });
            Assert.Equal(0, (await Next(bot, 82363, RefineTotemAttrResp.Parser)).Result);
        }
        await bot.SendGameAsync(82363, new RefineTotemAttrReq { TotemGuid = simple, AttrId = 101 });
        Assert.Equal(ErrorCodes.AttemptsExhausted, (await Next(bot, 82363, RefineTotemAttrResp.Parser)).Result);
        Assert.Equal(10, Session.Items.Where(i => i.Entry == 1256004).Sum(i => i.Stack));
        Assert.InRange(Item(simple).Attrs![230], Item(simple).Attrs![250], Item(simple).Attrs![260]);
        await Gm("!item 1256003 5");
        await bot.SendGameAsync(82365, new UpgradeTotemSystemReq { ConsumeItems = { [1256003] = 5 } });
        Assert.Equal(0, (await Next(bot, 82365, UpgradeTotemSystemResp.Parser)).Result);
        Assert.Equal(50, Session.Totems!.TotemTotalExp); Assert.DoesNotContain(Session.Items, i => i.Entry == 1256003);
        long cap = data.Table("TotemLvConfig").Values.Sum(row => GameData.Int(row, "_iExp"));
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        {
            Session.Totems.TotemTotalExp = cap - 5;
            _app.Services.GetRequiredService<RoleStore>().SaveTotems(Session.Role!.Id, Session.Totems.ToByteArray());
            return Task.CompletedTask;
        });
        await Gm("!item 1256003 1");
        await bot.SendGameAsync(82365, new UpgradeTotemSystemReq { ConsumeItems = { [1256003] = 1 } });
        var capped = await Next(bot, 82365, UpgradeTotemSystemResp.Parser);
        Assert.Equal(0, capped.Result); Assert.Equal(cap, Session.Totems.TotemTotalExp);
        Assert.Equal(1, capped.GainItems[1256002]); Assert.Equal(2, capped.GainItems[1256001]);
    }

    [ClientDataFact]
    public async Task CombinationReplacesTwoEligibleGuidsAndRejectsSelfOrLowQuality()
    {
        await using var bot = await InWorld(); await Gm("!lvl 60");
        long a = await Give(17400001), b = await Give(17400001), low = await Give(17300001);
        await bot.SendGameAsync(82366, new CombineTotemReq { LeftTotemGuid = a, RightTotemGuid = a });
        Assert.NotEqual(0, (await Next(bot, 82366, CombineTotemResp.Parser)).Result);
        await bot.SendGameAsync(82366, new CombineTotemReq { LeftTotemGuid = low, RightTotemGuid = b });
        Assert.NotEqual(0, (await Next(bot, 82366, CombineTotemResp.Parser)).Result);
        await bot.SendGameAsync(82366, new CombineTotemReq { LeftTotemGuid = a, RightTotemGuid = b });
        var merged = await Next(bot, 82366, CombineTotemResp.Parser);
        Assert.Equal(0, merged.Result); Assert.True(merged.NewTotemGuid > 0);
        Assert.DoesNotContain(Session.Items, i => i.Guid == a || i.Guid == b);
        Assert.Equal(17400001, Item(merged.NewTotemGuid).Entry); Assert.NotNull(Item(low));
        Assert.InRange(Item(merged.NewTotemGuid).Attrs![230], Item(merged.NewTotemGuid).Attrs![250], Item(merged.NewTotemGuid).Attrs![260]);
    }

    [ClientDataFact]
    public async Task InactiveTotemPlanPreventsMarketAndStallEscrow()
    {
        await using var bot = await InWorld();
        await Gm("!lvl 60");
        await Gm("!item 2 20000");
        await Gm("!warp 10100 156 191");
        var scene = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = scene.SceneEntry });
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        long guid = await Give(17500001);
        await bot.SendGameAsync(82370, new TotemAppraiseReq { ItemGuids = { guid } });
        Assert.Equal(0, (await Next(bot, 82370, TotemAppraiseResp.Parser)).Result);
        Assert.Contains(Item(guid).Attrs!, pair => pair.Key is >= 240 and <= 249 && pair.Value > 0);
        Assert.Equal(0, (await Equip(bot, 1, guid)).Result);
        await bot.SendGameAsync(82367, new TotemSwitchPlanReq { TargetIndex = 1 });
        Assert.Equal(0, (await Next(bot, 82367, TotemSwitchPlanResp.Parser)).Result);
        Assert.Equal(0, Item(guid).Attrs![10001]);
        Assert.True(TotemService.IsReserved(Session, Item(guid)));
        long currency = Session.Progress!.Currency[2];

        await bot.SendGameAsync(TradeService.PutOn,
            new TradePutOnItemReq { ItemGuid = guid, ItemPrice = 7500, CurrencyType = 1, Quantity = 1 });
        Assert.Equal(ErrorCodes.TradeRequest, (await Next(bot, TradeService.PutOn, TradePutOnItemResp.Parser)).Result);
        var stall = new PlayerStartStallsReq { StallsTitle = "Totem" };
        stall.Itembase.Add(new StallsPutItem { Itemid = guid, Itemnum = 1, Price = 7500, Pricetype = 1 });
        await bot.SendGameAsync(StallsService.Start, stall);
        Assert.Equal(ErrorCodes.TradeRequest, (await Next(bot, StallsService.Start, PlayerStartStallsRsp.Parser)).Result);
        Assert.Equal(currency, Session.Progress.Currency[2]);
        Assert.Equal(guid, Session.Totems!.TotemPlans[0].SetUpTotem[1]);
        Assert.Contains(_app.Services.GetRequiredService<ItemStore>().Load(Session.Role!.Id), item => item.Guid == guid);

        // The same market gate still accepts a developed, unreserved copy.
        long free = await Give(17500001);
        await bot.SendGameAsync(82370, new TotemAppraiseReq { ItemGuids = { free } });
        Assert.Equal(0, (await Next(bot, 82370, TotemAppraiseResp.Parser)).Result);
        await bot.SendGameAsync(TradeService.PutOn,
            new TradePutOnItemReq { ItemGuid = free, ItemPrice = 7500, CurrencyType = 1, Quantity = 1 });
        Assert.Equal(0, (await Next(bot, TradeService.PutOn, TradePutOnItemResp.Parser)).Result);
        Assert.DoesNotContain(Session.Items, item => item.Guid == free);
        Assert.Contains(Session.Items, item => item.Guid == guid);
    }

    [ClientDataFact]
    public async Task CartPreservesTotemStorageAndRejectsSavedPlanTransfers()
    {
        long guid;
        async Task EnableCart() => await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            _app.Services.GetRequiredService<OwnerRegistry>().RunAsync("test/enable-totem-cart",
                [ReservationKey.Role(Session.Role!.Id)], () =>
                {
                    // GM changes queue owned refreshes; fixture writes must wait for their commit and apply.
                    Session.Progress!.Skills[16003] = 1; // Cart Use (row 1600301); same learned-cart fixture as WorldRoleOperationsTests.
                    _app.Services.GetRequiredService<RoleStore>().SaveProgress(Session.Role!.Id, Session.Progress);
                    return Task.CompletedTask;
                }));
        async Task<int> Transfer(BotClient bot, CareerSpecialTransferItemsType direction)
        {
            var request = new CareerSpecialTransferItemsReq { TransType = direction };
            request.ItemGuidCountMap[guid] = 1;
            await bot.SendGameAsync(CharacterService.CareerSpecialTransfer, request);
            return (await Next(bot, CharacterService.CareerSpecialTransfer, CareerSpecialTransferItemsRsp.Parser)).Result;
        }
        await using (var bot = await InWorld())
        {
            await Gm("!lvl 60");
            await Gm("!job 6100");
            await EnableCart();
            guid = await Give(17300001);
            Assert.Equal(0, (await Equip(bot, 1, guid)).Result);
            Assert.Equal(ErrorCodes.ItemLocked, await Transfer(bot, CareerSpecialTransferItemsType.ToCart));
            await bot.SendGameAsync(82367, new TotemSwitchPlanReq { TargetIndex = 1 });
            Assert.Equal(0, (await Next(bot, 82367, TotemSwitchPlanResp.Parser)).Result);
            Assert.Equal(ErrorCodes.ItemLocked, await Transfer(bot, CareerSpecialTransferItemsType.ToCart));
            Assert.Equal(4, Item(guid).Pack);
            guid = await Give(17300002);
            Assert.Equal(0, await Transfer(bot, CareerSpecialTransferItemsType.ToCart));
            Assert.Equal(100, Item(guid).Pack);
            Assert.Equal(ErrorCodes.TotemInvalid, (await Equip(bot, 1, guid)).Result);
        }
        await using (var bot = await InWorld(false))
        {
            Assert.Equal(100, Item(guid).Pack);
            Assert.Equal(ErrorCodes.TotemInvalid, (await Equip(bot, 1, guid)).Result);
            await EnableCart();
            Assert.Equal(0, await Transfer(bot, CareerSpecialTransferItemsType.ToNormal));
            Assert.Equal(4, Item(guid).Pack);
            Assert.Equal(4, _app.Services.GetRequiredService<ItemStore>().Load(Session.Role!.Id).Single(item => item.Guid == guid).Pack);
            Assert.Equal(0, (await Equip(bot, 1, guid)).Result);
        }
    }

    private static string RepoRoot()
    { for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent) if (File.Exists(Path.Combine(d.FullName, "Ro3Emu.slnx"))) return d.FullName; throw new DirectoryNotFoundException(); }
}
