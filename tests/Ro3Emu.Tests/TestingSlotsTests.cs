using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>TestingSwitches.Slots, the back-to-back occurrences of Ro3Emu:Testing:AlwaysOpen (a test aid).</summary>
public sealed class TestingSlotsTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");

    private static long Local(DateTime local) => new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUnixTimeSeconds();

    [Fact]
    public void EverySwitchIsOffByDefaultAndResetBringsBackTheConfiguredValues()
    {
        var testing = new TestingSwitches(Options.Create(new ServerOptions()));
        Assert.False(testing.Any);
        Assert.False(testing.Opens(126));
        var configured = new TestingSwitches(Options.Create(new ServerOptions { Testing = { UnlimitedRewards = true } }));
        configured.AlwaysOpen = true;
        configured.UnlimitedRewards = false;
        Assert.True(configured.Opens(126));
        Assert.False(configured.Opens(115)); // the Royal City War keeps its calendar
        configured.Reset();
        Assert.Equal((false, true), (configured.AlwaysOpen, configured.UnlimitedRewards));
    }

    [Fact]
    public void SlotsFollowEachOtherInsideTheTwoPartsOfTheDay()
    {
        long At(int day, int hour, int minute) => Local(new DateTime(2026, 11, day, hour, minute, 0));
        // 85 minutes (Survivor Battle 18:30-19:55) from 05:00: 05:00, 06:25, 07:50, 09:15, 10:40, 12:05...
        Assert.Equal([(At(10, 12, 5), At(10, 13, 30)), (At(10, 13, 30), At(10, 14, 55))],
            [.. TestingSwitches.Slots(At(10, 12, 10), 85 * 60, fullOnly: false, Zone)]);
        // A run of phases is never cut: 23:30-00:00 would end past 23:59:59, so the next run is tomorrow at 00:00.
        Assert.Equal((At(11, 0, 0), At(11, 0, 30)), TestingSwitches.Slots(At(10, 23, 50), 30 * 60, fullOnly: true, Zone).First());
        // A plain window is cut at the end of the part instead.
        Assert.Equal((At(10, 23, 30), At(11, 0, 0) - 1), TestingSwitches.Slots(At(10, 23, 45), 30 * 60, fullOnly: false, Zone).First());
        // A whole-day row takes each part whole: 00:00-05:00, then 05:00-23:59:59.
        Assert.Equal((At(10, 0, 0), At(10, 5, 0)), TestingSwitches.Slots(At(10, 3, 0), 86400, fullOnly: false, Zone).First());
        Assert.Equal((At(10, 5, 0), At(11, 0, 0) - 1), TestingSwitches.Slots(At(10, 12, 0), 86400, fullOnly: false, Zone).First());
    }
}
