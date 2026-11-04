using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// 82587 first-clear rewards (TenGameplayBossConfig._iPartAward) on the synthetic set: TenGameplayConfig 9501 (Normal)
/// and 9502 (Hard) share boss slots 901 and 902. Boss 1 pays 30 (Normal) or 40 (Hard) of currency 102; boss 2 has none.
/// 9503 (Normal, locked by a condition the role does not meet) and 9504 (Hard) share slot 903 and pay the same.
/// </summary>
public sealed class FirstPassAwardTests
{
    private const int Normal = 9501, Hard = 9502, LockedNormal = 9503, OpenHard = 9504, Currency = 102;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static GameSession Session(SyntheticHost host, BotClient bot)
        => host.Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    private static Task OnLoop(SyntheticHost host, Func<Task> work) => host.Service<GameLoop>().InvokeAsync(work);

    private static async Task<DBMultiDungeonAwardInfo> AwardInfo(SyntheticHost host, BotClient bot, int multi, int mark)
    {
        DBMultiDungeonInfo info = null!;
        await OnLoop(host, () => { info = (DBMultiDungeonInfo)Session(host, bot).Synced[DungeonService.Sync]; return Task.CompletedTask; });
        return info.DungeonInfo[multi].AwardInfo[mark - 1];
    }

    private static async Task<long> Count(SyntheticHost host, BotClient bot)
    {
        long count = 0;
        await OnLoop(host, () => { count = host.Service<CharacterService>().CountOf(Session(host, bot), Currency); return Task.CompletedTask; });
        return count;
    }

    private static async Task<int> Claim(BotClient bot, int multi, int mark)
    {
        using var reply = bot.Inbox.Expect(e => e.IsGame(DungeonService.ClaimFirstPass));
        await bot.SendGameAsync(DungeonService.ClaimFirstPass, new ClaimFirstPassAwardReq { MultiDungeonId = multi, MarkId = mark });
        return ClaimFirstPassAwardResp.Parser.ParseFrom((await reply.WaitAsync(Wait)).Body).Result;
    }

    /// <summary>Records the clear the way a settlement does (Passed), as for a role cleared before the reward was paid.</summary>
    private static Task Cleared(SyntheticHost host, BotClient bot, int multi, int mark) => OnLoop(host, async () =>
    {
        var session = Session(host, bot);
        session.Dungeons!.Records[$"{multi}:{mark}"] = new DungeonService.Record { Passed = true, FastestMs = 60_000 };
        await host.Service<DungeonService>().SyncAsync(session);
    });

    [Fact]
    public async Task AClearedBossPaysItsFirstClearRewardOnceAndHardAlsoPaysNormal()
    {
        await using var host = await SyntheticHost.StartAsync();
        var bot = await host.InWorldAsync("Clearer");
        await using (bot)
        {
            Assert.Equal(DungeonService.FirstAwardNone, (await AwardInfo(host, bot, Hard, 1)).IsFirst);
            Assert.Equal(ErrorCodes.Parameter, await Claim(bot, Hard, 1)); // not cleared yet

            await Cleared(host, bot, Hard, 1);
            Assert.Equal(DungeonService.FirstAwardComplete, (await AwardInfo(host, bot, Hard, 1)).IsFirst);
            Assert.Equal(DungeonService.FirstAwardNone, (await AwardInfo(host, bot, Normal, 1)).IsFirst);
            long before = await Count(host, bot);

            // Hard pays its own 40 and, Normal being unpaid, Normal's 30 as well.
            Assert.Equal(0, await Claim(bot, Hard, 1));
            Assert.Equal(before + 70, await Count(host, bot));
            Assert.Equal(DungeonService.FirstAwardFinish, (await AwardInfo(host, bot, Hard, 1)).IsFirst);
            Assert.Equal(DungeonService.FirstAwardFinish, (await AwardInfo(host, bot, Normal, 1)).IsFirst);

            Assert.Equal(ErrorCodes.RewardClaimed, await Claim(bot, Hard, 1));
            Assert.Equal(ErrorCodes.RewardClaimed, await Claim(bot, Normal, 1));
            Assert.Equal(before + 70, await Count(host, bot));

            // A boss without a first-clear reward shows Finish once cleared and offers nothing to claim.
            await Cleared(host, bot, Normal, 2);
            Assert.Equal(DungeonService.FirstAwardFinish, (await AwardInfo(host, bot, Normal, 2)).IsFirst);
            Assert.Equal(ErrorCodes.RewardClaimed, await Claim(bot, Normal, 2));
        }

        // The paid flag is stored with the reward.
        await host.RestartAsync();
        await using var again = await host.InWorldAsync("Clearer", create: false);
        Assert.Equal(DungeonService.FirstAwardFinish, (await AwardInfo(host, again, Hard, 1)).IsFirst);
        Assert.Equal(ErrorCodes.RewardClaimed, await Claim(again, Hard, 1));
    }

