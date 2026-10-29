using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Survivor Battle in the match, point 5: the unit custom data, follow, spectating, the shops, the encounters
/// and the weekly tasks (TimedBattleService.ChickenLook/Follow/Watch/Shop/Adventure, TaskService.ChickenTasks).</summary>
public sealed partial class SurvivorBattleTests
{
    private static long Custom(CustomDataChange change, int id) =>
        change.CustomDatas.Single(d => d.Id == id).CustomParam.LongParam;

    /// <summary>
    /// The career model, the wheel emote and the head sign ride on the entity; the Kafra shop is paid in Kaka Coin from
    /// the match pack; a Magic Die offers encounter cards through 5039 / 83421 / 83422.
    /// </summary>
    [ClientDataFact]
    public async Task CareerLookShopAndEncountersUseTheMatch()
    {
        await HostAsync(2, 1);
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var players = new[] { alice, bob };
        var stage0 = await StartAsync(players);
        await alice.SendGameAsync(TimedBattleService.SelectCareer, new SelectRoleCareerReq { Career = 103 });
        Assert.Equal(0, (await Next(alice, TimedBattleService.SelectCareer, SelectRoleCareerResp.Parser)).Result);
        // Chicken_PlayerCareer on Alice's entity swaps her model for the career's _iUnitB/_iUnitG, for everyone.
        var look = await Next(bob, WorldCoreService.CustomChange, CustomDataChange.Parser,
            c => (long)c.EntityId == Id(alice) && c.CustomDatas.Any(d => d.Id == TimedBattleService.CustomCareer));
        Assert.Equal(103, Custom(look, TimedBattleService.CustomCareer));
        await BattleAsync(players, stage0);

        // The wheel: edited with 43304 (no repeats), and only an emote of it plays, as a bubble on the entity.
        await alice.SendGameAsync(EmojiService.DungeonEmojiInstall, new DungeonEmojiInstallReq { DungeonId = 0, EmojiIds = { 1002, 1001 } });
        Assert.Equal(0, (await Next(alice, EmojiService.DungeonEmojiInstall, DungeonEmojiInstallResp.Parser)).Result);
        await alice.SendGameAsync(EmojiService.DungeonEmojiInstall, new DungeonEmojiInstallReq { DungeonId = 0, EmojiIds = { 1001, 1001 } });
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, EmojiService.DungeonEmojiInstall, DungeonEmojiInstallResp.Parser)).Result);
        await OnLoop(() =>
        {
            Assert.Equal([1002, 1001, 0, 0, 0, 0], Session(alice).EmojiInfo!.DungeonEmojiInstalls[0].EmojiIds);
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(TimedBattleService.EmojiPlay, new DungeonModuleEmojiPlayReq { EmojiId = 1003 });
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, TimedBattleService.EmojiPlay, DungeonModuleEmojiPlayResp.Parser)).Result);
        await alice.SendGameAsync(TimedBattleService.EmojiPlay, new DungeonModuleEmojiPlayReq { EmojiId = 1002 });
        Assert.Equal(0, (await Next(alice, TimedBattleService.EmojiPlay, DungeonModuleEmojiPlayResp.Parser)).Result);
        var bubble = await Next(bob, WorldCoreService.CustomChange, CustomDataChange.Parser,
            c => c.CustomDatas.Any(d => d.Id == TimedBattleService.CustomEmoji));
        Assert.Equal([1002, _clock.GetUtcNow().ToUnixTimeSeconds()], bubble.CustomDatas.Single().CustomParam.ListLongParam);

        // Kaka Coin into the pack, then the shop.
        long coins = 0;
        await OnLoop(async () => coins = await Battles.PlaceChestForTestAsync(Session(alice), 15000501, (10006, 6000)));
        await alice.SendGameAsync(TimedBattleService.PickupAll, new PickUpAllChickenItemReq { NpcIds = { coins } });
        Assert.Equal(0, (await Next(alice, TimedBattleService.PickupAll, PickUpAllChickenItemResp.Parser)).Result);
        var shops = Battles.ShopsForTest(Session(alice));
        Assert.Contains(shops, s => s.NpcId == 15000060);
        var kafra = shops.First(s => s.NpcId == 15000054);
        Assert.Equal(4 + 6 + 3 + 5 + 15, kafra.Goods.Length); // ShopConfig 27-29 _kGroup picks
        Assert.Contains(1011001L, kafra.Goods); // group 1001 has exactly its 4 rows
        await MoveAsync(alice, kafra.X, kafra.Z);
        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq { NpcUid = kafra.Uid });
        Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        await alice.SendGameAsync(TimedBattleService.OpenPanel, new ChickenOpenPanelReq { PanelNum = EChickenOpenPanel.Shop, IsOpen = true });
        Assert.Equal(EChickenOpenPanel.Shop, (await Next(alice, TimedBattleService.OpenPanel, ChickenOpenPanelResp.Parser)).Req.PanelNum);
        var sign = await Next(bob, WorldCoreService.CustomChange, CustomDataChange.Parser,
            c => c.CustomDatas.Any(d => d.Id == TimedBattleService.CustomShopSign));
        Assert.Equal(1, Custom(sign, TimedBattleService.CustomShopSign));
        await alice.SendGameAsync(TimedBattleService.DungeonShop, new GetDungeonShopReq { NpcGuid = kafra.Uid });
        var stock = await Next(alice, TimedBattleService.DungeonShop, GetDungeonShopResp.Parser);
        Assert.Equal(kafra.Goods.Length, stock.CommodityInfo.Count);
        Assert.Equal((30L, 30L), (stock.CommodityInfo[1011001].RemainCount, stock.CommodityInfo[1011001].CanBuyCount));
        await alice.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = 1011001, Count = 2, Price = 1000, NpcId = kafra.Uid });
        var bought = await Next(alice, ShopService.BuyCommodity, BuyCommodityResp.Parser);
        Assert.Equal((0, 2, 2), (bought.Result, bought.Count, bought.BuyTimes));
        var after = await Next(alice, TimedBattleService.DungeonShop, GetDungeonShopResp.Parser);
        Assert.Equal((28L, 28L), (after.CommodityInfo[1011001].RemainCount, after.CommodityInfo[1011001].CanBuyCount));
        await alice.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = 1011001, Count = 29, Price = 1000, NpcId = kafra.Uid });
        Assert.Equal(ErrorCodes.PurchaseLimit, (await Next(alice, ShopService.BuyCommodity, BuyCommodityResp.Parser)).Result);
        await OnLoop(() =>
        {
            var pack = Round(alice).PackInfos.Values.Where(i => i.ItemEntry != 0).ToArray();
            Assert.Equal(4000, pack.Where(i => i.ItemEntry == 10006).Sum(i => i.Stack));
            Assert.Equal(2, pack.Where(i => i.ItemEntry == 10001).Sum(i => i.Stack));
            return Task.CompletedTask;
        });

        // A Magic Die: three cards to choose one from, a paid re-roll, the choice, and the die is gone.
        var dice = Battles.DiceForTest(Session(alice));
        Assert.True(dice.Count >= 2);
        await MoveAsync(alice, dice[0].X, dice[0].Z);
        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq { NpcUid = dice[0].Uid });
        Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        var full = await Next(alice, TimedBattleService.AdventureSync, DBAdventureInfoIncrement.Parser, a => a.SyncAllMark);
        Assert.Equal(1003, full.CurActiveFunctionId);
        long operateId = 0;
        await OnLoop(() =>
        {
            var info = (DBAdventureInfo)Session(alice).Synced[TimedBattleService.AdventureSync];
            var offer = info.FunctionInfos[1003].Deck.Values.Single().NeedSelectMap.Values.Single();
            Assert.Equal((1003, 1, 0), (offer.PoolId, offer.ChooseCount, offer.RefreshCount));
            Assert.Equal([0, 1, 2], offer.Cards.Keys.Order());
            Assert.Equal(_clock.GetUtcNow().ToUnixTimeSeconds() + 10, offer.EndTime);
            operateId = offer.OperateId;
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(TimedBattleService.AdventureRefresh, new AdventureRefreshReq { OperateId = operateId });
        Assert.Equal(0, (await Next(alice, TimedBattleService.AdventureRefresh, AdventureRefreshResp.Parser)).Result);
        await Next(alice, TimedBattleService.AdventureSync, DBAdventureInfoIncrement.Parser, a => !a.SyncAllMark);
        long card = 0;
        await OnLoop(() =>
        {
            Assert.Equal(3000, Round(alice).PackInfos.Values.Where(i => i.ItemEntry == 10006).Sum(i => i.Stack)); // re-roll 1000
            var info = (DBAdventureInfo)Session(alice).Synced[TimedBattleService.AdventureSync];
            var offer = info.FunctionInfos[1003].Deck.Values.Single().NeedSelectMap.Values.Single();
            Assert.Equal(1, offer.RefreshCount);
            card = offer.Cards[0];
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(TimedBattleService.AdventureSelect, new AdventureEventSelectReq { OperateId = operateId, CardId = { card } });
        Assert.Equal(0, (await Next(alice, TimedBattleService.AdventureSelect, AdventureEventSelectResp.Parser)).Result);
        await Next(alice, TimedBattleService.AdventureSync, DBAdventureInfoIncrement.Parser, a => !a.SyncAllMark);
        await OnLoop(() =>
        {
            var info = (DBAdventureInfo)Session(alice).Synced[TimedBattleService.AdventureSync];
            var deck = info.FunctionInfos[1003].Deck.Values.Single();
            Assert.Empty(deck.NeedSelectMap);
            Assert.Equal(card, deck.Cards[0].CardId);
            Assert.DoesNotContain(Battles.DiceForTest(Session(alice)), d => d.Uid == dice[0].Uid);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Two teams of two: landing follow (82628 / 82673 / 82674), the follower landing at its leader's pick, spectating a
    /// fighting teammate after an elimination (82675, custom data 1002 to the watcher only), and the weekly tasks.
    /// </summary>
    [ClientDataFact]
    public async Task FollowSpectateAndWeeklyTasks()
    {
        await HostAsync(4, 2);
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var cara = await InWorld("Cara");
        await using var dave = await InWorld("Dave");
        var players = new[] { alice, bob, cara, dave };
        await OnLoop(async () =>
        {
            // Survivor Battle is open at level 99: the week's tasks the rank allows (2507 needs rank 13 or more).
            await Service<TaskService>().RefreshAsync(Session(alice));
            var tasks = Session(alice).Tasks!.Infos;
            Assert.Equal(TaskState.Running, tasks[2501].Status);
            Assert.Equal(TaskState.Running, tasks[2502].Status);
            Assert.False(tasks.ContainsKey(2507));
        });
        var stage0 = await StartAsync(players);
        var landing = await NextStageAsync(players, await NextStageAsync(players, stage0));
        // The landing stage opens with the team's (empty) relation: the map's Follow buttons wait for it.
        var opening = await Next(alice, TimedBattleService.FollowNotify, ChickenSelectFollowResp.Parser);
        Assert.Equal(0, opening.FollowedMap[0]);

        await alice.SendGameAsync(TimedBattleService.SelectFollow, new SelectFollowReq { Type = EChickenFollowNotifyType.Followed, RoleId = Id(alice), FollowedRoleId = Id(bob) });
        Assert.Equal(0, (await Next(alice, TimedBattleService.SelectFollow, SelectFollowResp.Parser)).Result);
        var relation = await Next(bob, TimedBattleService.FollowNotify, ChickenSelectFollowResp.Parser, r => r.FollowedMap.ContainsKey(Id(alice)));
        Assert.Equal(Id(bob), relation.FollowedMap[Id(alice)]);
        Assert.Equal(0, relation.InviteCd[0]); // never an empty map
        // Bob cannot follow Alice back (same follow group), nor an enemy.
        await bob.SendGameAsync(TimedBattleService.SelectFollow, new SelectFollowReq { Type = EChickenFollowNotifyType.Followed, RoleId = Id(bob), FollowedRoleId = Id(alice) });
        Assert.Equal(ErrorCodes.Parameter, (await Next(bob, TimedBattleService.SelectFollow, SelectFollowResp.Parser)).Result);
        await bob.SendGameAsync(TimedBattleService.SelectFollow, new SelectFollowReq { Type = EChickenFollowNotifyType.Followed, RoleId = Id(bob), FollowedRoleId = Id(cara) });
        Assert.Equal(ErrorCodes.Parameter, (await Next(bob, TimedBattleService.SelectFollow, SelectFollowResp.Parser)).Result);
        // An invite needs a landing pick first; then Dave is invited (roleId 0 = every teammate), once per cooldown.
        await cara.SendGameAsync(TimedBattleService.SelectFollow, new SelectFollowReq { Type = EChickenFollowNotifyType.Invite, RoleId = 0, FollowedRoleId = Id(cara) });
        Assert.Equal(ErrorCodes.Parameter, (await Next(cara, TimedBattleService.SelectFollow, SelectFollowResp.Parser)).Result);
        await cara.SendGameAsync(TimedBattleService.SelectPosition, new SelectRolePositionReq { PositionX = 500, PositionY = 500 });
        Assert.Equal(0, (await Next(cara, TimedBattleService.SelectPosition, SelectRolePositionResp.Parser)).Result);
        await cara.SendGameAsync(TimedBattleService.SelectFollow, new SelectFollowReq { Type = EChickenFollowNotifyType.Invite, RoleId = 0, FollowedRoleId = Id(cara) });
        Assert.Equal(0, (await Next(cara, TimedBattleService.SelectFollow, SelectFollowResp.Parser)).Result);
        Assert.Equal(Id(cara), (await Next(dave, TimedBattleService.InviteFollowNotify, ChickenInviteFollowNotify.Parser)).FollowedRoleId);
        await cara.SendGameAsync(TimedBattleService.SelectFollow, new SelectFollowReq { Type = EChickenFollowNotifyType.Invite, RoleId = Id(dave), FollowedRoleId = Id(cara) });
        Assert.Equal(1211, (await Next(cara, TimedBattleService.SelectFollow, SelectFollowResp.Parser)).Result);

        await bob.SendGameAsync(TimedBattleService.SelectPosition, new SelectRolePositionReq { PositionX = 300, PositionY = 400 });
        Assert.Equal(0, (await Next(bob, TimedBattleService.SelectPosition, SelectRolePositionResp.Parser)).Result);
        await alice.SendGameAsync(TimedBattleService.SelectPosition, new SelectRolePositionReq { PositionX = 600, PositionY = 600 });
        Assert.Equal(0, (await Next(alice, TimedBattleService.SelectPosition, SelectRolePositionResp.Parser)).Result);
        var battle = await NextStageAsync(players, landing);
        await OnLoop(() =>
        {
            var a = Session(alice); // she follows Bob: his pick wins over her own
            Assert.True(Math.Abs(a.Position.X - 300) < 10 && Math.Abs(a.Position.Z - 400) < 10, $"landed at {a.Position}");
            return Task.CompletedTask;
        });

        // Past the free revive Alice is out: the countdown, then her camera follows Bob, and she shows as Watch.
        await PastFreeReviveAsync(players, battle);
        await KillAsync(alice);
        var watch = await Next(alice, TimedBattleService.WatchNotify, ChickenWatchNotify.Parser);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeSeconds() + 10, watch.AutoWatchTime);
        await Tick(watch.AutoWatchTime + 1);
        var camera = await Next(alice, WorldCoreService.CustomChange, CustomDataChange.Parser,
            c => c.CustomDatas.Any(d => d.Id == TimedBattleService.CustomCamera));
        Assert.Equal(Id(bob), Custom(camera, TimedBattleService.CustomCamera));
        await OnLoop(() =>
        {
            Assert.Equal(RoundRoleStatus.Watch, Round(bob).TeamMemberInfo[Id(alice)].State);
            Assert.False(Session(alice).WorldState.CustomData.ContainsKey(TimedBattleService.CustomCamera)); // not on her AOI entity
            Assert.True(Session(alice).WorldState.CustomData.ContainsKey(TimedBattleService.CustomCareer));
            Assert.Empty(Service<Ro3Emu.Server.Maps.World>().Viewers(Session(alice))); // a watcher is seen by nobody
            Session(bob).Position = new Vec3 { X = 520, Y = Session(bob).Position.Y, Z = 520 }; // Bob runs off
            return Task.CompletedTask;
        });
        // Her view follows Bob, so his surroundings stay in her area of interest.
        await Tick(watch.AutoWatchTime + 2);
        await OnLoop(() =>
        {
            var a = Session(alice);
            Assert.True(Math.Abs(a.Position.X - 520) < 1 && Math.Abs(a.Position.Z - 520) < 1, $"view at {a.Position}");
            return Task.CompletedTask;
        });

        // Bob's team wins: the weekly "top 5" target counts the match, and the match's look leaves with the room.
        var settled = new TaskCompletionSource();
        Battles.SettlementCompletedForTest = () => settled.TrySetResult();
        await KillAsync(cara);
        await KillAsync(dave);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await OnLoop(async () =>
        {
            await Service<TaskService>().RefreshAsync(Session(bob));
            Assert.Equal(1, Session(bob).Tasks!.Infos[2501].CompleteNum[0]);
            Assert.False(Session(bob).WorldState.CustomData.ContainsKey(TimedBattleService.CustomCareer));
        });
    }
}
