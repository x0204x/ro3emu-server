using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    [ClientDataFact]
    public async Task AntCommittedResultChainsToDeniroWithoutAClientEnterRequest()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!quest accept 10104290");
        await MovePublic(alice, 10514);
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = 1000201 }));
        await Load(alice, 1000201);
        await Stage(alice, 1, quest: 1);
        await Gm(alice, "!actors use");
        await Stage(alice, 2, quest: 2);
        var vortex = Assert.Single(await ActorsIn(alice, 1, a => a.NpcId == 5300030));
        await Beside(alice, vortex.X, vortex.Y, vortex.Z);
        Assert.Equal(0, await Interact(alice, vortex, 5300030));
        await Stage(alice, 3, quest: 3);
        var before = alice.Inbox.Snapshot().Count;
        await WalkInto(alice, Service<WorldData>().Map(1000201)!.Regions[22]);
        var result = await Next(alice, DungeonService.ResultNotify, DungeonResultNotify.Parser);
        Assert.Equal(1000201, result.ResultInfo.DungeonId);
        Assert.Equal(1, result.ResultInfo.WinCamp);
        Assert.True(TaskService.IsDelivered(Session(alice), 10104290));
        var changed = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneEntry == 1000301);
        Assert.Equal(1000301, changed.SceneEntry);
        var current = Session(alice).DungeonAttempt!;
        Assert.NotNull(current);
        Assert.Equal(10514, Assert.Single(current.Room.Participants).ReturnScene);
        var events = alice.Inbox.Snapshot().Skip(before).ToArray();
        Assert.DoesNotContain(events, e => e.IsGame(DungeonService.Enter) || e.IsGame(DungeonService.Leave)
            || e.IsGame(DungeonService.Result));
        Assert.Single(events, e => e.IsGame(DungeonService.ResultNotify));
    }
}
