using Ro3Emu.Server.Game;
using Romsg;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    /// <summary>
    /// Ro3Emu:Testing:UnlimitedRewards (a test aid): Phantom Realm 50101's weekly allowance (TenGameplayRefreshTimeConfig
    /// type 2, one a week) spent, a clear pays nothing while the switch is off and pays again once "!testing unlimited on",
    /// without spending: the 5062 counter shows nothing used.
    /// </summary>
    [ClientDataFact]
    public async Task UnlimitedRewardsPaysAClearPastTheWeeklyAllowance()
    {
        const string Key = "50101:101:1";
        SyntheticSolo(); // the exported weekly allowance; test-only solo admission
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        Assert.Equal(0, await Enter(alice));
        await Kill(alice, await BossIn(await Load(alice)));
        Assert.True((await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Rewards.ContainsKey(2)); // free first clear
        await alice.SendGameAsync(DungeonService.ClientEvent, new ClientDungeonEvent { StrCmd = "DungeonReset" });
        Assert.Equal(0, (await Next(alice, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        await Next(alice, DungeonService.ClientEvent, NullArg.Parser);
        await Kill(alice, await BossIn(await Load(alice)));
        Assert.True((await Next(alice, DungeonService.Result, DungeonResNotify.Parser, r => r.DungeonEntry == Scene)).Rewards.ContainsKey(2));
        Assert.Equal(0, Session(alice).Dungeons!.Allowances[Key].Remaining); // the week's allowance is spent

        // Off: the clear is won but pays no personal award.
        Assert.Equal(0, await Enter(alice));
        await Kill(alice, await BossIn(await Load(alice)));
        var spent = await Next(alice, DungeonService.Result, DungeonResNotify.Parser);
        Assert.Equal((DungeonRes)1, spent.Res);
        Assert.False(spent.Rewards.ContainsKey(2));
        long zeny = Currency(alice, 2);

        await Gm(alice, "!testing unlimited on");
        var counter = ((DBMultiDungeonInfo)Session(alice).Synced[DungeonService.Sync]).DungeonInfo[Multi].AwardInfo[0];
        Assert.Equal(0, counter.UseNumber);
        Assert.Equal(0, await Enter(alice));
        await Kill(alice, await BossIn(await Load(alice)));
        var paid = await Next(alice, DungeonService.Result, DungeonResNotify.Parser);
        Assert.Equal((DungeonRes)1, paid.Res);
        Assert.Equal(90000, paid.Rewards[2]);
        Assert.Equal(zeny + 90000, Currency(alice, 2));
        Assert.Equal(0, Session(alice).Dungeons!.Allowances[Key].Remaining); // paid without spending
    }
}
