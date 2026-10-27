using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Tests;

/// <summary>Real gate exchanges for the clientB everyday bag, quest and attribute-plan operations.</summary>
public sealed class WorldRoleOperationsTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-roleops-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

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

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var frame = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(frame.Body);
    }

    private async Task Gm(string account, string command)
    {
        using var http = new HttpClient();
        var response = await http.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(command));
        response.EnsureSuccessStatusCode();
    }

    [ClientDataFact]
    public async Task ShortcutUsesOwnedPotionAndPreferencesSurviveRelog()
    {
        long potion;
        await using (var alice = await InWorld("Alice"))
        {
            var login = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
            potion = login.PackInfo[1].All.Items.Values.Single(i => i.ItemEntry == 1000000).ItemGuid;
            var emptyQuickUse = Assert.Single(login.QuickUseInfos.All.Value);
            Assert.Equal((0, 0), (emptyQuickUse.LieQuickUse, emptyQuickUse.ItemEntry));
            Assert.Equal(60, login.MedicinesAutoUse.All.HpConfig.Condition);
            // Every bar slot is present from login: the client only updates slots it already has.
            Assert.Equal([0, 1, 2, 3, 4, 5], login.ShortcutBarInfos.All.Value.Select(s => s.SlotIndex));
            Assert.All(login.ShortcutBarInfos.All.Value, s => Assert.Equal(0, s.ItemGuid));

            // The tutorial drags a potion onto slot 5. The client applies element-wise increments, never a new list.
            await alice.SendGameAsync(CharacterService.ShortcutBarSave, new ShortcutBarSaveReq { SlotIndex = 4, ItemGuid = potion });
            Assert.Equal(0, (await Next(alice, CharacterService.ShortcutBarSave, ShortcutBarSaveRes.Parser)).Result);
            var slotFive = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.ShortcutBarInfos != null);
            Assert.Null(slotFive.ShortcutBarInfos.All);
            Assert.Equal(6, slotFive.ShortcutBarInfos.Inc.Value.Count);
            Assert.Equal(potion, slotFive.ShortcutBarInfos.Inc.Value[4].ItemGuid);

            await alice.SendGameAsync(CharacterService.ShortcutBarSave, new ShortcutBarSaveReq { SlotIndex = 0, ItemGuid = potion });
            Assert.Equal(0, (await Next(alice, CharacterService.ShortcutBarSave, ShortcutBarSaveRes.Parser)).Result);
            var bound = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => !m.SyncAllMark && m.ShortcutBarInfos?.Inc?.Value.Count == 6);
            Assert.Equal(potion, bound.ShortcutBarInfos.Inc.Value[0].ItemGuid);
            Assert.True(bound.ShortcutBarInfos.Inc.Value[0].HasItemGuid);
            Assert.False(bound.ShortcutBarInfos.Inc.Value[1].HasItemGuid);
            await alice.SendGameAsync(CharacterService.QuickUseSave, new QuickUseSaveReq { LieQuickUse = 0, EntryItem = 1000000 });
            var quick = await Next(alice, CharacterService.QuickUseSave, QuickUseSaveRes.Parser);
            Assert.Equal(0, quick.Result);
            Assert.Equal(1000000, Assert.Single(quick.QuickSaveInfos).ItemEntry);

            await alice.SendGameAsync(CharacterService.ShortcutBarUse, new ShortcutBarUseReq { SlotIndex = 0 });
            Assert.Equal(0, (await Next(alice, CharacterService.ShortcutBarUse, ShortcutBarUseRes.Parser)).Result);
            var consumed = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => !m.SyncAllMark && m.PackInfo.TryGetValue(1, out var p) && p.Inc?.Items.TryGetValue(potion, out var i) == true && i.Inc?.Stack == 19);
            Assert.Equal(19, consumed.PackInfo[1].Inc.Items[potion].Inc.Stack);

            await alice.SendGameAsync(CharacterService.ShortcutBarSave, new ShortcutBarSaveReq { SlotIndex = 1, ItemGuid = 123456789 });
            Assert.Equal(ErrorCodes.ItemMissing, (await Next(alice, CharacterService.ShortcutBarSave, ShortcutBarSaveRes.Parser)).Result);
            await alice.SendGameAsync(CharacterService.ShortcutBarUse, new ShortcutBarUseReq { SlotIndex = 1 });
            Assert.Equal(ErrorCodes.ItemMissing, (await Next(alice, CharacterService.ShortcutBarUse, ShortcutBarUseRes.Parser)).Result);
        }
        await using var again = await InWorld("Alice", create: false);
        var persisted = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        Assert.Equal(19, persisted.PackInfo[1].All.Items[potion].Stack);
        Assert.Equal(6, persisted.ShortcutBarInfos.All.Value.Count);
        Assert.Contains(persisted.ShortcutBarInfos.All.Value, s => s.SlotIndex == 0 && s.ItemGuid == potion);
        Assert.Contains(persisted.ShortcutBarInfos.All.Value, s => s.SlotIndex == 4 && s.ItemGuid == 0);
        Assert.Equal(6, persisted.ShortcutBarInfos.All.Value.Count);
        Assert.Equal(1000000, Assert.Single(persisted.QuickUseInfos.All.Value).ItemEntry);
    }

    [ClientDataFact]
    public async Task ItemUseRejectsUnsupportedBatchAndPersistsSingleUseCooldown()
    {
        await using var alice = await InWorld("Alice");
        var initial = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        long potion = initial.PackInfo[1].All.Items.Values.Single(i => i.ItemEntry == 1000000).ItemGuid;
        await alice.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = potion, ItemCount = 2 });
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, CharacterService.ItemUse, ItemUseResp.Parser)).Result);

        await alice.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = potion, ItemCount = 1 });
        var used = await Next(alice, CharacterService.ItemUse, ItemUseResp.Parser);
        Assert.Equal(0, used.Result);
        Assert.Equal(1000000, used.ItemId);
        Assert.True(used.ItemCd > 0);
        var cd = await Next(alice, CharacterService.ItemsCdNotify, ItemsCDNotify.Parser);
        Assert.Equal(1000000, Assert.Single(cd.Items).ItemId);

        await alice.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = potion, ItemCount = 1 });
        Assert.NotEqual(0, (await Next(alice, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        var current = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");
        Assert.Equal(19, current.Items.Single(i => i.Guid == potion).Stack);
    }

    [ClientDataFact]
    public async Task SeasonLapseDecompositionRefusesWithoutConsumingAnItem()
    {
        await using var alice = await InWorld("Alice");
        var initial = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        long potion = initial.PackInfo[1].All.Items.Values.Single(i => i.ItemEntry == 1000000).ItemGuid;
        var request = new ItemDecomposeReq { IsLapseDecompose = true };
        request.DecomposeItem[potion] = 1;
        await alice.SendGameAsync(EquipmentService.ItemDecompose, request);
        Assert.Equal(ErrorCodes.FeatureUnavailable,
            (await Next(alice, EquipmentService.ItemDecompose, ItemDecomposeResp.Parser)).Result);
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");
        Assert.Equal(20, session.Items.Single(i => i.Guid == potion).Stack);
    }

    [ClientDataFact]
    public async Task StatPlanSwitchAndRenamePersistWithoutLosingOtherAllocation()
    {
        await using (var alice = await InWorld("Alice"))
        {
            await Next(alice, CharacterService.BaseAttrSync, BaseAttrInfoIncrement.Parser);
            var allocation = new AllocateAttrPointReq { PlanIndex = 0 };
            for (int i = 1; i <= 6; i++) allocation.Points[i] = i == 1 ? 5 : 1;
            await alice.SendGameAsync(CharacterService.AllocateAttrPoints, allocation);
            Assert.Equal(0, (await Next(alice, CharacterService.AllocateAttrPoints, AllocateAttrPointRsp.Parser)).Result);
            var allocated = await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
                m => m.Property.Propertys.Any(p => p.PropertyId == 1 && p.Data == 5));
            Assert.Single(allocated.AttrModTypes);
            Assert.Equal((int)EAttrModType.Base, allocated.AttrModTypes[0]);

            await alice.SendGameAsync(CharacterService.RenameStatPlan, new PlanAttrPointNameChangeReq { PlanIndex = 1, PlanName = "Tank" });
            Assert.Equal("Tank", (await Next(alice, CharacterService.RenameStatPlan, PlanAttrPointNameChangeResq.Parser)).PlanName);
            await alice.SendGameAsync(CharacterService.ChangeStatPlan, new PlanAttrPointChangeReq { DestPlanIndex = 1 });
            Assert.Equal(0, (await Next(alice, CharacterService.ChangeStatPlan, PlanAttrPointChangeResq.Parser)).Result);
            var swapped = await Next(alice, CharacterService.BaseAttrSync, BaseAttrInfoIncrement.Parser, m => !m.SyncAllMark && m.CurPlanIndex == 1);
            Assert.Equal(1, swapped.CurPlanIndex);
            var tankStats = await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
                m => m.Property.Propertys.Any(p => p.PropertyId == 1 && p.Data == 1));
            Assert.Single(tankStats.AttrModTypes);
            Assert.Equal((int)EAttrModType.Base, tankStats.AttrModTypes[0]);

            await alice.SendGameAsync(CharacterService.ChangeStatPlan, new PlanAttrPointChangeReq { DestPlanIndex = 77 });
            var rejected = await Next(alice, CharacterService.ChangeStatPlan, PlanAttrPointChangeResq.Parser);
            Assert.NotEqual(0, rejected.Result);
            Assert.Equal(1, rejected.DestPlanIndex);

            await alice.SendGameAsync(CharacterService.ChangeStatPlan, new PlanAttrPointChangeReq { DestPlanIndex = 0 });
            Assert.Equal(0, (await Next(alice, CharacterService.ChangeStatPlan, PlanAttrPointChangeResq.Parser)).Result);
            var attackStats = await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
                m => m.Property.Propertys.Any(p => p.PropertyId == 1 && p.Data == 5));
            Assert.Single(attackStats.AttrModTypes);
            Assert.Equal((int)EAttrModType.Base, attackStats.AttrModTypes[0]);
        }
        await using var again = await InWorld("Alice", create: false);
        var plans = await Next(again, CharacterService.BaseAttrSync, BaseAttrInfoIncrement.Parser);
        Assert.Equal(0, plans.CurPlanIndex);
        Assert.Equal(5, plans.AllPlanBaseProperty[0].All.BaseProperty[1]);
        Assert.Equal("Tank", plans.AllPlanBaseProperty[1].All.PlanName);
    }

    [ClientDataFact]
    public async Task MerchantCartSplitAndReturnPersistAndRefuseOverdraw()
    {
        await using (var alice = await InWorld("Alice"))
        {
            var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");
            var initial = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
            long potion = initial.PackInfo[1].All.Items.Values.Single(i => i.ItemEntry == 1000000).ItemGuid;
            var move = new CareerSpecialTransferItemsReq { TransType = CareerSpecialTransferItemsType.ToCart };
            move.ItemGuidCountMap[potion] = 7;
            await alice.SendGameAsync(CharacterService.CareerSpecialTransfer, move);
            Assert.Equal(ErrorCodes.FunctionLocked,
                (await Next(alice, CharacterService.CareerSpecialTransfer, CareerSpecialTransferItemsRsp.Parser)).Result);

            await Gm("alice", "!job 6100");
            session.Progress!.Skills[16003] = 1; // Cart Use, group of row 1600301
            _app.Services.GetRequiredService<RoleStore>().SaveProgress(session.Role!.Id, session.Progress);
            await alice.SendGameAsync(CharacterService.CareerSpecialTransfer, move);
            Assert.Equal(0, (await Next(alice, CharacterService.CareerSpecialTransfer, CareerSpecialTransferItemsRsp.Parser)).Result);
            await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.PackInfo.TryGetValue(100, out var cart) && cart.All?.Items.Count > 0);
            Assert.Equal(13, session.Items.Single(i => i.Guid == potion).Stack);
            var cartItem = Assert.Single(session.Items, i => i.Pack == 100);
            Assert.Equal(7, cartItem.Stack);
            Assert.NotEqual(potion, cartItem.Guid);

            move.ItemGuidCountMap[potion] = 100;
            await alice.SendGameAsync(CharacterService.CareerSpecialTransfer, move);
            Assert.Equal(ErrorCodes.ItemsShort,
                (await Next(alice, CharacterService.CareerSpecialTransfer, CareerSpecialTransferItemsRsp.Parser)).Result);
            Assert.Equal(7, cartItem.Stack);

            var back = new CareerSpecialTransferItemsReq { TransType = CareerSpecialTransferItemsType.ToNormal };
            back.ItemGuidCountMap[cartItem.Guid] = 7;
            await alice.SendGameAsync(CharacterService.CareerSpecialTransfer, back);
            Assert.Equal(0, (await Next(alice, CharacterService.CareerSpecialTransfer, CareerSpecialTransferItemsRsp.Parser)).Result);
            Assert.Equal(20, session.Items.Where(i => i.Grid == 0 && i.Pack == 1 && i.Entry == 1000000).Sum(i => i.Stack));
            Assert.DoesNotContain(session.Items, i => i.Pack == 100);
        }
        await using var again = await InWorld("Alice", create: false);
        var restored = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        Assert.Equal(20, restored.PackInfo[1].All.Items.Values.Where(i => i.ItemEntry == 1000000).Sum(i => i.Stack));
        Assert.Equal(50, (await Next(again, CharacterService.CareerSpecialSizeChange,
            NotifyCareerSpecialPackSizeChange.Parser)).Size);
    }

    [ClientDataFact]
    public async Task AutoMedicineAndArrowBindingsPersistAndRejectUnownedArrow()
    {
        await using (var alice = await InWorld("Alice"))
        {
            var fresh = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
            // A new role still gets both configs with every window slot, or the client's Add buttons hit a nil config.
            Assert.Equal(60, fresh.MedicinesAutoUse.All.HpConfig.Condition);
            Assert.Equal([0, 0, 0], fresh.MedicinesAutoUse.All.HpConfig.ItemEntrys);
            Assert.Equal([0, 0, 0], fresh.MedicinesAutoUse.All.SpConfig.ItemEntrys);

            // Add All and Add send every slot, with 0 for the empty ones.
            var addAll = new MedicinesAutoUseSetReq
            {
                Type = MedicineAutoUseType.Hp, OpType = MedicineAutoUseOpType.Medicine,
                SetData = new MedicinesAutoUse { Condition = 60 },
            };
            addAll.SetData.ItemEntrys.Add([1000000, 0, 0]);
            await alice.SendGameAsync(CharacterService.MedicinesAutoUseSet, addAll);
            var added = await Next(alice, CharacterService.MedicinesAutoUseSet, MedicinesAutoUseSetResp.Parser);
            Assert.Equal(0, added.Result);
            Assert.Equal([1000000, 0, 0], added.SetData.ItemEntrys);

            var medicine = new MedicinesAutoUseSetReq
            {
                Type = MedicineAutoUseType.Hp, OpType = MedicineAutoUseOpType.Switch,
                SetData = new MedicinesAutoUse { IsOpen = true, Condition = 50 },
            };
            medicine.SetData.ItemEntrys.Add(1000000);
            await alice.SendGameAsync(CharacterService.MedicinesAutoUseSet, medicine);
            var saved = await Next(alice, CharacterService.MedicinesAutoUseSet, MedicinesAutoUseSetResp.Parser);
            Assert.Equal(0, saved.Result);
            Assert.Equal(50, saved.SetData.Condition);
            Assert.Equal([1000000, 0, 0], saved.SetData.ItemEntrys);

            await alice.SendGameAsync(CharacterService.ArrowSwitch, new ItemArrowSwitchReq { ItemId = 1002011 });
            Assert.Equal(ErrorCodes.ItemMissing, (await Next(alice, CharacterService.ArrowSwitch, ItemArrowSwitchResp.Parser)).Result);
            await Gm("alice", "!item 1002011 10");
            await alice.SendGameAsync(CharacterService.ArrowSwitch, new ItemArrowSwitchReq { ItemId = 1002011 });
            var arrow = await Next(alice, CharacterService.ArrowSwitch, ItemArrowSwitchResp.Parser);
            Assert.Equal(0, arrow.Result);
            Assert.Equal(10, arrow.ItemCount);
        }
        await using var again = await InWorld("Alice", create: false);
        var items = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        Assert.Equal(1002011, items.CurrentArrowID);
        Assert.True(items.MedicinesAutoUse.All.HpConfig.IsOpen);
        Assert.Equal(50, items.MedicinesAutoUse.All.HpConfig.Condition);
        Assert.Equal([1000000, 0, 0], items.MedicinesAutoUse.All.HpConfig.ItemEntrys);
        Assert.Equal([0, 0, 0], items.MedicinesAutoUse.All.SpConfig.ItemEntrys);
    }

    [ClientDataFact]
    public async Task QuestResetAndBranchChoiceRequireOwnedPendingState()
    {
        await using var alice = await InWorld("Alice");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");
        await Next(alice, TaskService.TaskInfoSync, DBTaskInfoIncrement.Parser);

        await alice.SendGameAsync(TaskService.TaskReset, new TaskResetReq { Id = TaskService.FirstTask });
        var refusal = await Next(alice, TaskService.ApiError, APIErrorMsg.Parser, m => m.OriginMsgId == TaskService.TaskReset);
        Assert.Equal(ErrorCodes.TaskNoGiveUp, refusal.ErrorCode);

        await Gm("alice", "!quest accept 2000500");
        Assert.Equal(TaskState.Running, session.Tasks!.Infos[2000500].Status);
        await alice.SendGameAsync(TaskService.TaskReset, new TaskResetReq { Id = 2000500 });
        Assert.Equal(0, (await Next(alice, TaskService.TaskReset, TaskResetResp.Parser)).Result);
        Assert.All(session.Tasks.Infos[2000500].CompleteNum, n => Assert.Equal(0, n));

        await alice.SendGameAsync(TaskService.TaskSelectQuestBranch, new SelectTaskQuestBranchReq { BranchId = 12345 });
        Assert.Equal(ErrorCodes.TaskRequirements, (await Next(alice, TaskService.ApiError, APIErrorMsg.Parser,
            m => m.OriginMsgId == TaskService.TaskSelectQuestBranch)).ErrorCode);
        session.Tasks.WaitSelectBranch[12345] = 1;
        await alice.SendGameAsync(TaskService.TaskSelectQuestBranch, new SelectTaskQuestBranchReq { BranchId = 12345 });
        var branch = await Next(alice, TaskService.TaskSelectQuestBranch, SelectTaskQuestBranchResp.Parser);
        Assert.Equal(12345, branch.Req.BranchId);
        Assert.True(session.Tasks.SelectBranch[12345]);
        await alice.SendGameAsync(TaskService.TaskSelectQuestBranch, new SelectTaskQuestBranchReq { BranchId = 12345 });
        Assert.Equal(ErrorCodes.TaskRequirements, (await Next(alice, TaskService.ApiError, APIErrorMsg.Parser,
            m => m.OriginMsgId == TaskService.TaskSelectQuestBranch)).ErrorCode);

        await alice.SendGameAsync(TaskService.TaskLuckyTurn, new LuckyTurnReq { Type = 1, Version = 1 });
        Assert.Equal(ErrorCodes.ItemsShort, (await Next(alice, TaskService.ApiError, APIErrorMsg.Parser,
            m => m.OriginMsgId == TaskService.TaskLuckyTurn)).ErrorCode);

        await alice.SendGameAsync(TaskService.TaskNpcChat, new TaskNpcChatReq { ChatId = 999999 });
        Assert.Equal(ErrorCodes.TaskRequirements, (await Next(alice, TaskService.TaskNpcChat, TaskNpcChatResp.Parser)).Result);
        await alice.SendGameAsync(TaskService.TaskFinishPyd, new FinishPYDTaskReq());
        Assert.Equal(ErrorCodes.TaskMissing,
            (await Next(alice, TaskService.TaskFinishPyd, FinishPYDTaskResp.Parser)).Result);
        await alice.SendGameAsync(TaskService.TaskTrackTeleport, new TaskTrackTeleportReq { TaskId = 999999 });
        Assert.Equal(ErrorCodes.TaskRequirements, (await Next(alice, TaskService.ApiError, APIErrorMsg.Parser,
            m => m.OriginMsgId == TaskService.TaskTrackTeleport)).ErrorCode);
    }

    [ClientDataFact]
    public async Task PydReportFinishesOnlyActiveDownloadQuestAndUiDeliveryPaysOnceAfterRelog()
    {
        await using (var alice = await InWorld("Alice"))
        {
            var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");
            await Next(alice, TaskService.TaskInfoSync, DBTaskInfoIncrement.Parser);
            long before = session.Progress!.Currency.GetValueOrDefault(1);
            await Gm("alice", "!quest accept 100");
            Assert.Equal(TaskState.Running, session.Tasks!.Infos[100].Status);
            Assert.Equal(0, session.Tasks.Infos[100].CompleteNum[0]);
            int start = alice.Inbox.Snapshot().Count;

            await alice.SendGameAsync(TaskService.TaskFinishPyd, new FinishPYDTaskReq());
            Assert.Equal(0, (await Next(alice, TaskService.TaskFinishPyd, FinishPYDTaskResp.Parser)).Result);
            var newFrames = alice.Inbox.Snapshot().Skip(start).Where(e => e.IsGame(TaskService.TaskInfoSync)
                || e.IsGame(TaskService.TaskFinishPyd)).ToArray();
            Assert.Equal(TaskService.TaskInfoSync, newFrames[0].MsgId);
            Assert.Contains(100L, DBTaskInfoIncrement.Parser.ParseFrom(newFrames[0].Body).Infos.Keys);
            Assert.Equal(TaskService.TaskFinishPyd, newFrames[^1].MsgId);
            Assert.Equal(TaskState.Finish, session.Tasks.Infos[100].Status);
            Assert.Equal(1, session.Tasks.Infos[100].CompleteNum[0]);
            Assert.Equal(0, session.Tasks.CompleteTaskNum.GetValueOrDefault(100));
            Assert.Equal(before, session.Progress.Currency.GetValueOrDefault(1));

            await alice.SendGameAsync(TaskService.TaskFinishPyd, new FinishPYDTaskReq());
            Assert.Equal(ErrorCodes.TaskStatus,
                (await Next(alice, TaskService.TaskFinishPyd, FinishPYDTaskResp.Parser)).Result);
            Assert.Equal(before, session.Progress.Currency.GetValueOrDefault(1));
        }

        await using var again = await InWorld("Alice", create: false);
        var saved = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");
        Assert.Equal(TaskState.Finish, saved.Tasks!.Infos[100].Status);
        Assert.Equal(1, saved.Tasks.Infos[100].CompleteNum[0]);
        Assert.Equal(0, saved.Tasks.CompleteTaskNum.GetValueOrDefault(100));
        long beforeReward = saved.Progress!.Currency.GetValueOrDefault(1);
        await again.SendGameAsync(TaskService.TaskFinishPyd, new FinishPYDTaskReq());
        Assert.Equal(ErrorCodes.TaskStatus,
            (await Next(again, TaskService.TaskFinishPyd, FinishPYDTaskResp.Parser)).Result);

        var turnIn = new TaskDeliveryReq { Req = { new TaskDeliverySingleReq { Id = 100 } } };
        await again.SendGameAsync(TaskService.TaskDelivery, turnIn);
        Assert.Equal(0, Assert.Single((await Next(again, TaskService.TaskDelivery, TaskDeliveryResp.Parser)).Data).Result);
        Assert.Equal(TaskState.Delivery, saved.Tasks.Infos[100].Status);
        Assert.Equal(1, saved.Tasks.CompleteTaskNum[100]);
        Assert.Equal(beforeReward + 1000, saved.Progress.Currency.GetValueOrDefault(1));

        await again.SendGameAsync(TaskService.TaskFinishPyd, new FinishPYDTaskReq());
        Assert.Equal(ErrorCodes.TaskStatus,
            (await Next(again, TaskService.TaskFinishPyd, FinishPYDTaskResp.Parser)).Result);
        await again.SendGameAsync(TaskService.TaskDelivery, turnIn);
        Assert.Empty((await Next(again, TaskService.TaskDelivery, TaskDeliveryResp.Parser)).Data);
        Assert.Equal(ErrorCodes.TaskStatus, (await Next(again, TaskService.ApiError, APIErrorMsg.Parser,
            m => m.OriginMsgId == TaskService.TaskDelivery)).ErrorCode);
        Assert.Equal(beforeReward + 1000, saved.Progress.Currency.GetValueOrDefault(1));
    }

    [ClientDataFact]
    public async Task LuckyTurnSpendsGemsAndStopsTheWheelOnTheSlotItGranted()
    {
        // #bug-reports "Dodo Pink Gem hard lock": the wheel (UI_Sys_Main_EntrustLotteryPanelCtrl) stays locked until a
        // GetRewardNotify of ManifestTypeRunRing (18) names the slots it stops on. The slots are award 84011's preview.
        const int Gem = 1250001, RunRing = 18;
        var data = _app.Services.GetRequiredService<GameData>();
        long award = MultiverseGroupConfig.Get(data, 84011)!.Value.ValueDefault;
        var slots = Assert.Single(AwardConfig.Get(data, award)).Preview.Select(t => ((int)t[0], (int)t[1])).ToArray();
        await using (var alice = await InWorld("Alice"))
        {
            var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");
            await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
            await Gm("alice", $"!item {Gem} 11");

            await alice.SendGameAsync(TaskService.TaskLuckyTurn, new LuckyTurnReq { Type = 0, Version = 1 });
            var one = await Next(alice, CharacterService.GetRewardNotify, GetRewardNotify.Parser, m => m.RewardType.Contains(RunRing));
            Assert.Equal(0, (await Next(alice, TaskService.TaskLuckyTurn, LuckyTurnResp.Parser)).Result);
            int slot = Assert.Single(one.RewardIndexes);
            Assert.InRange(slot, 0, slots.Length - 1);
            var granted = Assert.Single(one.Rewards);
            Assert.Equal(slots[slot], (granted.ItemEntry, (int)granted.Stack));
            Assert.Equal(10, session.Items.Where(i => i.Grid == 0 && i.Entry == Gem).Sum(i => i.Stack));

            await alice.SendGameAsync(TaskService.TaskLuckyTurn, new LuckyTurnReq { Type = 1, Version = 1 });
            var ten = await Next(alice, CharacterService.GetRewardNotify, GetRewardNotify.Parser, m => m.RewardType.Contains(RunRing));
            Assert.Equal(0, (await Next(alice, TaskService.TaskLuckyTurn, LuckyTurnResp.Parser)).Result);
            Assert.Equal(10, ten.RewardIndexes.Count);
            Assert.All(ten.RewardIndexes, i => Assert.InRange(i, 0, slots.Length - 1));
            Assert.DoesNotContain(session.Items, i => i.Grid == 0 && i.Entry == Gem);

            await alice.SendGameAsync(TaskService.TaskLuckyTurn, new LuckyTurnReq { Type = 0, Version = 1 });
            Assert.Equal(ErrorCodes.ItemsShort, (await Next(alice, TaskService.ApiError, APIErrorMsg.Parser,
                m => m.OriginMsgId == TaskService.TaskLuckyTurn)).ErrorCode);
        }
        await using var again = await InWorld("Alice", create: false);
        var saved = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");
        Assert.DoesNotContain(saved.Items, i => i.Grid == 0 && i.Entry == Gem);
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "data", "config")))
            dir = Directory.GetParent(dir)?.FullName ?? throw new DirectoryNotFoundException("repository data/config");
        return dir;
    }
}