    [Fact]
    public async Task NormalPaysOnlyItself()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var bot = await host.InWorldAsync("Normal");
        await Cleared(host, bot, Normal, 1);
        long before = await Count(host, bot);
        Assert.Equal(0, await Claim(bot, Normal, 1));
        Assert.Equal(before + 30, await Count(host, bot));
        Assert.Equal(DungeonService.FirstAwardNone, (await AwardInfo(host, bot, Hard, 1)).IsFirst);
    }

    /// <summary>
    /// The reward pays again every season (Steven's answer on #228 and #275). A new season rebuilds the state
    /// (CurrentState) without Records or FirstPassPaid, and the once-only economy key carries the season start. The
    /// restart with a later ServerOptions.SeasonStart stands in for the rollover.
    /// </summary>
    [Fact]
    public async Task ANewSeasonOffersTheFirstClearRewardAgain()
    {
        string seasonStart = "2026-10-05";
        await using var host = await SyntheticHost.StartAsync(o => o.SeasonStart = seasonStart);
        long before;
        await using (var bot = await host.InWorldAsync("Seasons"))
        {
            await Cleared(host, bot, Normal, 1);
            before = await Count(host, bot);
            Assert.Equal(0, await Claim(bot, Normal, 1));
            Assert.Equal(ErrorCodes.RewardClaimed, await Claim(bot, Normal, 1));
        }

        seasonStart = "2027-01-04";
        await host.RestartAsync();
        await using var again = await host.InWorldAsync("Seasons", create: false);
        Assert.Equal(DungeonService.FirstAwardNone, (await AwardInfo(host, again, Normal, 1)).IsFirst); // the clear resets too
        Assert.Equal(ErrorCodes.Parameter, await Claim(again, Normal, 1));

        await Cleared(host, again, Normal, 1);
        Assert.Equal(DungeonService.FirstAwardComplete, (await AwardInfo(host, again, Normal, 1)).IsFirst);
        Assert.Equal(0, await Claim(again, Normal, 1));
        Assert.Equal(before + 60, await Count(host, again));
        Assert.Equal(ErrorCodes.RewardClaimed, await Claim(again, Normal, 1));
    }

    [Fact]
    public async Task HardDoesNotPayALockedNormal()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var bot = await host.InWorldAsync("Locked");
        await Cleared(host, bot, OpenHard, 1);
        long before = await Count(host, bot);
        Assert.Equal(0, await Claim(bot, OpenHard, 1));
        Assert.Equal(before + 40, await Count(host, bot));
        bool paid = true;
        await OnLoop(host, () => { paid = Session(host, bot).Dungeons!.FirstPassPaid.Contains($"{LockedNormal}:1"); return Task.CompletedTask; });
        Assert.False(paid); // a locked dungeon is not in 5062, so its paid flag is checked on the state
    }
}
