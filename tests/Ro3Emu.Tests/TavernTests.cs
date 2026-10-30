using Romsg;
using Ro3Emu.Server.Game;
namespace Ro3Emu.Tests;
/// <summary>自造計時／日界資料；不替代原服政策與玩家端驗收。</summary>
public sealed class TavernTests
{
    [Theory]
    [InlineData(0, 1000, 1000, true, 0)]
    [InlineData(0, 1000, 1999, true, 999)]
    [InlineData(1199000, 1000, 2000, true, 1200000)]
    [InlineData(1199000, 1000, 8000, true, 1200000)]
    [InlineData(1200, 1000, 0, true, 1200)]
    [InlineData(1200, 1000, 8000, false, 1200)]
    public void MeterUsesOnlyEligiblePositiveIntervalsAndCapsOnce(long active, long cursor, long until, bool joined, long expected)
    {
        var before = new TavernService.Meter { ActiveMs = active, AccountedAtMs = cursor, RoomId = 42, Lease = "lease" };
        var after = TavernService.Accrue(before, until, joined, 1200);
        Assert.Equal(expected, after.ActiveMs);
        Assert.Equal(Math.Max(cursor, until), after.AccountedAtMs);
        Assert.Equal(active, before.ActiveMs);
        Assert.Equal(cursor, before.AccountedAtMs);
    }
    [Fact]
    public void FractionalIntervalsRetainTheirRemainderWithoutDuplicateCredit()
    {
        var first = TavernService.Accrue(new() { AccountedAtMs = 1000 }, 1550, true, 1200);
        var second = TavernService.Accrue(first, 2000, true, 1200);
        Assert.Equal(1000, second.ActiveMs);
        Assert.Equal(1000, TavernService.Accrue(second, 2000, true, 1200).ActiveMs);
    }
    [Fact]
    public void SavedOldDayAwardRemainsPendingUntilActualDelivery()
    {
        var meter = new TavernService.Meter { DayEpoch = 100, ActiveMs = 1200000 };
        var daily = new DailyActivityService.DailyState { DailyEpoch = 100 };
        var tasks = new DBTaskInfo(); tasks.Infos[2000] = new TaskInfo { TaskId = 2000, Status = TaskState.Finish };
        var rows = new DBDailyActivityInfo(); rows.ActivityData[2000] = new DBDailyActivityData { ActivityId = 2000 };
        Assert.True(TavernService.PendingDailyAward(meter, daily, tasks, rows, 2000, 1200));
        tasks.CompleteTaskNum[2000] = 1;
        Assert.False(TavernService.PendingDailyAward(meter, daily, tasks, rows, 2000, 1200));
        tasks.CompleteTaskNum.Clear(); meter.DayEpoch = 99;
        Assert.False(TavernService.PendingDailyAward(meter, daily, tasks, rows, 2000, 1200));
    }
}
