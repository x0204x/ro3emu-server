using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>5010/5018 and 335xx over real free-port gates, with persisted inventory/profession/To-Do verification.</summary>
public sealed class LifeJobTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-life-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(12);

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
    private static string RepoRoot()
    {
        var p = new DirectoryInfo(AppContext.BaseDirectory);
        while (p != null && !File.Exists(Path.Combine(p.FullName, "Ro3Emu.slnx"))) p = p.Parent;
        return p?.FullName ?? throw new DirectoryNotFoundException("repository");
    }
    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var b = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await b.LoginAsync(name.ToLowerInvariant(), "pw"); await b.EnterGateAsync();
        long id = create ? (await b.CreateRoleAsync(name)).RoleBaseInfo.Id : b.Roles.Single().Id;
        await b.EnterWorldAsync(id); return b;
    }
    private static async Task<T> Next<T>(BotClient b, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
    {
        var e = await b.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }
    private async Task Gm(string account, string line)
    {
        using var c = new HttpClient(); var r = await c.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(line)); r.EnsureSuccessStatusCode();
    }
    private Task<T> OnLoop<T>(string account, Func<GameSession, T> fn) => _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        Task.FromResult(fn(_app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == account))));
    private static async Task<int> Switch(BotClient b, int job, int slot)
    {
        await b.SendGameAsync(LifeJobService.SwitchLife, new SwitchLifeJobReq { NewLifejobId = (LifeJobType)job, OpJobType = slot });
        return (await Next(b, LifeJobService.SwitchLife, SwitchLifeJobResp.Parser)).Result;
    }
    /// <summary>Every learn consumes the workshop certificate (LifeJobConfig._iChangeConsume 1291000 x1).</summary>
    private async Task<int> Learn(BotClient b, string account, int job, int slot)
    {
        await Gm(account, "!item 1291000 1");
        return await Switch(b, job, slot);
    }
    private async Task Cooker(BotClient b, string account, int batches = 1)
    {
        await Gm(account, "!lvl 34"); Assert.Equal(0, await Learn(b, account, 2, 2));
        await Gm(account, "!item 2 10000");
        await b.SendGameAsync(LifeJobService.StudyRecipe, new StudyRecipeReq { FormulaID = 20001 });
        Assert.Equal(0, (await Next(b, LifeJobService.StudyRecipe, StudyRecipeResp.Parser)).Result);
        foreach (int id in new[] { 1293022, 1293050, 1294010 }) await Gm(account, $"!item {id} {batches * 8}");
    }
    private static async Task<StartForgeResp> Craft(BotClient b, int count)
    {
        await b.SendGameAsync(LifeJobService.ForgeTime, new GetForgeCostTimeReq { ForgeID = 20001 });
        var time = await Next(b, LifeJobService.ForgeTime, GetForgeCostTimeResp.Parser);
        Assert.Equal(0, time.Result); Assert.True(time.CostTime > 0);
        await Task.Delay(time.CostTime + 100);
        await b.SendGameAsync(LifeJobService.StartForge, new StartForgeReq { ItemUniqueId = 20001, ItemNum = count });
        return await Next(b, LifeJobService.StartForge, StartForgeResp.Parser);
    }

    [ClientDataFact]
    public async Task LifeRequestsAtRoleScreenCannotMutatePreviousCharacter()
    {
        await using var b = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await b.LoginAsync("alice", "pw"); await b.EnterGateAsync();
        await StageGate.AssertUnansweredAsync(b, LifeJobService.SwitchLife, new SwitchLifeJobReq { NewLifejobId = (LifeJobType)1, OpJobType = 1 });
        await StageGate.AssertUnansweredAsync(b, LifeJobService.HealthAll, new GetHealthCostAllReq(), LifeJobService.HealthNotify);
        long id = (await b.CreateRoleAsync("Alice")).RoleBaseInfo.Id;
        await b.EnterWorldAsync(id);
        Assert.NotEqual(0, await Switch(b, 1, 1)); // no certificate
        Assert.Equal(0, await Learn(b, "alice", 1, 1));
        await Gm("alice", "!lvl 34");
        await Gm("alice", "!item 1291002 1");
        await Gm("alice", "!item 1001801 1");
        var consumables = await OnLoop("alice", s => s.Items.Where(i => i.Entry is 1291002 or 1001801).Select(i => i.Guid).ToArray());
        var roles = _app.Services.GetRequiredService<RoleStore>();
        byte[] before = roles.LoadLifeJob(id)!;
        await b.SwitchRoleAsync();
        await StageGate.AssertUnansweredAsync(b, LifeJobService.SwitchLife, new SwitchLifeJobReq { NewLifejobId = (LifeJobType)5, OpJobType = 2 });
        await StageGate.AssertUnansweredAsync(b, LifeJobService.StudyRecipe, new StudyRecipeReq { FormulaID = 20001 });
        foreach (long guid in consumables)
            await StageGate.AssertUnansweredAsync(b, CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
        var savedItems = _app.Services.GetRequiredService<ItemStore>().Load(id).Where(i => consumables.Contains(i.Guid)).ToArray();
        Assert.Equal(2, savedItems.Length); Assert.All(savedItems, i => Assert.Equal(1, i.Stack));
        Assert.Equal(before, roles.LoadLifeJob(id));
        await b.EnterWorldAsync(id);
        Assert.Equal(1, await OnLoop("alice", s => s.LifeJob!.ActiveLifejob[1]));
        Assert.Single(await OnLoop("alice", s => s.LifeJob!.ActiveLifejob));
    }

    [ClientDataFact]
    public async Task LoginHasRealVigorAndSafeEmptyUnchosenProfessionState()
    {
        await using var b = await InWorld("Alice");
        var life = await Next(b, LifeJobService.LifeSync, DBLifeJobInfoIncrement.Parser);
        Assert.True(life.SyncAllMark); Assert.Equal(1, life.Version);
        Assert.All(life.ActiveLifejob.Values, v => Assert.True(v.Del));
        // Every profession has a level-0 row with its title info: the Learn tip reads LifeJobTitleInfo[jobId] unguarded.
        Assert.Equal(_app.Services.GetRequiredService<Ro3Emu.Server.Data.GameData>().Table("LifeJobConfig").Count, life.LifeJobInfoMap.Count);
        Assert.All(life.LifeJobInfoMap.Values, v => { Assert.Equal(0, v.All.JobLevel); Assert.NotNull(v.All.TitleInfo); });
        var energy = await Next(b, LifeJobService.RestrictSync, DBRestrictInfoIncrement.Parser);
        Assert.Equal(500, energy.RestrictCount[1004].All.Count); Assert.Equal(1000, energy.RestrictCount[1004].All.LimitCount);
        var health = await Next(b, LifeJobService.HealthNotify, GetHealthCostNtf.Parser);
        Assert.Equal(20, health.CollectHealthCost[100100]); Assert.Equal(20, health.ForgeHealthCost[20001]);
        await b.SendGameAsync(LifeJobService.HealthOne, new GetHealthCostReq { Id = 20001, Type = 0 });
        var one = await Next(b, LifeJobService.HealthNotify, GetHealthCostNtf.Parser);
        Assert.NotEmpty(one.CollectHealthCost); Assert.NotEmpty(one.ForgeHealthCost);
        Assert.Equal(500, await OnLoop("alice", s => s.Restrictions!.RestrictCount[1004].Count));
        Assert.Empty(await OnLoop("alice", s => s.LifeJob!.ActiveLifejob));
        Assert.Equal(0, await OnLoop("alice", s => s.Progress!.Currency.GetValueOrDefault(5)));
        await b.SendGameAsync(LifeJobService.CheckCollect, new GetHealthCostAllReq()); // msgtable request has no fields
        Assert.Equal(ErrorCodes.Parameter, (await Next(b, LifeJobService.CheckCollect, CheckCanCollectNtf.Parser)).Result);
    }

    [ClientDataFact]
    public async Task LearningCraftingAndReconnectPreserveExactPaidState()
    {
        long role;
        await using (var b = await InWorld("Alice"))
        {
            role = (long)b.Self!.Uuid; await Cooker(b, "alice", 2);
            var reply = await Craft(b, 1);
            Assert.Equal(0, reply.Result); Assert.Equal(1, reply.ForgeNum);
            Assert.True(reply.OutputItemMap[1].Items.Where(i => i.ItemEntry is 1001101 or 1001201).Sum(i => i.Stack) >= 2);
            Assert.All(reply.OutputItemMap[1].Items, i => { Assert.NotEqual(112001, i.ItemEntry); Assert.True(i.ItemGuid > 0); });
            var state = await OnLoop("alice", s => (s.LifeJob!.Clone(), s.Restrictions!.Clone(), s.Items.Where(i => i.Entry == 1293022).Sum(i => i.Stack)));
            Assert.Equal(2, state.Item1.LifeJobInfoMap[2].JobLevel); Assert.Equal(149, state.Item1.LifeJobInfoMap[2].JobExp);
            Assert.Equal(480, state.Item2.RestrictCount[1004].Count); Assert.Equal(8, state.Item3);
            await b.SendGameAsync(LifeJobService.StartForge, new StartForgeReq { ItemUniqueId = 20001, ItemNum = 1 });
            Assert.NotEqual(0, (await Next(b, LifeJobService.StartForge, StartForgeResp.Parser)).Result);
            Assert.Equal(480, await OnLoop("alice", s => s.Restrictions!.RestrictCount[1004].Count));
        }
        await using var again = await InWorld("Alice", false);
        var life = await Next(again, LifeJobService.LifeSync, DBLifeJobInfoIncrement.Parser);
        Assert.True(life.SyncAllMark); Assert.Equal(2, life.ActiveLifejob[2].All);
        var job = life.LifeJobInfoMap[2].All;
        Assert.True(job.BuildInfo.RecipeList[20001]); Assert.NotNull(job.TitleInfo); Assert.NotNull(job.GatherInfo);
        Assert.Equal(149, job.JobExp);
        var persisted = DBRestrictInfo.Parser.ParseFrom(_app.Services.GetRequiredService<RoleStore>().LoadLifeRestrict(role)!);
        Assert.Equal(480, persisted.RestrictCount[1004].Count);
    }

    [ClientDataFact]
    public async Task RefusalsDoNotConsumeIngredientsVigorOrUnlockCosts()
    {
        await using var b = await InWorld("Alice");
        await b.SendGameAsync(LifeJobService.StudyRecipe, new StudyRecipeReq { FormulaID = 20001 });
        Assert.Equal(ErrorCodes.LifeWrongJob, (await Next(b, LifeJobService.StudyRecipe, StudyRecipeResp.Parser)).Result);
        Assert.Equal(ErrorCodes.LifeInvalidJob, await Switch(b, 999, 1));
        await Cooker(b, "alice");
        long money = await OnLoop("alice", s => s.Progress!.Currency.GetValueOrDefault(2));
        await b.SendGameAsync(LifeJobService.StudyRecipe, new StudyRecipeReq { FormulaID = 20001 });
        Assert.Equal(ErrorCodes.LifeAlreadyLearned, (await Next(b, LifeJobService.StudyRecipe, StudyRecipeResp.Parser)).Result);
        Assert.Equal(money, await OnLoop("alice", s => s.Progress!.Currency.GetValueOrDefault(2)));
        await b.SendGameAsync(LifeJobService.StartForge, new StartForgeReq { ItemUniqueId = 20001, ItemNum = -1 });
        Assert.Equal(ErrorCodes.Parameter, (await Next(b, LifeJobService.StartForge, StartForgeResp.Parser)).Result);
        await b.SendGameAsync(LifeJobService.ForgeTime, new GetForgeCostTimeReq { ForgeID = 20001 });
        Assert.Equal(0, (await Next(b, LifeJobService.ForgeTime, GetForgeCostTimeResp.Parser)).Result);
        await b.SendGameAsync(LifeJobService.StartForge, new StartForgeReq { ItemUniqueId = 20001, ItemNum = 1 });
        Assert.Equal(ErrorCodes.Parameter, (await Next(b, LifeJobService.StartForge, StartForgeResp.Parser)).Result);
        var failed = await Craft(b, 2); Assert.NotEqual(0, failed.Result);
        Assert.Equal(500, await OnLoop("alice", s => s.Restrictions!.RestrictCount[1004].Count));
        Assert.Equal(8, await OnLoop("alice", s => s.Items.Where(i => i.Entry == 1293022).Sum(i => i.Stack)));
    }

    [ClientDataFact]
    public async Task SpendingOneHundredVigorCompletesTheLifeSkillsTodoOnce()
    {
        await using var b = await InWorld("Alice"); await Cooker(b, "alice", 6);
        await Gm("alice", "!quest done 10105090"); // Function 25's actual To-Do prerequisite, as in DailyTests.
        Assert.True(await OnLoop("alice", s => s.Tasks!.Infos.ContainsKey(500070)));
        Assert.Equal(0, (await Craft(b, 5)).Result);
        var done = await Next(b, DailyActivityService.ActivitySync, DBDailyActivityInfoIncrement.Parser,
            m => m.ActivityData.TryGetValue(107, out var row) && (row.Inc?.TaskCompleteTimes == 1 || row.All?.TaskCompleteTimes == 1));
        Assert.NotNull(done);
        Assert.Equal(100, await OnLoop("alice", s => LifeJobService.DailyEnergyUsed(s, 1004)));
        Assert.Equal(20, await OnLoop("alice", s => s.Progress!.Currency.GetValueOrDefault(5)));
        Assert.Equal(0, (await Craft(b, 1)).Result);
        Assert.Equal(1, await OnLoop("alice", s => s.DailyRows!.ActivityData[107].TaskCompleteTimes));
        Assert.Equal(20, await OnLoop("alice", s => s.Progress!.Currency.GetValueOrDefault(5)));
    }

    [ClientDataFact]
    public async Task VigorCanBeExhaustedAndNeverRefillsOnReconnect()
    {
        await using (var b = await InWorld("Alice"))
        {
            await Cooker(b, "alice", 26); Assert.Equal(0, (await Craft(b, 25)).Result);
            await b.SendGameAsync(LifeJobService.ForgeTime, new GetForgeCostTimeReq { ForgeID = 20001 });
            Assert.Equal(ErrorCodes.LifeVigor, (await Next(b, LifeJobService.ForgeTime, GetForgeCostTimeResp.Parser)).Result);
        }
        await using var again = await InWorld("Alice", false);
        var energy = await Next(again, LifeJobService.RestrictSync, DBRestrictInfoIncrement.Parser);
        Assert.Equal(0, energy.RestrictCount[1004].All.Count); Assert.Equal(500, energy.RestrictCount[1004].All.HistoryUseCount);
    }

    [ClientDataFact]
    public async Task VigorPotionUsesRestrictionAndExtraCapAndPersistsConsumption()
    {
        await using (var b = await InWorld("Alice"))
        {
            await Gm("alice", "!item 1291002 6");
            long guid = await OnLoop("alice", s => s.Items.Single(i => i.Entry == 1291002).Guid);
            await b.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
            Assert.Equal(ErrorCodes.LifeRequirements, (await Next(b, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
            await Gm("alice", "!lvl 34");
            await b.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 5 });
            Assert.Equal(ErrorCodes.LifeVigorOverflow, (await Next(b, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
            Assert.Equal(6, await OnLoop("alice", s => s.Items.Single(i => i.Entry == 1291002).Stack));
            await b.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 4 });
            Assert.Equal(0, (await Next(b, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
            Assert.Equal(4500, await OnLoop("alice", s => s.Restrictions!.RestrictCount[1004].Count));
            Assert.Equal(1, await OnLoop("alice", s => s.Restrictions!.RestrictCount[1005].Count));
            Assert.Equal(2, await OnLoop("alice", s => s.Items.Single(i => i.Entry == 1291002).Stack));
        }
        await using var again = await InWorld("Alice", false);
        var energy = await Next(again, LifeJobService.RestrictSync, DBRestrictInfoIncrement.Parser);
        Assert.Equal(4500, energy.RestrictCount[1004].All.Count);
        Assert.Equal(1, energy.RestrictCount[1005].All.Count);
        Assert.Equal(0, await OnLoop("alice", s => LifeJobService.DailyEnergyUsed(s, 1004)));
        Assert.Equal(2, await OnLoop("alice", s => s.Items.Single(i => i.Entry == 1291002).Stack));
    }

    [ClientDataFact]
    public async Task MagicFoodIncreasesStatsPersistsAndRejectsOverLimitWithoutEating()
    {
        long attack;
        await using (var b = await InWorld("Alice"))
        {
            await Gm("alice", "!lvl 34"); await Gm("alice", "!item 1001801 51");
            long guid = await OnLoop("alice", s => s.Items.Single(i => i.Entry == 1001801).Guid);
            attack = await OnLoop("alice", s => s.EntityProperties.Single(p => p.Id == 103).Value);
            await b.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 50 });
            Assert.Equal(0, (await Next(b, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
            Assert.Equal(50, await OnLoop("alice", s => s.LifeJob!.MagicFoodTimes[1001801]));
            Assert.Equal(attack + 200, await OnLoop("alice", s => s.EntityProperties.Single(p => p.Id == 103).Value));
            await Task.Delay(3100);
            await b.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
            Assert.Equal(ErrorCodes.AttemptsExhausted, (await Next(b, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
            Assert.Equal(1, await OnLoop("alice", s => s.Items.Single(i => i.Entry == 1001801).Stack));
        }
        await using var again = await InWorld("Alice", false);
        Assert.Equal(attack + 200, again.Self!.Property.Single(p => p.Idx == 103).Data);
        Assert.Equal(50, (await Next(again, LifeJobService.LifeSync, DBLifeJobInfoIncrement.Parser)).MagicFoodTimes[1001801].All);
    }

    [ClientDataFact]
    public async Task ProfessionSwitchKeepsProgressAndChecksActiveLifeCondition()
    {
        await using var b = await InWorld("Alice"); Assert.Equal(0, await Learn(b, "alice", 1, 1));
        await Next(b, LifeJobService.LifeSync, DBLifeJobInfoIncrement.Parser, m => m.LifeJobInfoMap.ContainsKey(1));
        Assert.Equal(1, await OnLoop("alice", s => s.LifeJob!.LifeJobInfoMap[1].JobLevel));
        Assert.True(await OnLoop("alice", s => s.LifeJob!.LifeJobInfoMap[1].GatherInfo.ItemDetails.ContainsKey(100100)));
        Assert.Empty(await OnLoop("alice", s => s.LifeJob!.LifeJobInfoMap[1].BuildInfo.RecipeList));
        Assert.True(await OnLoop("alice", s => _app.Services.GetRequiredService<FunctionService>().ConditionMet(s, 7000079)));
        Assert.NotEqual(0, await Switch(b, 5, 1));
        await Gm("alice", "!item 1291000 1"); Assert.Equal(0, await Switch(b, 5, 1));
        Assert.False(await OnLoop("alice", s => _app.Services.GetRequiredService<FunctionService>().ConditionMet(s, 7000079)));
        Assert.Equal(1, await OnLoop("alice", s => s.LifeJob!.LifeJobInfoMap[1].JobLevel));
    }

    /// <summary>
    /// #bug-reports "Level 3 mining does not grant access to level 2 resources": the client's Unlock button only walks to
    /// the gathering area, so Lv. 2 ore (100200, condition 43000000: Mining 3) unlocks with the level, at the next gather.
    /// </summary>
    [ClientDataFact]
    public async Task LevelTwoOreUnlocksWithMiningLevelThree()
    {
        await using var b = await InWorld("Alice"); Assert.Equal(0, await Learn(b, "alice", 1, 1));
        var life = _app.Services.GetRequiredService<LifeJobService>();
        Assert.Equal(ErrorCodes.LifeRequirements, await OnLoop("alice", s => life.CheckGather(s, 100200))); // Mining 1
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
        {
            var s = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
            using var gate = await CharacterService.Lock(s);
            s.LifeJob!.LifeJobInfoMap[1].JobLevel = 3;
            Assert.Equal(0, await life.CompleteGatherAsync(s, 100100, sceneEntry: 10522, pointId: 312));
        });
        Assert.Equal(0, await OnLoop("alice", s => life.CheckGather(s, 100200)));
        Assert.Equal(ErrorCodes.LifeRequirements, await OnLoop("alice", s => life.CheckGather(s, 100300))); // Mining 5
        var saved = DBLifeJobInfo.Parser.ParseFrom(_app.Services.GetRequiredService<RoleStore>().LoadLifeJob((long)b.Self!.Uuid)!);
        Assert.True(saved.LifeJobInfoMap[1].GatherInfo.ItemDetails.ContainsKey(100200));
    }

    [ClientDataTheory]
    [InlineData(1001801)]
    [InlineData(1291002)]
    public async Task SpecializedShortcutConsumesLastItemOnceAndKeepsReplyAndCooldownContract(int entry)
    {
        long role;
        long itemCd = 0;
        await using (var b = await InWorld("Alice"))
        {
            await Gm("alice", "!lvl 34"); await Gm("alice", $"!item {entry} 1");
            role = (long)b.Self!.Uuid;
            long guid = await OnLoop("alice", s => s.Items.Single(i => i.Entry == entry).Guid);
            await b.SendGameAsync(CharacterService.ShortcutBarSave, new ShortcutBarSaveReq { SlotIndex = 0, ItemGuid = guid });
            Assert.Equal(0, (await Next(b, CharacterService.ShortcutBarSave, ShortcutBarSaveRes.Parser)).Result);
            await b.SendGameAsync(CharacterService.QuickUseSave, new QuickUseSaveReq { LieQuickUse = 0, EntryItem = entry });
            Assert.Equal(0, (await Next(b, CharacterService.QuickUseSave, QuickUseSaveRes.Parser)).Result);
            int start = b.Inbox.Snapshot().Count;
            await b.SendGameAsync(CharacterService.ShortcutBarUse, new ShortcutBarUseReq { SlotIndex = 0 });
            Assert.Equal(0, (await Next(b, CharacterService.ShortcutBarUse, ShortcutBarUseRes.Parser)).Result);
            Assert.DoesNotContain(b.Inbox.Snapshot().Skip(start), e => e.IsGame(CharacterService.ItemUse));
            Assert.DoesNotContain(await OnLoop("alice", s => s.Items.Select(i => i.Guid).ToArray()), id => id == guid);
            Assert.DoesNotContain(_app.Services.GetRequiredService<ItemStore>().Load(role), i => i.Guid == guid);
            if (entry == 1001801)
            {
                var cd = Assert.Single((await Next(b, CharacterService.ItemsCdNotify, ItemsCDNotify.Parser)).Items);
                Assert.Equal(entry, cd.ItemId); Assert.True(cd.ItemCd > 0); itemCd = cd.ItemCd;
                Assert.Equal(1, await OnLoop("alice", s => s.LifeJob!.MagicFoodTimes[entry]));
                var saved = DBItemsInfo.Parser.ParseFrom(_app.Services.GetRequiredService<RoleStore>().LoadBagState(role)!);
                Assert.Equal(itemCd, saved.ItemCds.Data[entry]);
            }
            else
            {
                Assert.Equal(1500, await OnLoop("alice", s => s.Restrictions!.RestrictCount[1004].Count));
                Assert.Equal(4, await OnLoop("alice", s => s.Restrictions!.RestrictCount[1005].Count));
            }
            await b.SendGameAsync(CharacterService.ShortcutBarUse, new ShortcutBarUseReq { SlotIndex = 0 });
            Assert.Equal(ErrorCodes.ItemMissing, (await Next(b, CharacterService.ShortcutBarUse, ShortcutBarUseRes.Parser)).Result);
            Assert.DoesNotContain(b.Inbox.Snapshot().Skip(start), e => e.IsGame(CharacterService.ItemUse));
        }
        await using var again = await InWorld("Alice", false);
        Assert.DoesNotContain(await OnLoop("alice", s => s.Items.Select(i => i.Entry).ToArray()), id => id == entry);
        if (entry == 1001801)
        {
            Assert.Equal(1, await OnLoop("alice", s => s.LifeJob!.MagicFoodTimes[entry]));
            Assert.Equal(itemCd, DBItemsInfo.Parser.ParseFrom(_app.Services.GetRequiredService<RoleStore>().LoadBagState(role)!).ItemCds.Data[entry]);
        }
        else Assert.Equal(1500, await OnLoop("alice", s => s.Restrictions!.RestrictCount[1004].Count));
    }

    [ClientDataTheory]
    [InlineData(1001801)]
    [InlineData(1291002)]
    public async Task SpecializedItemCooldownSaveFailureRollsBackConsumptionAndLifeState(int entry)
    {
        await using var b = await InWorld("Alice");
        await Gm("alice", "!lvl 34"); await Gm("alice", $"!item {entry} 1");
        long role = (long)b.Self!.Uuid;
        var roles = _app.Services.GetRequiredService<RoleStore>();
        long guid = await OnLoop("alice", s => s.Items.Single(i => i.Entry == entry).Guid);
        var before = await OnLoop("alice", s => (Life: s.LifeJob!.ToByteArray(), Restrict: s.Restrictions!.ToByteArray(), Bag: s.BagState.Info.ToByteArray()));
        var savedBag = roles.LoadBagState(role);
        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(),
            "CREATE TRIGGER reject_item_cooldown BEFORE UPDATE OF bag_state ON role BEGIN SELECT RAISE(ABORT,'cooldown rollback test'); END");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
        {
            var s = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
            await _app.Services.GetRequiredService<CharacterService>().UseItemAsync(s, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
        }));
        var after = await OnLoop("alice", s => (Life: s.LifeJob!.ToByteArray(), Restrict: s.Restrictions!.ToByteArray(), Bag: s.BagState.Info.ToByteArray()));
        Assert.Equal(before.Life, after.Life); Assert.Equal(before.Restrict, after.Restrict); Assert.Equal(before.Bag, after.Bag);
        Assert.Equal(1, await OnLoop("alice", s => s.Items.Single(i => i.Guid == guid).Stack));
        Assert.Equal(0, await OnLoop("alice", s => s.ItemCds.GetValueOrDefault(entry)));
        Assert.Equal(1, _app.Services.GetRequiredService<ItemStore>().Load(role).Single(i => i.Guid == guid).Stack);
        Assert.Equal(before.Life, roles.LoadLifeJob(role)); Assert.Equal(before.Restrict, roles.LoadLifeRestrict(role));
        Assert.Equal(savedBag, roles.LoadBagState(role));
        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(), "DROP TRIGGER reject_item_cooldown");
        await b.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
        Assert.Equal(0, (await Next(b, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        Assert.DoesNotContain(_app.Services.GetRequiredService<ItemStore>().Load(role), i => i.Guid == guid);
    }

    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SpecializedCooldownIsPublishedBeforePostCommitHookCanFail(int failOnCall)
    {
        await using var b = await InWorld("Alice");
        await Gm("alice", "!lvl 34"); await Gm("alice", "!item 1001801 2");
        long role = (long)b.Self!.Uuid;
        long guid = await OnLoop("alice", s => s.Items.Single(i => i.Entry == 1001801).Guid);
        var character = _app.Services.GetRequiredService<CharacterService>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => loop.InvokeAsync(async () =>
        {
            var s = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
            var itemUseWork = loop.CurrentWork ?? throw new InvalidOperationException("Item-use test operation was not admitted.");
            int calls = 0;
            void FailAfterCommit(GameSession changed)
            {
                // Exchange's hook is first; LifeJob's post-commit ItemsChanged hook is second.
                if (ReferenceEquals(changed, s) && ReferenceEquals(loop.CurrentWork, itemUseWork)
                    && ++calls == failOnCall) throw new InvalidOperationException("post-commit hook test");
            }
            character.StateChanged.Add(FailAfterCommit);
            try { await character.UseItemAsync(s, new ItemUseReq { ItemGuid = guid, ItemCount = 1 }); }
            finally { character.StateChanged.Remove(FailAfterCommit); }
        }));
        Assert.Equal("post-commit hook test", failure.Message);
        var state = await OnLoop("alice", s => (Cd: s.ItemCds.GetValueOrDefault(1001801), BagCd: s.BagState.Info.ItemCds?.Data.GetValueOrDefault(1001801) ?? 0,
            Stack: s.Items.Single(i => i.Guid == guid).Stack, Food: s.LifeJob!.MagicFoodTimes[1001801]));
        Assert.True(state.Cd > 0); Assert.Equal(state.Cd, state.BagCd);
        Assert.Equal(1, state.Stack); Assert.Equal(1, state.Food);
        var roles = _app.Services.GetRequiredService<RoleStore>();
        Assert.Equal(state.Cd, DBItemsInfo.Parser.ParseFrom(roles.LoadBagState(role)!).ItemCds.Data[1001801]);
        Assert.Equal(state.Food, DBLifeJobInfo.Parser.ParseFrom(roles.LoadLifeJob(role)!).MagicFoodTimes[1001801]);
        Assert.Equal(state.Stack, _app.Services.GetRequiredService<ItemStore>().Load(role).Single(i => i.Guid == guid).Stack);
    }

    [ClientDataFact]
    public async Task FailedLifeCommitRollsBackItemsVigorExperienceAndPointCounter()
    {
        await using var b = await InWorld("Alice"); Assert.Equal(0, await Learn(b, "alice", 1, 1));
        var roles = _app.Services.GetRequiredService<RoleStore>();
        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(),
            "CREATE TRIGGER reject_life BEFORE UPDATE OF lifejob ON role BEGIN SELECT RAISE(ABORT,'life rollback test'); END");
        var before = await OnLoop("alice", s => (s.LifeJob!.ToByteArray(), s.Items.Select(i => (i.Guid, i.Entry, i.Stack)).ToArray()));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
        {
            var s = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
            using var gate = await CharacterService.Lock(s);
            await _app.Services.GetRequiredService<LifeJobService>().CompleteGatherAsync(s, 100100, sceneEntry: 10522, pointId: 312);
        }));
        var after = await OnLoop("alice", s => (s.LifeJob!.ToByteArray(), s.Items.Select(i => (i.Guid, i.Entry, i.Stack)).ToArray(), s.Restrictions!.RestrictCount[1004].Count));
        Assert.Equal(before.Item1, after.Item1); Assert.Equal(before.Item2, after.Item2); Assert.Equal(500, after.Item3);
        long role = (long)b.Self!.Uuid;
        Assert.Equal(before.Item1, roles.LoadLifeJob(role));
        Assert.Equal(500, DBRestrictInfo.Parser.ParseFrom(roles.LoadLifeRestrict(role)!).RestrictCount[1004].Count);
    }
}
