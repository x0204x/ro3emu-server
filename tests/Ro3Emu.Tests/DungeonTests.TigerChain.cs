using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    [ClientDataFact]
    public async Task TigerKingPeltDialogueCityJumpAndLaterPlotUseTheirOwnTaskGates()
    {
        const long dungeon = 1022001, field = 10220, city = 10200;
        const long clear = 10102200, pelt = 10102210, dialogue = 10102220, plotTask = 10102230;
        const long peltPoint = 1010221001, dialoguePoint = 1010222001, cityPoint = 1010222101;
        const int peltInteraction = 40022001, dialogueId = 1010222001, plot = 130121;

        var data = Service<GameData>();
        var peltRow = data.Row("TaskConfig", pelt)!.Value;
        var dialogueRow = data.Row("TaskConfig", dialogue)!.Value;
        var plotRow = data.Row("PlotInterpretation", plot)!.Value;
        Assert.Equal("345", peltRow.GetProperty("_kEXECAction")[0][0].GetString());
        Assert.Equal("1253018", peltRow.GetProperty("_kEXECAction")[0][1].GetString());
        Assert.Equal("141", dialogueRow.GetProperty("_kEXECAction")[0][0].GetString());
        Assert.Equal(city.ToString(), dialogueRow.GetProperty("_kEXECAction")[0][1].GetString());
        Assert.Equal(cityPoint.ToString(), dialogueRow.GetProperty("_kEXECAction")[0][2].GetString());
        Assert.Equal(city, plotRow.GetProperty("_kTrigger")[0].GetInt64());
        Assert.Equal(plotTask, plotRow.GetProperty("_kTrigger")[2].GetInt64());

        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99"); // LAB 準備角色；任務進度仍由正式協定觸發。
        await Gm(alice, $"!quest accept {clear}");
        await MovePublic(alice, field);
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = dungeon }));
        long instance = await Load(alice, dungeon);
        await ClearReconstructedStoryRun(alice, instance);
        Assert.Equal(1, (await Next(alice, DungeonService.ResultNotify, DungeonResultNotify.Parser)).ResultInfo.WinCamp);
        await StoryMovesOn(alice, clear, pelt);

        // 副本通關後先回公共斐揚森林，虎皮互動與後續對話皆在該地圖。
        await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, sw => sw.SceneEntry == field);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = field });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        var peltAt = Service<WorldData>().Map(field)!.Points[peltPoint];
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(alice), peltAt));
        await alice.SendGameAsync(TaskService.TriggerTaskEvent,
            new TriggerTaskEventReq { TaskId = peltInteraction, GoalCount = 1 });
        Assert.Equal(0, (await Next(alice, TaskService.TriggerTaskEvent, TriggerTaskEventResp.Parser)).Result);
        await StoryMovesOn(alice, pelt, dialogue);

        var dialogueAt = Service<WorldData>().Map(field)!.Points[dialoguePoint];
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(alice), dialogueAt));
        await alice.SendGameAsync(SceneService.ClientNpcInteract,
            new ClientNpcInteractReq { DialogID = dialogueId, Areapointid = dialoguePoint });
        await StoryMovesOn(alice, dialogue, plotTask);

        // 141 是客戶端動作：對話任務完成後，客戶端才送 30131 前往城中。
        await alice.SendGameAsync(SceneService.CustomJumpMap, new ClientCustomJumpMapReq
        {
            SceneId = city, PointId = cityPoint, CustomType = (EClientCustomJumpMapType)2, TypeParam1 = dialogue,
        });
        Assert.Equal(0, (await Next(alice, SceneService.CustomJumpMap, ClientCustomJumpMapResp.Parser)).Result);
        await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, sw => sw.SceneEntry == city);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = city });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);

        // 130121 屬城中下一個任務的劇情，和虎王遺體互動是不同的結算點。
        await alice.SendGameAsync(TaskService.ExitCutSceneState, new ExitCutSceneStateReq());
        await alice.SendGameAsync(TaskService.PlotActingEnd, new PlotActingEndReq { PlotId = plot });
        await StoryMovesOn(alice, plotTask, 10102240);
    }
}
