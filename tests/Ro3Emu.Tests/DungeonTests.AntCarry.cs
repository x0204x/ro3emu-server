using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    // 原服上車 124447–124457、下車 124701–124703 的協定順序；不是原生動畫驗收。
    [ClientDataFact]
    public async Task AntRideCarriesThePlayerBeforeVehicleStateAndPublishesTheCarrierAfterAcknowledgement()
    {
        await using var alice = await InWorld("Alice");
        var deniro = await PrepareAntDeniroAsync(alice, 4);
        var frames = await BoardAntWithCarryAsync(alice, deniro);
        long rider = Session(alice).Role!.Id;
        int carrierState = Array.FindIndex(frames, e => AntCarryState(e, deniro.Uid, InteractInfo.Types.EState.Vehicle));
        int carriedMove = Array.FindIndex(frames, e => e.IsGame(GameAoiSink.MovementChange)
            && MovementChangeNtf.Parser.ParseFrom(e.Body) is { Uid: var uid, TransType: MovementTransitionType.MoveTransBeCarriedMove } && uid == rider);
        int carry = Array.FindIndex(frames, AntSelfCarry);
        int playerState = Array.FindIndex(frames, e => AntCarryState(e, rider, InteractInfo.Types.EState.Vehicle));
        int reply = Array.FindIndex(frames, e => e.IsGame(SceneService.NpcInteraction)
            && NpcInteractionRsp.Parser.ParseFrom(e.Body).Req.InteractionId == 5300005);
        int carrierPassengers = Array.FindIndex(frames, e => AntCarrierAdd(e, deniro.Uid, rider));
        Assert.True(carrierState >= 0 && carrierState < carriedMove && carriedMove < carry && carry < playerState
            && playerState < reply && reply < carrierPassengers, "載具狀態、上車位移、31044、玩家狀態、32700回覆、載具乘客清單必須依序送出");
        var carried = AoIInfoSingle.Parser.ParseFrom(frames[carry].Body).CarryInfo.BeCarried;
        Assert.Equal((ulong)deniro.Uid, carried.Id);
        Assert.Equal((0, 0), (carried.CarrySlot, carried.CarryType));
        Assert.Equal(2.8f, carried.CarryOffset.Y, 3);
        Assert.Equal(0f, carried.CarryOffset.X);
        Assert.Equal(0f, carried.CarryOffset.Z);
        var passenger = Assert.Single(deniro.Entity().CarryInfo.AllCarries);
        Assert.Equal((ulong)rider, passenger.Id);
        Assert.Equal(carried.CarryOffset, passenger.CarryOffset);
    }

    [ClientDataFact]
    public async Task AntRideUnlinksOnExitCanBoardAgainAndCannotSettleTwice()
    {
        await using var alice = await InWorld("Alice");
        var deniro = await PrepareAntDeniroAsync(alice, 4);
        await BoardAntWithCarryAsync(alice, deniro);
        var session = Session(alice);
        int before = alice.Inbox.Snapshot().Count;
        await alice.SendGameAsync(WorldInteractionService.Action, new UseInteractActionReq { ButtonId = 26 });
        Assert.Equal(0, (await Next(alice, WorldInteractionService.Action, UseInteractActionRes.Parser)).Result);
        AssertAntCarryCleared(alice.Inbox.Snapshot().Skip(before).ToArray(), session.Role!.Id, deniro.Uid);
        Assert.False(Service<WorldInteractionService>().IsSeatedOnSceneActor(session));
        Assert.True(deniro.Entity().CarryInfo == null || deniro.Entity().CarryInfo.AllCarries.Count == 0);

        // 先用同一個真實座位建立已失效token，再驗證其不能卸除之後正常互動建立的新座位。
        long staleToken = 0;
        await OnLoop(async () =>
        {
            staleToken = await Service<WorldInteractionService>().SeatOnSceneActorAsync(session, deniro.Uid, deniro.NpcId, 5300005);
            Assert.NotEqual(0, staleToken);
            await Service<WorldInteractionService>().PutDownFromSceneActorAsync(session, staleToken);
        });
        await BoardAntWithCarryAsync(alice, deniro);
        await OnLoop(() => Service<WorldInteractionService>().PutDownFromSceneActorAsync(session, staleToken));
        Assert.True(Service<WorldInteractionService>().IsSeatedOnSceneActor(session));
        before = alice.Inbox.Snapshot().Count;
        await WalkInto(alice, Service<WorldData>().Map(1000301)!.Regions[31]);
        await Next(alice, DungeonService.ResultNotify, DungeonResultNotify.Parser, r => r.ResultInfo.DungeonId == 1000301);
        await StoryMovesOn(alice, 10104300, 10104310);
        await OnLoop(async () =>
        {
            await Service<DungeonService>().PlayerMovedAsync(session);
            await Service<DungeonService>().PlayerMovedAsync(session);
        });
        Assert.Single(alice.Inbox.Snapshot().Skip(before), e => e.IsGame(DungeonService.ResultNotify)
            && DungeonResultNotify.Parser.ParseFrom(e.Body).ResultInfo.DungeonId == 1000301);
        Assert.False(Service<WorldInteractionService>().IsSeatedOnSceneActor(session));
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AntRideDropsItsCarryOnAbandonOrDisconnect(bool disconnect)
    {
        await using var alice = await InWorld("Alice");
        var deniro = await PrepareAntDeniroAsync(alice, 4);
        await BoardAntWithCarryAsync(alice, deniro);
        var session = Session(alice);
        long rider = session.Role!.Id;
        int before = alice.Inbox.Snapshot().Count;
        if (disconnect)
        {
            await alice.DisposeAsync();
            await Until(() => !Service<WorldInteractionService>().IsSeatedOnSceneActor(session), "斷線後回收實際搭乘狀態");
        }
        else
        {
            await OnLoop(() => Service<DungeonService>().AbandonAsync(session));
            await Until(() => alice.Inbox.Snapshot().Skip(before).Any(e => AntCarryState(e, rider, InteractInfo.Types.EState.Idle)), "離場送出玩家Idle");
            AssertAntCarryCleared(alice.Inbox.Snapshot().Skip(before).ToArray(), rider, deniro.Uid);
        }
        Assert.False(Service<WorldInteractionService>().IsSeatedOnSceneActor(session));
        Assert.Equal(InteractInfo.Types.EState.Idle, deniro.Entity().InteractInfo.EType);
        Assert.True(deniro.Entity().CarryInfo == null || deniro.Entity().CarryInfo.AllCarries.Count == 0);
        Assert.Null(Service<DungeonService>().AntStoryMovementSpeed(session));
    }

    [ClientDataFact]
    public async Task AntRideNewObserverReceivesCarryBeforeTheRidersVehicleState()
    {
        await using var alice = await InWorld("Alice");
        var deniro = await PrepareAntDeniroAsync(alice, 4);
        await BoardAntWithCarryAsync(alice, deniro);
        await using var bob = await InWorld("Bob");
        var rider = Session(alice);
        var viewer = Session(bob);
        int before = bob.Inbox.Snapshot().Count;
        // 只放置隔離測試觀察者以觸發真正的World AOI進場；不宣稱個人任務允許組隊進入。
        await OnLoop(async () =>
        {
            await Service<World>().LeaveAsync(viewer);
            viewer.SceneEntry = rider.SceneEntry; viewer.InstanceId = rider.InstanceId; viewer.Position = rider.Position.Clone();
            await Service<World>().JoinAsync(viewer);
        });
        await Until(() => bob.Inbox.Snapshot().Skip(before).Any(e => AntCarryState(e, rider.Role!.Id, InteractInfo.Types.EState.Vehicle)), "新觀察者取得載乘狀態");
        var frames = bob.Inbox.Snapshot().Skip(before).ToArray();
        int carried = Array.FindIndex(frames, e => e.IsGame(WorldCoreService.AoiInfo)
            && AoIInfoTotal.Parser.ParseFrom(e.Body).AoiData.Any(a => a.EntityUuid == (ulong)rider.Role!.Id && a.CarryInfo?.BeCarried?.Id == (ulong)deniro.Uid));
        int state = Array.FindIndex(frames, e => AntCarryState(e, rider.Role!.Id, InteractInfo.Types.EState.Vehicle));
        Assert.True(carried >= 0 && carried < state, "新觀察者須先建立乘客掛載，再進入Vehicle狀態");
    }

    private async Task<BotEvent[]> BoardAntWithCarryAsync(BotClient bot, DungeonService.SceneActorUnit deniro)
    {
        await Beside(bot, deniro.X, deniro.Y, deniro.Z);
        int before = bot.Inbox.Snapshot().Count;
        long rider = Session(bot).Role!.Id;
        Assert.Equal(0, await Interact(bot, deniro, 5300005));
        await Until(() => bot.Inbox.Snapshot().Skip(before).Any(e => AntCarryState(e, rider, InteractInfo.Types.EState.Vehicle)), "上車後玩家Vehicle狀態");
        // 玩家Vehicle已送出，31044理應在它之前；缺包直接失敗，不用等到超時掩蓋問題。
        Assert.Single(bot.Inbox.Snapshot().Skip(before), AntSelfCarry);
        await Until(() => bot.Inbox.Snapshot().Skip(before).Any(e => AntCarrierAdd(e, deniro.Uid, rider)), "載具端新增乘客");
        return bot.Inbox.Snapshot().Skip(before).ToArray();
    }

    private static bool AntSelfCarry(BotEvent e) => e.IsGame(MsgId.SyncAoiSelf)
        && AoIInfoSingle.Parser.ParseFrom(e.Body).CarryInfo?.BeCarried is { Id: > 0 };

    private static bool AntCarryState(BotEvent e, long uid, InteractInfo.Types.EState state) => e.IsGame(WorldInteractionService.Change)
        && InteractChangeNtf.Parser.ParseFrom(e.Body) is { Uid: var actual, DataInfo: { EType: var actualState } } && actual == uid && actualState == state;

    private static bool AntCarrierAdd(BotEvent e, long carrier, long rider) => e.IsGame(WorldCoreService.AoiInfo)
        && AoIInfoTotal.Parser.ParseFrom(e.Body).AoiData.Any(a => a.EntityUuid == (ulong)carrier
            && a.CarryInfo != null && a.CarryInfo.AddCarries.Any(c => c.Id == (ulong)rider));

    private static void AssertAntCarryCleared(BotEvent[] frames, long rider, long carrier)
    {
        int carrierIdle = Array.FindIndex(frames, e => AntCarryState(e, carrier, InteractInfo.Types.EState.Idle));
        int clear = Array.FindIndex(frames, e => e.IsGame(MsgId.SyncAoiSelf)
            && AoIInfoSingle.Parser.ParseFrom(e.Body).CarryInfo?.BeCarried is { Id: 0 });
        int playerIdle = Array.FindIndex(frames, e => AntCarryState(e, rider, InteractInfo.Types.EState.Idle));
        Assert.True(carrierIdle >= 0 && carrierIdle < clear && clear < playerIdle, "下車須先清載具狀態，再送31044解除掛載，最後清玩家狀態");
        Assert.NotNull(AoIInfoSingle.Parser.ParseFrom(frames[clear].Body).CarryInfo.BeCarried.CarryOffset);
    }
}
