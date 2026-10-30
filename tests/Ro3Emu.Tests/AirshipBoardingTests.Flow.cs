using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class AirshipBoardingTests
{
    private async Task Barrier(BotClient bot)
    {
        await bot.SendGameAsync(TaskService.TaskFollow, new TaskFollowReq { Id = TaskService.FirstTask });
        await Next(bot, TaskService.TaskFollow, TaskFollowResp.Parser);
    }

    private static Task PlotEnd(BotClient bot, long plot) =>
        bot.SendGameAsync(TaskService.PlotActingEnd, new PlotActingEndReq { PlotId = plot });

    private static async Task<int> EnterAirship(BotClient bot)
    {
        // G action3000 的後兩參數不入封包，SendEnterDungeon 只送 sceneEntry。
        await bot.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = 1000501 });
        return (await Next(bot, DungeonService.Enter, EnterDungeonRsp.Parser)).Result;
    }

    private async Task Dock(BotClient bot)
    {
        await Gm("!lvl 99");
        await PrepareCargo(bot); // LAB 只準備貨物任務起點，後續以正式協定逐節推進。
        Assert.Equal(0, await CompleteCargo(bot));
        Assert.Equal(TaskState.Running, await Read(s => s.Tasks!.Infos[10103160].Status));
        var point = Service<WorldData>().Map(10101)!.Points[1010316001];
        // LAB 定位對話點位，仍由 SceneService 更新 AOI 與移動通知。
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session, point));
        await bot.SendGameAsync(SceneService.ClientNpcInteract, new ClientNpcInteractReq
        { DialogID = 1010316001, Areapointid = 1010316001 });
        await Barrier(bot);
        Assert.Equal(TaskState.Delivery, await Read(s => s.Tasks!.Infos[10103160].Status));
        Assert.Equal(TaskState.Running, await Read(s => s.Tasks!.Infos[10103170].Status));
        await bot.SendGameAsync(DungeonService.PlotSceneEvent, new PlotActingTriggerReq { PlotId = 130031 });
        await PlotEnd(bot, 130032); // 羅盤預示不是碼頭任務的 601 結束動作。
        await bot.SendGameAsync(32709, new NullArg());
        await Barrier(bot);
        Assert.Equal(TaskState.Running, await Read(s => s.Tasks!.Infos[10103170].Status));
        Assert.False(await Read(s => s.Tasks!.Infos.ContainsKey(10103210)));
        await PlotEnd(bot, 130031);
        await Barrier(bot);
        Assert.Equal(TaskState.Delivery, await Read(s => s.Tasks!.Infos[10103170].Status));
        Assert.Equal(TaskState.Running, await Read(s => s.Tasks!.Infos[10103200].Status));
    }

    private async Task BoardingDialogue(BotClient bot)
    {
        await MoveCargoMap(bot);
        var npc = Service<WorldData>().Map(10101)!.Npcs.First(n => n.NpcId == 100098);
        // LAB 定位既有 NPC；正式 31208 仍須通過伺服器場景及距離檢查。
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session, new MapPoint(npc.X, npc.Y, npc.Z, 0)));
        var request = new ClientNpcInteractReq { NpcUid = npc.Uid, DialogID = 1010320001 };
        await bot.SendGameAsync(SceneService.ClientNpcInteract, request);
        await Barrier(bot);
        Assert.Equal(TaskState.Delivery, await Read(s => s.Tasks!.Infos[10103200].Status));
        Assert.Equal(TaskState.Running, await Read(s => s.Tasks!.Infos[10103201].Status));
        await bot.SendGameAsync(SceneService.ClientNpcInteract, request);
        await Barrier(bot);
        Assert.Equal(1, await Read(s => s.Tasks!.CompleteTaskNum[10103200]));
    }

    [ClientDataFact]
    public async Task DockVisionAndCutsceneExitCannotBypassFarewellOrBoarding()
    {
        await using var bot = await InWorld();
        long role = (long)bot.Self!.Uuid;
        await Dock(bot);
        Assert.Equal(ErrorCodes.TaskRequirements, await EnterAirship(bot));
        await BoardingDialogue(bot);
        await PlotEnd(bot, 130032);
        await bot.SendGameAsync(DungeonService.PlotSceneEvent, new PlotActingTriggerReq { PlotId = 130041 });
        await bot.SendGameAsync(32709, new NullArg());
        await Barrier(bot);
        Assert.Equal(TaskState.Running, await Read(s => s.Tasks!.Infos[10103201].Status));
        Assert.Equal(ErrorCodes.TaskRequirements, await EnterAirship(bot));

        // 130041 正常播完與 _kSkip=[2,4] 的合法跳過，都經同一個 601→84011；沒有 skip 欄位。
        await PlotEnd(bot, 130041);
        await Barrier(bot);
        Assert.Equal(TaskState.Delivery, await Read(s => s.Tasks!.Infos[10103201].Status));
        Assert.Equal(TaskState.Running, await Read(s => s.Tasks!.Infos[10103210].Status));
        await PlotEnd(bot, 130041); // 共用結束包重送不得再發獎或重新接受登船任務。
        await Barrier(bot);
        Assert.Equal(1, await Read(s => s.Tasks!.CompleteTaskNum[10103201]));
        Assert.Equal(1, AwardSources(role, 10103200));
        Assert.Equal(0, await EnterAirship(bot));
        var change = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneEntry == 1000501);
        Assert.NotEqual(change.SceneEntry, change.SceneID);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 1000501 });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, m => m.SceneId == 1000501)).Result);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        await Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, m => m.DungeonId == change.SceneID);
        Assert.Equal(0, await Read(s => s.Tasks!.Infos[10103210].CompleteNum[0]));
        Assert.Equal(0, AwardSources(role, 10103210));
    }

    [ClientDataFact]
    public async Task FarewellKeepsItsOriginalInstanceAcrossReconnectAndCompletesOnce()
    {
        long role, started;
        await using (var bot = await InWorld())
        {
            role = (long)bot.Self!.Uuid;
            await Dock(bot);
            await BoardingDialogue(bot);
            started = await Read(s => s.Tasks!.Infos[10103201].StartTime);
            await bot.SendGameAsync(32709, new NullArg());
            await Barrier(bot);
            Assert.Equal(0, StoredTasks(role).CompleteTaskNum.GetValueOrDefault(10103201));
        }
        await Restart();
        await using var recovered = await InWorld(create: false);
        Assert.Equal(started, await Read(s => s.Tasks!.Infos[10103201].StartTime));
        Assert.Equal(TaskState.Running, await Read(s => s.Tasks!.Infos[10103201].Status));
        Assert.False(await Read(s => s.Tasks!.Infos.ContainsKey(10103210)));
        await MoveCargoMap(recovered);
        await PlotEnd(recovered, 130041);
        await Barrier(recovered);
        await PlotEnd(recovered, 130041);
        await Barrier(recovered);
        Assert.Equal(1, StoredTasks(role).CompleteTaskNum[10103201]);
        Assert.Equal(TaskState.Running, await Read(s => s.Tasks!.Infos[10103210].Status));
        Assert.Equal(0, AwardSources(role, 10103210));
    }
}
