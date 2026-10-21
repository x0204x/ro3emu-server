using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    private async Task<TaskDeliveryResp> Claim(BotClient bot, long task)
    {
        await bot.SendGameAsync(TaskService.TaskDelivery, new TaskDeliveryReq { Req = { new TaskDeliverySingleReq { Id = task } } });
        return await Next(bot, TaskService.TaskDelivery, TaskDeliveryResp.Parser);
    }

    /// <summary>
    /// 106602 CheckTenBOSSWin counts a committed clear of a listed TenGameplay boss: growth emblem title 3 (703990) then
    /// completes through its child 703030 "Clear Wildlands dungeons" [106602,1,4300001,..]. The guild co-op quest 1601010
    /// needs a fellow guild member in the clearing party, so a solo clear leaves it at 0.
    /// </summary>
    [ClientDataFact]
    public async Task ATenGameplayBossClearCountsForTheGrowthTitleButNotForAGuildCoopQuestAlone()
    {
        SyntheticSolo(); // exported 50101 requires three; only this host's cached admission is relaxed
        var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest done 701990");
        await Gm(alice, "!quest done 702990");
        await QuestProbe.AtAsync(_app, "alice", 703030, 0); // title 3 offered
        foreach (long child in new long[] { 703010, 703020, 703040, 703050, 703060, 703070 }) await Gm(alice, $"!quest done {child}");
        await QuestProbe.AcceptAsync(_app, _http, "alice", 1601010); // [Guild Co-op] Phantom Realm, same boss list
        await QuestProbe.AtAsync(_app, "alice", 1601010, 0);

        Assert.Equal(0, await Enter(alice));
        long instance = await Load(alice);
        await Kill(alice, await BossIn(instance));
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        await QuestProbe.DoneAsync(_app, "alice", 703030);
        Assert.Equal(0, (await QuestProbe.ReadAsync(_app, "alice", 1601010))!.CompleteNum[0]);
        await alice.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);

        await alice.DisposeAsync();
        await QuestProbe.OfflineAsync(_app, "alice");
        await using var relog = await InWorld("Alice", create: false);
        await QuestProbe.DoneAsync(_app, "alice", 703030); // the event count survives the relogin
        Assert.Single((await Claim(relog, 703030)).Data);
        Assert.Single((await Claim(relog, 703990)).Data);
        await QuestProbe.WaitAsync(_app, "alice", 703990, i => i.Status == TaskState.Delivery, "delivered");
        Assert.True(TaskService.IsDelivered(Session(relog), 703990));
        await QuestProbe.AtAsync(_app, "alice", 704020, 0); // title 4 is offered next
    }
}
