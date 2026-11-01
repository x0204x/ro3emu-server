using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    /// <summary>反向場景的徒步也走模型表面；入場及下車後不能被地面導航格困住。</summary>
    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AntSurfaceWalkingWorksOnArrivalAndAfterLeavingTheSeat(bool afterSeat)
    {
        await using var alice = await InWorld("Alice");
        Service<IOptions<ServerOptions>>().Value.WalkCheck = "enforce";
        var deniro = await PrepareAntDeniroAsync(alice, afterSeat ? 4 : 0);
        var session = Session(alice);
        if (afterSeat)
        {
            await Beside(alice, deniro.X, deniro.Y, deniro.Z);
            Assert.Equal(0, await Interact(alice, deniro, 5300005));
            await alice.SendGameAsync(WorldInteractionService.Action, new UseInteractActionReq { ButtonId = 26 });
            Assert.Equal(0, (await Next(alice, WorldInteractionService.Action, UseInteractActionRes.Parser)).Result);
        }
        Assert.Null(Service<DungeonService>().AntStoryMovementSpeed(session));
        Assert.False(Service<WorldInteractionService>().IsSeatedOnSceneActor(session));
        var start = await OnLoopValue(() => session.Position.Clone());
        await alice.WalkAsync(start.X + .5f, start.Y, start.Z, stop: true);
        await alice.HeartbeatAsync();
        Assert.Equal(start.X + .5f, await OnLoopValue(() => session.Position.X), 3);
        Assert.Equal(0, Service<CheatMonitor>().LoggedCount(alice.Account!, CheatMonitor.Walk));

        // 表面導航例外不代表可瞬移，亦不能穿過已知空氣牆。
        var scenes = Service<SceneService>();
        await OnLoop(async () =>
        {
            var near = session.Position.Clone();
            near.X += .5f;
            var previous = scenes.WalkBlocked;
            try
            {
                scenes.WalkBlocked = (_, _, _) => true;
                Assert.False(await scenes.AcceptWalkAsync(session, new WalkReq { Pos = near }));
            }
            finally { scenes.WalkBlocked = previous; }
            var far = session.Position.Clone();
            far.X += 500;
            Assert.False(await scenes.AcceptWalkAsync(session, new WalkReq { Pos = far }));
        });
    }
}
