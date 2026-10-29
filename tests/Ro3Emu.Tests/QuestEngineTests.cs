using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Fast quest protocol/regression checks. Combat callback tests simulate committed server deaths;
/// QuestWalkTests remains the separate Slow test of actual fighting through the chapter.</summary>
public sealed class QuestEngineTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-quests-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game;
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

    private async Task<BotClient> InWorld(string name = "QuestAlice", bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string line, string account = "questalice")
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(line))).EnsureSuccessStatusCode();
    }

    private GameSession Alice => Service<SessionRegistry>().All().Single(s => s.Role?.Name == "QuestAlice");
    private Task Change(Func<GameSession, Task> change) => Service<GameLoop>().InvokeAsync(() => change(Alice));
    private Task<T> Read<T>(Func<GameSession, T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read(Alice)));
    private Task<TaskInfo> State(long id) => Read(s => s.Tasks!.Infos[id].Clone());
    private Task Refresh() => Change(s => Service<TaskService>().RefreshAsync(s));
    private async Task Accept(params long[] ids) { foreach (long id in ids) await Gm($"!quest accept {id}"); }

    private long OwnedQuestSourceCount(long role, long task)
    {
        using var db = Service<AccountStore>().Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM operation_source s JOIN operation_receipt r ON r.operation_id=s.operation_id
            WHERE s.source_key LIKE $source AND r.command_kind='cd13e/quest-award'
            """;
        command.Parameters.AddWithValue("$source", $"task/{role}/{task}/1/%");
        return (long)command.ExecuteScalar()!;
    }

    private async Task Barrier(BotClient bot)
    {
        await bot.SendGameAsync(TaskService.TaskFollow, new TaskFollowReq { Id = TaskService.FirstTask });
        await Next(bot, TaskService.TaskFollow, TaskFollowResp.Parser);
    }

    [ClientDataFact]
    public async Task TheNovicePassActivityTaskGrantsTheAwardConfigRowItNames()
    {
        // 9201101's _iAward 920160 has no MultiverseGroupConfig row; AwardConfig 920160 is 2000 novice pass EXP (#166).
        await using var bot = await InWorld();
        await Gm("!quest done 9201101");
        Assert.True(await Read(s => TaskService.IsDelivered(s, 9201101)));
        var plan = Service<AwardService>().ResolveTask(Alice, 920160, null);
        Assert.Equal(920160, plan.AwardId);
        Assert.Contains(plan.Grants, g => g.Entry == NewActivityService.NovicePassExpCurrency && g.Count == 2000);
    }

    [ClientDataFact]
    public async Task TheCaravanQuestMarksItsGuildActivityJoinAndIsDelivered()
    {
        // 1601000's _kEXECAction [115,3000]: ActionConfig 115 MarkGuildActivityJoin with GuildActivityConfig 3000 (#166).
        await using var bot = await InWorld();
        var joined = new List<long>();
        Service<TaskService>().GuildActivityJoined.Add((_, activity) => joined.Add(activity));
        await Gm("!quest done 1601000");
        Assert.True(await Read(s => TaskService.IsDelivered(s, 1601000)));
        Assert.Equal([3000L], joined);
    }

    [ClientDataFact]
    public async Task NoviceTrialGrantsExactlySevenExpAndReplaySurvivesRelog()
    {
        long role;
        await using (var bot = await InWorld())
        {
            role = (long)bot.Self!.Uuid;
            await Accept(10101040);
            await bot.SendGameAsync(SceneService.ClientNpcInteract, new ClientNpcInteractReq { DialogID = 1010104001, Areapointid = 999 });
            await Barrier(bot);
            Assert.Equal(TaskState.Running, (await State(10101040)).Status);
            var point = Service<WorldData>().Map(10111)!.Points[1010104001];
            await Change(s => { s.Position = new Vec3 { X = point.X, Y = point.Y, Z = point.Z }; return Task.CompletedTask; });
            var request = new ClientNpcInteractReq { DialogID = 1010104001, Areapointid = 1010104001 };
            await bot.SendGameAsync(SceneService.ClientNpcInteract, request);
            var reward = await Next(bot, CharacterService.GetRewardNotify, GetRewardNotify.Parser);
            Assert.Equal(100, Assert.Single(reward.Rewards).ItemEntry);
            Assert.Equal(7, reward.Rewards[0].Stack);
            await Next(bot, TaskService.TaskInfoSync, DBTaskInfoIncrement.Parser,
                m => m.CompleteTaskNum.TryGetValue(10101040, out var n) && n.All == 1);
            await bot.SendGameAsync(SceneService.ClientNpcInteract, request);
            await Barrier(bot);
            Assert.Equal(2, await Read(s => s.Progress!.Level));
            Assert.Equal(2, await Read(s => s.Progress!.Exp)); // Five EXP were spent on level 2.
            Assert.Equal(1, await Read(s => s.Tasks!.CompleteTaskNum[10101040]));
            Assert.Equal(1, OwnedQuestSourceCount(role, 10101040));
        }
        await using var again = await InWorld(create: false);
        Assert.Equal(2, Service<RoleStore>().LoadProgress(role).Level);
        Assert.Equal(2, Service<RoleStore>().LoadProgress(role).Exp);
        Assert.Equal(TaskState.Delivery, (await State(10101040)).Status);
        Assert.Equal(new long[] { 1 }, (await State(10101040)).CompleteNum);
        Assert.Equal(1, OwnedQuestSourceCount(role, 10101040));
    }

    [ClientDataFact]
    public async Task SpecificRefusalCodesAndClientCompletionReports()
    {
        await using var bot = await InWorld();
        await bot.SendGameAsync(TaskService.TaskAccept, new TaskAcceptReq { Id = long.MaxValue });
        Assert.Equal(ErrorCodes.TaskNoConfig, (await Next(bot, TaskService.TaskAccept, TaskAcceptResp.Parser)).Result);
        await bot.SendGameAsync(TaskService.TaskAccept, new TaskAcceptReq { Id = TaskService.FirstTask });
        Assert.Equal(ErrorCodes.TaskExists, (await Next(bot, TaskService.TaskAccept, TaskAcceptResp.Parser)).Result);
        await bot.SendGameAsync(TaskService.TaskGiveUp, new TaskGiveUpReq { Id = TaskService.FirstTask });
        Assert.Equal(ErrorCodes.TaskNoGiveUp, (await Next(bot, TaskService.TaskGiveUp, TaskGiveUpResp.Parser)).Result);
        await bot.SendGameAsync(TaskService.TaskFollow, new TaskFollowReq { Id = 987654321 });
        Assert.Equal(ErrorCodes.TaskMissing, (await Next(bot, TaskService.TaskFollow, TaskFollowResp.Parser)).Result);
        await bot.SendGameAsync(TaskService.TaskDelivery, new TaskDeliveryReq { Req = { new TaskDeliverySingleReq { Id = TaskService.FirstTask }, new TaskDeliverySingleReq { Id = 987654321 } } });
        var refused = await Next(bot, TaskService.TaskDelivery, TaskDeliveryResp.Parser);
        Assert.Empty(refused.Data); // failed rows would incorrectly fire the client's TaskRewardEvent
        Assert.Equal(ErrorCodes.TaskStatus, (await Next(bot, TaskService.ApiError, APIErrorMsg.Parser)).ErrorCode);
        Assert.Equal(ErrorCodes.TaskMissing, (await Next(bot, TaskService.ApiError, APIErrorMsg.Parser)).ErrorCode);

        await Accept(10101150);
        foreach (int count in new[] { -1, 0, 2, int.MaxValue })
        {
            await bot.SendGameAsync(TaskService.TriggerTaskEvent, new TriggerTaskEventReq { TaskId = 40004201, GoalCount = count });
            Assert.Equal(ErrorCodes.TaskRequirements, (await Next(bot, TaskService.TriggerTaskEvent, TriggerTaskEventResp.Parser)).Result);
            Assert.Equal(0, (await State(10101150)).CompleteNum[0]);
        }
        await bot.SendGameAsync(TaskService.TriggerTaskEvent, new TriggerTaskEventReq { TaskId = 40004201, GoalCount = 1 });
        Assert.Equal(ErrorCodes.TaskRequirements, (await Next(bot, TaskService.TriggerTaskEvent, TriggerTaskEventResp.Parser)).Result);
        Assert.Equal(0, (await State(10101150)).CompleteNum[0]);
        await Change(s => { s.SceneEntry = 10001; return Task.CompletedTask; }); // configured interaction map
        await bot.SendGameAsync(TaskService.TriggerTaskEvent, new TriggerTaskEventReq { TaskId = 40004201, GoalCount = 1 });
        Assert.Equal(0, (await Next(bot, TaskService.TriggerTaskEvent, TriggerTaskEventResp.Parser)).Result);
        await bot.SendGameAsync(TaskService.TriggerTaskEvent, new TriggerTaskEventReq { TaskId = 40004201, GoalCount = 1 });
        Assert.Equal(ErrorCodes.TaskRequirements, (await Next(bot, TaskService.TriggerTaskEvent, TriggerTaskEventResp.Parser)).Result);
        Assert.Equal(1, await Read(s => s.Tasks!.CompleteTaskNum[10101150]));
    }

    [ClientDataFact]
    public async Task ZeroFiltersCannotCompleteAndJobRankUsesBooleanWireValue()
    {
        await using var bot = await InWorld();
        await Accept(703070, 201120, 1700300, 2000501, 10104104);
        foreach (long id in new long[] { 703070, 201120, 1700300, 2000501, 10104104 })
        {
            var state = await State(id);
            Assert.Equal(TaskState.Running, state.Status);
            Assert.All(state.CompleteNum, n => Assert.Equal(0, n));
        }
        Assert.Equal(2, (await State(201120)).CompleteNum.Count);
        long rank3 = GameData.Int(Service<GameData>().Table("JobConfig").Values.First(row => GameData.Int(row, "_iJobRank") == 3), "_iID");
        await Gm($"!job {rank3}");
        await Refresh();
        Assert.Equal(1, (await State(10104104)).CompleteNum[0]);
        Assert.NotEqual(TaskState.Running, (await State(10104104)).Status);
    }

    [ClientDataFact]
    public async Task BothNpcAlternativesAndDamageParticipantsCountExactlyOncePerDeath()
    {
        await using var bot = await InWorld();
        await using var bob = await InWorld("QuestBob");
        await Accept(10104080, 301010);
        await Gm("!quest accept 10104080", "questbob");
        var combat = Service<CombatService>();
        var callback = combat.Killed.Single(h => h.Target is TaskService);
        for (int i = 0; i < 15; i++)
        {
            int npc = i % 2 == 0 ? 5000055 : 5000056;
            await Change(async s =>
            {
                var monster = new MonsterUnit
                {
                    Uid = 900000 + i, SceneEntry = s.SceneEntry, Dead = true,
                    Template = Service<MonsterService>().Template(npc)!,
                    Home = new SpawnPoint(0, 0, 0, 0, 0, npc), Group = new SpawnGroup(0, false, 1, 0, [], []),
                };
                foreach (var handler in combat.Dealt) handler(s, monster, 1);
                var killer = Service<SessionRegistry>().All().Single(x => x.Role?.Name == "QuestBob");
                await callback(killer, monster);
            });
            Assert.Equal(i + 1, (await State(10104080)).CompleteNum[0]);
            Assert.Equal(i + 1, (await State(301010)).CompleteNum[0]);
        }
        Assert.Equal(TaskState.Delivery, (await State(10104080)).Status);
        Assert.Equal(15, await Read(_ => Service<SessionRegistry>().All().Single(s => s.Role?.Name == "QuestBob").Tasks!.Infos[10104080].CompleteNum[0]));
    }

    [ClientDataFact]
    public async Task NpcSubmitRechecksInventoryAndDuplicateBatchConsumesAndGrantsOnce()
    {
        await using var bot = await InWorld();
        await Accept(10102050);
        await Gm("!item 1009013 10");
        await Refresh();
        Assert.Equal(TaskState.Finish, (await State(10102050)).Status);
        var turnIn = new TaskDeliverySingleReq { Id = 10102050 };
        await bot.SendGameAsync(TaskService.TaskDelivery, new TaskDeliveryReq { Req = { turnIn } });
        Assert.Empty((await Next(bot, TaskService.TaskDelivery, TaskDeliveryResp.Parser)).Data);
        Assert.Equal(ErrorCodes.TaskRequirements, (await Next(bot, TaskService.ApiError, APIErrorMsg.Parser)).ErrorCode);
        Assert.Equal(10, await Read(s => Service<CharacterService>().CountOf(s, 1009013)));
        var point = Service<WorldData>().Map(10213)!.Points[1010203001];
        // Fixture relocation; the actual tested request still goes over 31205.
        await Change(s => { s.SceneEntry = 10213; s.Position = new Vec3 { X = point.X, Y = point.Y, Z = point.Z }; return Task.CompletedTask; });
        await Gm("!delitem 1009013 1");
        await bot.SendGameAsync(TaskService.TaskDelivery, new TaskDeliveryReq { Req = { turnIn } });
        Assert.Empty((await Next(bot, TaskService.TaskDelivery, TaskDeliveryResp.Parser)).Data);
        Assert.NotEqual(0, (await Next(bot, TaskService.ApiError, APIErrorMsg.Parser)).ErrorCode);
        await Gm("!item 1009013 1");
        await Refresh();
        await bot.SendGameAsync(TaskService.TaskDelivery, new TaskDeliveryReq { Req = { turnIn, turnIn.Clone() } });
        var response = await Next(bot, TaskService.TaskDelivery, TaskDeliveryResp.Parser);
        Assert.Equal(0, Assert.Single(response.Data).Result);
        Assert.Equal(ErrorCodes.TaskStatus, (await Next(bot, TaskService.ApiError, APIErrorMsg.Parser)).ErrorCode);
        Assert.Equal(0, await Read(s => Service<CharacterService>().CountOf(s, 1009013)));
        Assert.Equal(1, await Read(s => s.Tasks!.CompleteTaskNum[10102050]));
        Assert.Equal(TaskState.Running, (await State(10102060)).Status);
    }

    [ClientDataFact]
    public async Task NextObjectiveUsesThePreviousQuestsNpcPointAndJumpsToPayonForest()
    {
        // Live incident: the dialog id changes, but Boya keeps point 1010211001.
        // Reproduce the level and position saved for the stalled character; do not complete the task by GM.
        await using var bot = await InWorld();
        await Gm("!lvl 17");
        await Accept(10102120);
        await Gm("!warp 10213 210.18365 120.14987");
        var arrival = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = arrival.SceneEntry });
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);

        var report = new ClientNpcInteractReq { DialogID = 1010212001, Areapointid = 1010212001 };
        await bot.SendGameAsync(SceneService.ClientNpcInteract, report);
        await Barrier(bot);
        Assert.Equal(TaskState.Running, (await State(10102120)).Status);

        report.Areapointid = 1010211001;
        await bot.SendGameAsync(SceneService.ClientNpcInteract, report);
        await Barrier(bot);
        Assert.Equal(TaskState.Delivery, (await State(10102120)).Status);
        Assert.Equal(TaskState.Running, (await State(10102130)).Status);
        await bot.SendGameAsync(SceneService.ClientNpcInteract, report);
        await Barrier(bot);
        Assert.Equal(1, await Read(s => s.Tasks!.CompleteTaskNum[10102120]));

        // TaskConfig's completion action 141 asks the client to send this jump.
        await bot.SendGameAsync(SceneService.CustomJumpMap, new ClientCustomJumpMapReq
        {
            SceneId = 10220, PointId = 1010212001, CustomType = (EClientCustomJumpMapType)2, TypeParam1 = 10102120,
        });
        Assert.Equal(0, (await Next(bot, SceneService.CustomJumpMap, ClientCustomJumpMapResp.Parser)).Result);
        var next = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        Assert.Equal(10220, next.SceneEntry);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = next.SceneEntry });
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        Assert.Equal(10220, await Read(s => s.SceneEntry));
    }

    [ClientDataFact]
    public async Task GuideRecordsAreNotCompletionAndCargoRequiresActiveMappedTask()
    {
        await using var bot = await InWorld();
        await Accept(301080);
        await bot.SendGameAsync(GuideService.UpdateGuide, new UpdateGuideReq { GuideIds = { 21 }, Records = { [21] = 1 } });
        await Barrier(bot);
        Assert.Equal(0, (await State(301080)).CompleteNum[0]);
        await bot.SendGameAsync(GuideService.CompleteGuide, new ClientCompleteGuideReq { GuildId = 21 });
        await Barrier(bot);
        Assert.Equal(1, (await State(301080)).CompleteNum[0]);
        await bot.SendGameAsync(TaskService.GoodsStack, new TaskGoodsStackReq());
        Assert.Equal(ErrorCodes.TaskRequirements, (await Next(bot, TaskService.GoodsStack, TaskGoodsStackResp.Parser)).Result);
        await Accept(10103150);
        await bot.SendGameAsync(TaskService.GoodsStack, new TaskGoodsStackReq());
        Assert.Equal(ErrorCodes.TaskRequirements, (await Next(bot, TaskService.GoodsStack, TaskGoodsStackResp.Parser)).Result);
        await Change(s => { s.SceneEntry = 10101; return Task.CompletedTask; });
        for (int n = 1; n <= 3; n++)
        {
            await bot.SendGameAsync(TaskService.GoodsStack, new TaskGoodsStackReq());
            Assert.Equal(0, (await Next(bot, TaskService.GoodsStack, TaskGoodsStackResp.Parser)).Result);
            Assert.Equal(n, (await State(10103150)).CompleteNum[0]);
        }
        await bot.SendGameAsync(TaskService.GoodsStack, new TaskGoodsStackReq());
        Assert.Equal(ErrorCodes.TaskRequirements, (await Next(bot, TaskService.GoodsStack, TaskGoodsStackResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task ConsumeActionBlocksWithoutItemAndCutsceneExitIsNotPlotCompletion()
    {
        await using var bot = await InWorld();
        await Accept(105091);
        await Gm("!quest finish 105091");
        Assert.Equal(TaskState.Finish, (await State(105091)).Status);
        await Gm("!item 1009001 1");
        await Refresh();
        Assert.Equal(TaskState.Delivery, (await State(105091)).Status);
        Assert.Equal(0, await Read(s => Service<CharacterService>().CountOf(s, 1009001)));
        await Refresh();
        Assert.Equal(1, await Read(s => s.Tasks!.CompleteTaskNum[105091]));

        var row = Service<GameData>().Table("TaskConfig").Values.First(r => r.TryGetProperty("_kEXECAction", out var a)
            && a.ValueKind == JsonValueKind.Array && a.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Array && x.GetArrayLength() > 0 && x[0].ToString() == "621"));
        long cutsceneTask = GameData.Int(row, "_iTaskID");
        await Accept(cutsceneTask);
        await Gm($"!quest done {cutsceneTask}");
        Assert.True(await Read(s => s.InCutScene));
        await Accept(10101020);
        await bot.SendGameAsync(TaskService.ExitCutSceneState, new ExitCutSceneStateReq());
        await Barrier(bot);
        Assert.False(await Read(s => s.InCutScene));
        Assert.Equal(0, (await State(10101020)).CompleteNum[0]);
    }

    [ClientDataFact]
    public async Task CommittedItemUseEquipmentAndCraftDriveTheirOwnObjectives()
    {
        await using var bot = await InWorld();
        await Accept(301070, 1700100, 703070, 701010);
        long potion = await Read(s => s.Items.First(i => i.Entry == 1000000).Guid);
        await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = potion, ItemCount = 0 });
        Assert.NotEqual(0, (await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        Assert.Equal(0, (await State(301070)).CompleteNum[0]);
        await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = potion, ItemCount = 1 });
        Assert.Equal(0, (await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        // ItemUse replies before posting its objective event; observe the committed quest notification.
        await Next(bot, TaskService.TaskInfoSync, DBTaskInfoIncrement.Parser,
            m => m.CompleteTaskNum.TryGetValue(301070, out var completed) && completed.All == 1);
        await Refresh();
        Assert.Equal(1, (await State(301070)).CompleteNum[0]);

        var data = Service<GameData>();
        long gear = GameData.Int(data.Table("ItemConfig").Values.First(row => GameData.Int(row, "_iType") == 5
            && GameData.Int(row, "_iEquipPart") == 4 && GameData.Int(row, "_iLevelNeed") >= 20), "_iID");
        await Gm($"!item {gear} 1");
        long guid = await Read(s => s.Items.First(i => i.Entry == gear).Guid);
        Assert.Equal(0, (await State(1700100)).CompleteNum[0]); // bag ownership is not equipped ownership
        await bot.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { ItemGUID = guid, Pos = (EquipGridType)3 });
        Assert.Equal(0, (await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        await Refresh();
        Assert.Equal(1, (await State(1700100)).CompleteNum[0]);

        await Gm("!quest done 10103020"); // bootstrap craft feature, then perform a real paid 31409
        var recipe = data.Table("EquipCraftConfig").Values.First(r => GameData.Int(r, "_iRank") == 3 && GameData.Int(r, "_iEquipType") == 2);
        foreach (var (entry, count) in GameData.Pairs(recipe, "_kCost")) await Gm($"!item {entry} {(long)count}");
        await bot.SendGameAsync(EquipmentService.Craft, new EquipCraftReq { Rank = 3, Subtype = 2 });
        Assert.Equal(0, (await Next(bot, EquipmentService.Craft, EquipCraftResp.Parser)).Result);
        Assert.Equal(1, (await State(703070)).CompleteNum[0]); // R=6, trailing equipment filter=0
        Assert.Equal(0, (await State(701010)).CompleteNum[0]); // rank 2 required
        await bot.SendGameAsync(EquipmentService.Craft, new EquipCraftReq { Rank = -1, Subtype = 2 });
        Assert.NotEqual(0, (await Next(bot, EquipmentService.Craft, EquipCraftResp.Parser)).Result);
        Assert.Equal(1, (await State(703070)).CompleteNum[0]);
    }

    [ClientDataFact]
    public async Task CraftObjectiveMatchesTheMadeItemsEquipPartNotTheRecipeType()
    {
        // 701050 "Craft 1 Tier 2 Purple Accessories" is [103024, 4, 2, 1, 7]: part 7 is the made item's _iEquipPart.
        // The accessory recipe's request subtype is its _iEquipType 70, so comparing that with 7 never matched.
        await using var bot = await InWorld();
        await Gm("!quest done 10103020"); // opens crafting
        await Accept(701040, 701050);
        var data = Service<GameData>();
        var recipe = data.Table("EquipCraftConfig").Values.First(r => GameData.Int(r, "_iRank") == 2 && GameData.Int(r, "_iEquipType") == 70);
        // _iOrangeGuarantee 11: purple or better comes within 12 crafts.
        for (int i = 0; i < 12 && (await State(701050)).CompleteNum[0] == 0; i++)
        {
            foreach (var (entry, count) in GameData.Pairs(recipe, "_kCost")) await Gm($"!item {entry} {(long)count}");
            await bot.SendGameAsync(EquipmentService.Craft, new EquipCraftReq { Rank = 2, Subtype = 70 });
            Assert.Equal(0, (await Next(bot, EquipmentService.Craft, EquipCraftResp.Parser)).Result);
        }
        Assert.Equal(1, (await State(701050)).CompleteNum[0]);
        Assert.Equal(0, (await State(701040)).CompleteNum[0]); // an accessory is not a top (part 4)
    }

    [ClientDataFact]
    public async Task EquipmentPlanSwitchRefreshesEquippedObjectivesWithoutAnotherProgressEvent()
    {
        await using var bot = await InWorld();
        long gear = GameData.Int(Service<GameData>().Table("ItemConfig").Values.First(row => GameData.Int(row, "_iType") == 5
            && GameData.Int(row, "_iEquipPart") == 4 && GameData.Int(row, "_iLevelNeed") >= 20), "_iID");
        await Gm($"!item {gear} 1");
        long guid = await Read(s => s.Items.First(i => i.Entry == gear).Guid);
        await bot.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { ItemGUID = guid, Pos = (EquipGridType)3 });
        Assert.Equal(0, (await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        await Accept(1700101); // Two pieces required; one worn piece remains a reversible partial objective.
        Assert.Equal(1, (await State(1700101)).CompleteNum[0]);

        await bot.SendGameAsync(CharacterService.EquipSwitchPlan, new EquipSwitchPlanReq { TargetIndex = 1 });
        Assert.Equal(0, (await Next(bot, CharacterService.EquipSwitchPlan, EquipSwitchPlanResp.Parser)).Result);
        await Next(bot, TaskService.TaskInfoSync, DBTaskInfoIncrement.Parser,
            m => m.Infos.GetValueOrDefault(1700101)?.Inc?.CompleteNum?.All?.Value.SequenceEqual(new long[] { 0 }) == true);
        Assert.Equal(0, (await State(1700101)).CompleteNum[0]);

        await bot.SendGameAsync(CharacterService.EquipSwitchPlan, new EquipSwitchPlanReq { TargetIndex = 0 });
        Assert.Equal(0, (await Next(bot, CharacterService.EquipSwitchPlan, EquipSwitchPlanResp.Parser)).Result);
        await Next(bot, TaskService.TaskInfoSync, DBTaskInfoIncrement.Parser,
            m => m.Infos.GetValueOrDefault(1700101)?.Inc?.CompleteNum?.All?.Value.SequenceEqual(new long[] { 1 }) == true);
        Assert.Equal(1, (await State(1700101)).CompleteNum[0]);
        Assert.Equal(TaskState.Running, (await State(1700101)).Status);
    }

    [ClientDataFact]
    public async Task BoundCardStackKeepsItsBindingThroughSocketSplitAndReturn()
    {
        await using var bot = await InWorld();
        await Gm("!lvl 60");
        await Accept(302310, 1700212);
        int card = (int)GameData.Int(Service<GameData>().Table("ItemConfig").Values.First(row => GameData.Int(row, "_iType") == 4
            && GameData.Int(row, "_iSubType") == 400 && GameData.Int(row, "_iCardPart") == 1
            && GameData.Int(row, "_iQuality") >= 4), "_iID");
        await Gm($"!item {card} 1");
        long expiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        await Change(async s => Assert.True(await Service<CharacterService>().ApplyAwardAsync(s,
            new AwardPlan(0, 0, [new AwardGrant(card, 2, 1, expiry)]))));
        long dagger = await Read(s => s.Items.First(i => i.Entry == 2010121).Guid);
        await bot.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { ItemGUID = dagger, Pos = (EquipGridType)1 });
        Assert.Equal(0, (await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        long bound = await Read(s => s.Items.First(i => i.Entry == card && i.BindType == 1).Guid);
        await bot.SendGameAsync(CardService.SlotInsert, new EquipmentCardInsertReq { CardGuid = bound, EquipPos = (EquipGridType)1, SlotIndex = 1 });
        var inserted = await Next(bot, CardService.SlotInsert, EquipmentCardInsertResp.Parser);
        Assert.Equal(0, inserted.Result);
        Assert.Equal(1, (int)inserted.CardInfo.ItemBindInfo.ItemBindType);
        Assert.Equal(expiry, inserted.CardInfo.ItemBindInfo.ExpireTimeStamp);
        await Refresh();
        Assert.Equal(1, (await State(302310)).CompleteNum[0]);
        await bot.SendGameAsync(CardService.LibraryCardUnlock, new LibraryCardUnlockReq { CardId = card, IsTreasure = true });
        Assert.Equal(0, (await Next(bot, CardService.LibraryCardUnlock, LibraryCardUnlockResp.Parser)).Result);
        Assert.Equal(1, (await State(1700212)).CompleteNum[0]);
        await bot.SendGameAsync(CardService.SlotRemove, new EquipmentCardRemoveReq { EquipPos = (EquipGridType)1, SlotIndex = 1 });
        Assert.Equal(0, (await Next(bot, CardService.SlotRemove, EquipmentCardRemoveResp.Parser)).Result);
        var saved = await Read(s => Service<ItemStore>().Load(s.Role!.Id).Where(i => i.Entry == card).ToList());
        Assert.Equal(1, saved.Where(i => i.BindType == 0).Sum(i => i.Stack));
        Assert.Equal(2, saved.Where(i => i.BindType == 1 && i.ExpireTimeStamp == expiry && i.Grid == 0).Sum(i => i.Stack));
    }

    [ClientDataFact]
    public async Task CardUnlockKeepsMandatoryQuestDebtAfterAwardSqlFailureAndRecoversExactlyOnce()
    {
        const long task = 1700212;
        int card = (int)GameData.Int(Service<GameData>().Table("ItemConfig").Values.First(row =>
            GameData.Int(row, "_iType") == 4 && GameData.Int(row, "_iSubType") == 400 &&
            GameData.Int(row, "_iQuality") >= 4), "_iID");
        long role;
        await using (var bot = await InWorld())
        {
            role = (long)bot.Self!.Uuid;
            await Accept(task);
            await Gm($"!item {card} 1");
            Assert.Equal(TaskState.Running, (await State(task)).Status);
            FixtureSql.Execute(Service<AccountStore>(),
                "CREATE TRIGGER fail_card_task_award BEFORE UPDATE OF exp ON role BEGIN SELECT RAISE(ABORT, 'card task award fault'); END");
            await bot.SendGameAsync(CardService.LibraryCardUnlock, new LibraryCardUnlockReq { CardId = card, IsTreasure = true });
            Assert.Equal(0, (await Next(bot, CardService.LibraryCardUnlock, LibraryCardUnlockResp.Parser)).Result);
            var roles = Service<RoleStore>();
            Assert.True(DBLibraryInfo.Parser.ParseFrom(roles.LoadLibrary(role)).Cards[card].TreasureUnlock);
            Assert.True(roles.HasTaskFollowup(role));
            Assert.Equal(0, DBTaskInfo.Parser.ParseFrom(roles.LoadTasks(role)).CompleteTaskNum.GetValueOrDefault(task));
            FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_card_task_award");
        }
        await using (var relog = await InWorld(create: false))
        {
            Assert.Equal(1, (await State(task)).CompleteNum[0]);
            Assert.Equal(1, await Read(s => s.Tasks!.CompleteTaskNum.GetValueOrDefault(task)));
            Assert.False(Service<RoleStore>().HasTaskFollowup(role));
        }
        await using (var again = await InWorld(create: false))
            Assert.Equal(1, await Read(s => s.Tasks!.CompleteTaskNum.GetValueOrDefault(task)));
    }

    [ClientDataFact]
    public async Task OnlyPaidUpgradesAdvanceRefinementAndStrengthObjectives()
    {
        await using var bot = await InWorld();
        await Accept(100008, 100028, 1700112, 1700124, 100007);
        var request = new RefineEquipmentReq { EquipPos = (EquipGridType)1 };
        await bot.SendGameAsync(EquipmentService.Refine, request);
        Assert.Equal(ErrorCodes.RefineMaterials, (await Next(bot, EquipmentService.Refine, RefineEquipmentRsp.Parser)).Result);
        Assert.Equal(0, (await State(100008)).CompleteNum[0]);
        var data = Service<GameData>();
        var refine = data.Table("RefiningTable").Values.First(r => GameData.Int(r, "_iEquipType") == 1 && GameData.Int(r, "_iRefiningInterval") == 1);
        Assert.Equal(10000, GameData.Int(refine, "_iSuccess")); // deterministic first level
        foreach (var (entry, count) in GameData.Pairs(refine, "_kDeplete")) await Gm($"!item {entry} {(long)count}");
        await bot.SendGameAsync(EquipmentService.Refine, request);
        var response = await Next(bot, EquipmentService.Refine, RefineEquipmentRsp.Parser);
        Assert.Equal(0, response.Result);
        Assert.Equal(1, response.RefineLv);
        await Refresh();
        foreach (long id in new long[] { 100008, 100028, 1700112, 1700124 })
            Assert.Equal(1, (await State(id)).CompleteNum[0]);
        await bot.SendGameAsync(EquipmentService.Refine, request);
        Assert.NotEqual(0, (await Next(bot, EquipmentService.Refine, RefineEquipmentRsp.Parser)).Result);
        Assert.Equal(1, (await State(100008)).CompleteNum[0]);

        await Gm("!lvl 60");
        var costs = data.Table("StrengthenTable").Values
            .Where(r => GameData.Int(r, "_iEquipType") == 1 && GameData.Int(r, "_iStrengthenInterval") is >= 1 and <= 5)
            .SelectMany(r => GameData.Pairs(r, "_kDeplete")).GroupBy(c => c.Id);
        foreach (var cost in costs) await Gm($"!item {cost.Key} {(long)cost.Sum(c => c.Value)}");
        await bot.SendGameAsync(EquipmentService.Strengthen, new StrengthenEquipmentReq { EquipPos = (EquipGridType)1, StrengthenNum = 5 });
        Assert.Equal(0, (await Next(bot, EquipmentService.Strengthen, StrengthenEquipmentRsp.Parser)).Result);
        await Refresh();
        Assert.Equal(5, (await State(100007)).CompleteNum[0]);
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoardingDialogueTakesAPassOnlyWhenThePlayerHasOne(bool hasPass)
    {
        // The pass (1253015) has no source on the connected main story, so the boarding check never waits for it
        // (story walk blocker 10103200); the success row's ConsumeItem still takes one the player does have.
        await using var bot = await InWorld();
        if (hasPass) await Gm("!item 1253015 1");
        await Accept(10103200);
        var npc = Service<WorldData>().Map(10101)!.Npcs.First(n => n.NpcId == 100098);
        await Change(s => { s.SceneEntry = 10101; s.Position = new Vec3 { X = npc.X, Y = npc.Y, Z = npc.Z }; return Task.CompletedTask; });
        var report = new ClientNpcInteractReq { NpcUid = npc.Uid, DialogID = 1010320001 };
        await bot.SendGameAsync(SceneService.ClientNpcInteract, report);
        await Barrier(bot);
        Assert.Equal(TaskState.Delivery, (await State(10103200)).Status);
        Assert.Equal(0, await Read(s => Service<CharacterService>().CountOf(s, 1253015)));
        await bot.SendGameAsync(SceneService.ClientNpcInteract, report);
        await Barrier(bot);
        Assert.Equal(1, await Read(s => s.Tasks!.CompleteTaskNum[10103200]));
    }

    [ClientDataFact]
    public async Task DAsiaSerialKeyedRewardCompletesTheFirstRequest()
    {
        await using var bot = await InWorld();
        await Gm("!job 1100");
        var data = Service<GameData>();
        var groups = Assert.IsType<Dictionary<string, JsonElement>>(data.Table("DropGroupConfig"));
        const string groupId = "121121";
        JsonElement original = groups[groupId];
        groups[groupId] = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["1"] = new
            {
                _iID = 121121, _iJobLimit = new[] { 1000 }, _iMinLevelLimit = 1,
                _iMinNumber = 2, _iMaxNumber = 2, _kDropItem = 2010120,
            },
            ["2"] = new
            {
                _iID = 121121, _iJobLimit = new[] { 1100 }, _iMinLevelLimit = 1,
                _iSerial = 2, _kDropItem = 2020120,
            },
        });
        try
        {
            await Accept(10101210);
            await Gm("!quest finish 10101210");
            Assert.Equal(TaskState.Delivery, (await State(10101210)).Status);
            Assert.Equal(1, await Read(s => s.Tasks!.CompleteTaskNum[10101210]));
            Assert.Equal(10, await Read(s => Service<CharacterService>().CountOf(s, 2020120)));
            Assert.Equal(1, OwnedQuestSourceCount((long)bot.Self!.Uuid, 10101210));
        }
        finally { groups[groupId] = original; }
    }

    [ClientDataFact]
    public async Task ChapterOneFinalDialoguesGrantTheCareerPoolOnce()
    {
        await using var bot = await InWorld();
        await Gm("!job 1100");
        await Accept(10101250);
        var point = Service<WorldData>().Map(10111)!.Points[1010121001];
        await Change(s => { s.Position = new Vec3 { X = point.X, Y = point.Y, Z = point.Z }; return Task.CompletedTask; });
        foreach (long id in new long[] { 10101250, 10101251, 10102010 })
        {
            var row = Service<GameData>().Row("TaskConfig", id)!.Value;
            int dialog = row.GetProperty("_kProgressTarget")[0][3].GetInt32();
            await bot.SendGameAsync(SceneService.ClientNpcInteract, new ClientNpcInteractReq { DialogID = dialog, Areapointid = 1010121001 });
            await Barrier(bot);
            Assert.Equal(TaskState.Delivery, (await State(id)).Status);
        }
        var granted = await Read(s => s.Items.Where(i => i.Entry == 1501014).Select(i => (i.Stack, i.BindType)).ToArray());
        Assert.Equal((1, 1), Assert.Single(granted));
        await bot.SendGameAsync(SceneService.ClientNpcInteract, new ClientNpcInteractReq { DialogID = 1010125001, Areapointid = 1010121001 });
        await Barrier(bot);
        Assert.Equal(1, await Read(s => Service<CharacterService>().CountOf(s, 1501014)));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

}
