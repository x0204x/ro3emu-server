using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    [ClientDataFact]
    public async Task TigerKingCommittedClearSyncsQuestThenResultAndReturnsWithoutClientLeaveRequest()
    {
        const long scene = 1022001, task = 10102200, field = 10220;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Gm(alice, $"!quest accept {task}");
        await MovePublic(alice, field);
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        int before = alice.Inbox.Snapshot().Count;
        using var left = alice.Inbox.Expect(e => e.IsGame(DungeonService.Leave));
        await ClearReconstructedStoryRun(alice, instance);
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);

        var events = alice.Inbox.Snapshot().Skip(before).ToArray();
        int questSync = Array.FindIndex(events, e => e.IsGame(TaskService.TaskInfoSync));
        int result = Array.FindIndex(events, e => e.IsGame(DungeonService.Result));
        Assert.True(questSync >= 0 && result > questSync, "committed quest 5027 must precede 30403");
        Assert.True(TaskService.IsDelivered(Session(alice), task));
        // 採上游單人 plane 的正式 30402 退場通知；本案沒有送出玩家離場請求。
        var leave = await left.WaitAsync(TimeSpan.FromSeconds(DungeonService.PlaneExitSeconds + 10));
        Assert.Equal(0, LeaveDungeonRsp.Parser.ParseFrom(leave.Body).Result);
        var back = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser,
            sw => sw.SceneEntry == field);
        Assert.Equal(field, back.SceneEntry);
        Assert.Equal(0, Session(alice).InstanceId);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = field });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        Assert.Single(alice.Inbox.Snapshot(), e => e.IsGame(DungeonService.Leave));
    }

    [ClientDataFact]
    public async Task TigerKingManual30402BeforeAutoReturnDoesNotTransferTwice()
    {
        const long scene = 1022001;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10102200");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        await ClearReconstructedStoryRun(alice, instance);
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        await alice.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        var back = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser,
            sw => sw.SceneEntry == RoleStore.SpawnSceneEntry);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = back.SceneEntry });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        int transfers = alice.Inbox.Snapshot().Count(e => e.IsGame(MsgId.SwitchSceneLine));
        await Task.Delay(TimeSpan.FromSeconds(DungeonService.PlaneExitSeconds + 1));
        Assert.Equal(transfers, alice.Inbox.Snapshot().Count(e => e.IsGame(MsgId.SwitchSceneLine)));
        Assert.Equal(0, Session(alice).InstanceId);
    }
}
