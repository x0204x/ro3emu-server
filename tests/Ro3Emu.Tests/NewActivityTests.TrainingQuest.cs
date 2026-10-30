using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class NewActivityTests
{
    private const long TrainingQuest = 10200120;

    /// <summary>實際G資料、正常計時與封包；零DPS仍屬已完成挑戰，任務獎勵只提交一次。</summary>
    [ClientDataFact]
    public async Task ZeroDamageTrainingCompletesQuestOnceAcrossAnotherRunAndRelogin()
    {
        var bot = await InWorld("TrainingQuest");
        long role = (long)bot.Self!.Uuid;
        try
        {
            await PrepareTrainingQuest(bot);
            await StartTrainingQuestRun(bot);
            await CompleteZeroDamageTraining(bot);
            await QuestProbe.DeliveredAsync(_app, Assert.IsType<string>(bot.Account), TrainingQuest);
            AssertTrainingQuestDelivery(role, 1);
            await StartTrainingQuestRun(bot);
            await CompleteZeroDamageTraining(bot);
            AssertTrainingQuestDelivery(role, 1);
        }
        finally { await bot.DisposeAsync(); }
        await using var again = await InWorld("TrainingQuest", false);
        await Next(again, TrainingService.Sync, DBTrainingGroundInfoIncrement.Parser);
        Assert.Equal(0, Session(again).Training!.MaxDps);
        AssertTrainingQuestDelivery(role, 1);
    }

    /// <summary>暫停持久化時，記憶體／DB都不能先報進度；放行同筆結果後才完成任務。</summary>
    [ClientDataFact]
    public async Task TrainingQuestProgressPublishesOnlyAfterResultCommit()
    {
        await using var bot = await InWorld("TrainingCommit");
        await PrepareTrainingQuest(bot);
        await StartTrainingQuestRun(bot);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Service<AccountStore>().BeforeOwnedCommitForTest = label =>
        {
            if (label != "training/result") return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };
        try
        {
            _clock.AdvanceSeconds(61);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, Session(bot).Tasks!.Infos[TrainingQuest].CompleteNum[0]);
            var saved = DBTaskInfo.Parser.ParseFrom(Service<RoleStore>().LoadTasks((long)bot.Self!.Uuid)!);
            Assert.Equal(0, saved.Infos[TrainingQuest].CompleteNum[0]);
            Assert.Equal(0, saved.CompleteTaskNum.GetValueOrDefault(TrainingQuest));
        }
        finally { release.TrySetResult(); Service<AccountStore>().BeforeOwnedCommitForTest = null; }
        await Next(bot, DungeonService.Result, DungeonResNotify.Parser, r => r.Data?.TrainingGroundData != null);
        await QuestProbe.DeliveredAsync(_app, Assert.IsType<string>(bot.Account), TrainingQuest);
        AssertTrainingQuestDelivery((long)bot.Self!.Uuid, 1);
    }

    /// <summary>提前重設或離開不是完成；過期的排程不得補發任務或獎勵。</summary>
    [ClientDataFact]
    public async Task ResetOrLeaveBeforeTrainingEndsDoesNotCreditQuest()
    {
        await using var bot = await InWorld("TrainingAbort");
        await PrepareTrainingQuest(bot);
        await StartTrainingQuestRun(bot);
        _clock.AdvanceSeconds(20);
        await bot.SendGameAsync(TrainingService.ChallengeReset, new NullArg());
        Assert.Equal(0, (await Next(bot, TrainingService.ChallengeReset, TrainingChallengeResetRes.Parser)).Result);
        _clock.AdvanceSeconds(61);
        await bot.HeartbeatAsync();
        Assert.Equal(0, Session(bot).Tasks!.Infos[TrainingQuest].CompleteNum[0]);
        await StartTrainingQuestRun(bot);
        await bot.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(bot, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        _clock.AdvanceSeconds(61);
        await bot.HeartbeatAsync();
        var saved = DBTaskInfo.Parser.ParseFrom(Service<RoleStore>().LoadTasks((long)bot.Self!.Uuid)!);
        Assert.Equal(0, saved.Infos[TrainingQuest].CompleteNum[0]);
        AssertTrainingQuestDelivery((long)bot.Self!.Uuid, 0);
    }

    private async Task PrepareTrainingQuest(BotClient bot)
    {
        await Next(bot, TrainingService.Sync, DBTrainingGroundInfoIncrement.Parser);
        await Gm(bot, "!lvl 99");
        await Gm(bot, "!job 1100");
        await QuestProbe.AcceptAsync(_app, _http, Assert.IsType<string>(bot.Account), TrainingQuest);
        Assert.Equal(0, Session(bot).Tasks!.Infos[TrainingQuest].CompleteNum[0]);
        await bot.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = 2100011 });
        Assert.Equal(0, (await Next(bot, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        await LoadPrivate(bot, 2100011);
    }

    private static async Task StartTrainingQuestRun(BotClient bot)
    {
        await bot.SendGameAsync(DungeonService.ClientEvent, new ClientDungeonEvent { StrCmd = "OnPileChallengeStart" });
        await Next(bot, DungeonService.ClientEvent, NullArg.Parser);
        await Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, r => r.Stage == 105);
    }

    private async Task CompleteZeroDamageTraining(BotClient bot)
    {
        _clock.AdvanceSeconds(61);
        var result = await Next(bot, DungeonService.Result, DungeonResNotify.Parser, r => r.Data?.TrainingGroundData != null);
        Assert.Equal(0, result.Data.TrainingGroundData.SumDamage);
        Assert.Equal(0, result.Data.TrainingGroundData.MaxDps);
    }

    private void AssertTrainingQuestDelivery(long role, int count)
    {
        var saved = DBTaskInfo.Parser.ParseFrom(Service<RoleStore>().LoadTasks(role)!);
        Assert.Equal(count, saved.CompleteTaskNum.GetValueOrDefault(TrainingQuest));
        Assert.Equal(count, QuestProbe.AwardSourceCount(_app, role, TrainingQuest));
    }
}
