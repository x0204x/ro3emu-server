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
        Service<Microsoft.Extensions.Options.IOptions<ServerOptions>>().Value.ScriptedScenePacePercent = 10;
        const long scene = 1022001, task = 10102200, field = 10220;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Gm(alice, $"!quest accept {task}");
        await MovePublic(alice, field);
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        int before = alice.Inbox.Snapshot().Count;
        await ClearReconstructedStoryRun(alice, instance);
        Assert.Equal(1, (await Next(alice, DungeonService.ResultNotify, DungeonResultNotify.Parser)).ResultInfo.WinCamp);

        var events = alice.Inbox.Snapshot().Skip(before).ToArray();
        int questSync = Array.FindIndex(events, e => e.IsGame(TaskService.TaskInfoSync));
        int result = Array.FindIndex(events, e => e.IsGame(DungeonService.ResultNotify));
        Assert.True(questSync >= 0 && result > questSync, "committed quest 5027 must precede 30132");
        Assert.True(TaskService.IsDelivered(Session(alice), task));
        var back = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser,
            sw => sw.SceneEntry == field);
        Assert.Equal(field, back.SceneEntry);
        Assert.Equal(0, Session(alice).InstanceId);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = field });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(DungeonService.Leave));
        var shouts = alice.Inbox.Snapshot().Where(e => e.IsGame(DungeonService.Event))
            .Select(e => DungeonEventNotify.Parser.ParseFrom(e.Body)).Where(e => e.CliFuncname == "Shouting")
            .Select(e => e.Params[0].IntParams[0]).ToArray();
        Assert.Equal(new long[] { 5300012, 5300013 }, shouts);
        Assert.Single(alice.Inbox.Snapshot(), e => e.IsGame(DungeonService.ResultNotify));
    }

    [ClientDataFact]
    public async Task TigerKingManual30402BeforeAutoReturnDoesNotTransferTwice()
    {
        Service<Microsoft.Extensions.Options.IOptions<ServerOptions>>().Value.ScriptedScenePacePercent = 10;
        const long scene = 1022001;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10102200");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        await ClearReconstructedStoryRun(alice, instance);
        Assert.Equal(1, (await Next(alice, DungeonService.ResultNotify, DungeonResultNotify.Parser)).ResultInfo.WinCamp);
        await alice.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        var back = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser,
            sw => sw.SceneEntry == RoleStore.SpawnSceneEntry);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = back.SceneEntry });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        int transfers = alice.Inbox.Snapshot().Count(e => e.IsGame(MsgId.SwitchSceneLine));
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(transfers, alice.Inbox.Snapshot().Count(e => e.IsGame(MsgId.SwitchSceneLine)));
        Assert.Equal(0, Session(alice).InstanceId);
    }
}
