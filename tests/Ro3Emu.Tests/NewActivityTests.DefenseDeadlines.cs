using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class NewActivityTests
{
    [ClientDataFact]
    public async Task DefenseRoundAndIntensityProjectAtDeadlinesWithoutDefensePolling()
    {
        await using var bot = await InWorld("DefDeadline");
        await Gm(bot, "!lvl 99");
        var defense = Service<PronteraDefenseService>();
        await OnLoop(() => { Service<GameLoop>().Remove(defense); return Task.CompletedTask; });
        Assert.True(Service<ActivityClock>().TryWindow(1012, out var start, out _));
        long first = start.ToUnixTimeSeconds() + 122400;
        _clock.AdvanceSeconds(checked((int)(first - 1 - _clock.GetUtcNow().ToUnixTimeSeconds())));
        await Reconcile(bot);
        var session = Session(bot);
        Assert.Equal(0, session.NewActivities!.ActivityInfo[118].ActivityParam.PronteraDefense.GlobalInfo.CurRound);

        foreach (var (offset, round, intensity) in new[]
            { (0, 1, 80000L), (600, 1, 100000L), (1200, 1, 120000L), (1560, 1, 140000L), (1800, 0, 0L) })
        {
            _clock.AdvanceSeconds(checked((int)(first + offset - _clock.GetUtcNow().ToUnixTimeSeconds())));
            await Eventually(() =>
            {
                var saved = DBNewActivityInfo.Parser.ParseFrom(Service<RoleStore>().LoadNewActivities(session.Role!.Id));
                var state = saved.ActivityInfo[118].ActivityParam.PronteraDefense.GlobalInfo;
                return state.CurRound == round && state.CurIntensity == intensity;
            });
            await OnLoop(() =>
            {
                var expected = defense.BuildPlayerState(session).GlobalInfo;
                var current = session.NewActivities!.ActivityInfo[118].ActivityParam.PronteraDefense.GlobalInfo;
                Assert.Equal(expected, current);
                Assert.Equal(round, current.CurRound);
                Assert.Equal(intensity, current.CurIntensity);
                return Task.CompletedTask;
            });
        }
    }
}
