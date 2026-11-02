using System.Text.Json;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Game.Persistence;

namespace Ro3Emu.Tests;

/// <summary>本服午夜補充規則：當日在線才領、漏天不補、重送不重領；資料集使用合成 fixture。</summary>
public sealed class WildAfkDailyAllowanceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private long _milliseconds = now.ToUnixTimeMilliseconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _milliseconds));
        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("LAB UTC+08", Offset, "LAB UTC+08", "LAB UTC+08");
        public void Set(DateTimeOffset value) => Interlocked.Exchange(ref _milliseconds, value.ToUnixTimeMilliseconds());
    }

    private static DateTimeOffset Local(int day, int hour = 0, int minute = 0, int second = 0) =>
        new(2026, 11, day, hour, minute, second, Offset);

    private static GameSession Session(SyntheticHost host, BotClient bot) => host.Service<SessionRegistry>().All()
        .Single(s => s.Role?.Id == (long)bot.Self!.Uuid);

    private static Task Owned(SyntheticHost host, GameSession session, Func<Task> action) =>
        host.Service<GameLoop>().InvokeAsync(() => host.Service<OwnerRegistry>().RunSessionAsync(
            "午夜額度測試", [ReservationKey.Role(session.Role!.Id)], session, action));

    private static Task<T> Read<T>(SyntheticHost host, Func<T> read) => host.Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    private static async Task<long> Query(BotClient bot)
    {
        await bot.SendGameAsync(WildAfkService.Info, new WildAFKInfoReq());
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(WildAfkService.Info), Wait);
        return WildAFKInfoRsp.Parser.ParseFrom(packet.Body).CurAfkTime;
    }

    private static WildAfkService.State Stored(SyntheticHost host, long role) =>
        JsonSerializer.Deserialize<WildAfkService.State>(host.Service<RoleStore>().LoadWildAfk(role)!)!;

    private static Task SeedOffline(SyntheticHost host, long role, Action<WildAfkService.State> change) =>
        host.Service<GameLoop>().InvokeAsync(() => host.Service<OwnerRegistry>().RunAsync(
            "離線額度測試資料", [ReservationKey.Role(role)], () =>
            {
                var state = Stored(host, role);
                change(state);
                host.Service<RoleStore>().SaveWildAfk(role, JsonSerializer.SerializeToUtf8Bytes(state));
                return Task.CompletedTask;
            }));

    [Theory]
    [InlineData(0, 1200, 1200)]
    [InlineData(1, 1200, 4800)]
    [InlineData(7, 1200, 4800)]
    [InlineData(7, 0, 3600)]
    [InlineData(1, 9000, 10800)]
    [InlineData(7, 10800, 10800)]
    public async Task OfflineDaysGrantOnlyTheLoginDayAndRelogCannotReplay(int absentDays, long bank, long expected)
    {
        var clock = new Clock(Local(10, 12));
        await using var host = await SyntheticHost.StartAsync(clock: clock);
        await using var bot = await host.InWorldAsync("Daily");
        long role = (long)bot.Self!.Uuid;
        await bot.SwitchRoleAsync();
        await SeedOffline(host, role, state =>
        {
            state.Info.HighMultiplierTimeSeconds = bank;
            state.AllowanceEpoch = Local(10).AddDays(-absentDays).ToUnixTimeSeconds();
        });
        await bot.EnterWorldAsync(role);
        Assert.Equal(expected, await Query(bot));
        Assert.Equal(Local(10).ToUnixTimeSeconds(), await Read(host, () => Stored(host, role).AllowanceEpoch));
        await bot.SwitchRoleAsync();
        await bot.EnterWorldAsync(role);
        Assert.Equal(expected, await Query(bot));
        Assert.Equal(expected, await Query(bot));
    }

    [Fact]
    public async Task OnlinePausedRoleReceivesMidnightGrantFromTickerWithoutARequestAndKeepsFiveAmCounters()
    {
        var clock = new Clock(Local(2, 23, 59, 59));
        await using var host = await SyntheticHost.StartAsync(clock: clock);
        await using var bot = await host.InWorldAsync("Midnight");
        var session = Session(host, bot);
        long daily = session.WildAfk!.DailyEpoch;
        await Owned(host, session, () =>
        {
            session.WildAfk!.Info.IsHighMagnification = false;
            session.WildAfk.ConsumedTodaySeconds = 77;
            return Task.CompletedTask;
        });
        Assert.Equal(3600, await Query(bot));
        clock.Set(Local(3));
        // 只等正式 ticker 的 5035，不先查詢或切換加倍來觸發補充。
        await bot.Inbox.WaitAsync(e => e.IsGame(WildAfkService.Sync)
            && DBWildAFKInfoIncrement.Parser.ParseFrom(e.Body).HighMultiplierTimeSeconds == 7200, Wait);
        Assert.Equal(7200, await Read(host, () => Stored(host, session.Role!.Id).Info.HighMultiplierTimeSeconds));
        Assert.Equal(daily, await Read(host, () => session.WildAfk!.DailyEpoch));
        Assert.Equal(77, await Read(host, () => session.WildAfk!.ConsumedTodaySeconds));
        long role = session.Role!.Id;
        await bot.SwitchRoleAsync();
        await bot.EnterWorldAsync(role);
        Assert.Equal(7200, await Query(bot));
    }

    [Theory]
    [InlineData(2, 2, 4500)]
    [InlineData(2, 6, 900)]
    [InlineData(7, 6, 4500)]
    public async Task LegacyFiveAmDatesMigrateWithoutGivingTheSameDateTwice(int lastGrantDay, int loginHour, long expected)
    {
        int loginDay = lastGrantDay == 7 ? 10 : 3;
        var clock = new Clock(Local(loginDay, loginHour));
        await using var host = await SyntheticHost.StartAsync(clock: clock);
        await using var bot = await host.InWorldAsync("Legacy");
        long role = (long)bot.Self!.Uuid;
        await bot.SwitchRoleAsync();
        await SeedOffline(host, role, state =>
        {
            state.Info.HighMultiplierTimeSeconds = 900;
            state.AllowanceEpoch = 0;
            int creditedDay = loginHour >= 5 && lastGrantDay == 2 ? loginDay : lastGrantDay;
            state.DailyEpoch = Local(creditedDay, 5).ToUnixTimeSeconds();
        });
        await bot.EnterWorldAsync(role);
        Assert.Equal(expected, await Query(bot));
        Assert.Equal(Local(loginDay).ToUnixTimeSeconds(), await Read(host, () => Stored(host, role).AllowanceEpoch));
        Assert.Equal(expected, await Query(bot));
    }

    [Fact]
    public async Task MidnightConsumptionAndConcurrentReconcileGrantOnceAndPreserveTheFraction()
    {
        var clock = new Clock(Local(2, 23, 59, 59).AddMilliseconds(500));
        await using var host = await SyntheticHost.StartAsync(clock: clock);
        await using var bot = await host.InWorldAsync("Consume");
        var session = Session(host, bot);
        await Owned(host, session, () =>
        {
            host.Service<GameLoop>().Remove(host.Service<WildAfkService>());
            var state = session.WildAfk!;
            state.Info.HighMultiplierTimeSeconds = 1200;
            state.Info.IsHighMagnification = true;
            state.Info.IsSuspended = false;
            state.AccountedAtMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
            state.LastKillAtMs = state.AccountedAtMs;
            state.RemainderMs = 0;
            return Task.CompletedTask;
        });
        clock.Set(Local(3).AddMilliseconds(500));
        var service = host.Service<WildAfkService>();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => host.Service<GameLoop>().InvokeAsync(() => service.ReconcileAsync(session))));
        Assert.Equal(4799, await Query(bot));
        Assert.Equal(1, await Read(host, () => session.WildAfk!.ConsumedTodaySeconds));
        Assert.Equal(0, await Read(host, () => session.WildAfk!.RemainderMs));
        Assert.Equal(Local(3).ToUnixTimeSeconds(), await Read(host, () => Stored(host, session.Role!.Id).AllowanceEpoch));
    }

    [Fact]
    public async Task MidnightGrantCommitsThePendingClockAndClearsItsLazySave()
    {
        var clock = new Clock(Local(2, 23, 59, 59));
        await using var host = await SyntheticHost.StartAsync(clock: clock);
        await using var bot = await host.InWorldAsync("Barrier");
        var session = Session(host, bot);
        var lazy = host.Service<WildAfkPersistence>();
        await Owned(host, session, async () =>
        {
            host.Service<GameLoop>().Remove(host.Service<WildAfkService>());
            host.Service<GameLoop>().Remove(lazy);
            var state = session.WildAfk!;
            state.Info.HighMultiplierTimeSeconds = 1200;
            state.LazyMarked = JsonSerializer.SerializeToUtf8Bytes(state);
            lazy.MarkDirty(session, urgent: false);
            Assert.True(lazy.IsDirty(session));
            clock.Set(Local(3));
            await host.Service<WildAfkService>().ReconcileAsync(session);
            Assert.False(lazy.IsDirty(session));
            Assert.Null(state.LazyMarked);
            Assert.Equal(4800, Stored(host, session.Role!.Id).Info.HighMultiplierTimeSeconds);
        });
        long role = session.Role!.Id;
        await bot.SwitchRoleAsync();
        await bot.EnterWorldAsync(role);
        Assert.Equal(4800, await Query(bot));
    }

    [Fact]
    public async Task FailedMidnightSaveLeavesBalanceAndGrantDateUntouchedAndRetryGrantsOnce()
    {
        var clock = new Clock(Local(2, 23, 59, 59));
        await using var host = await SyntheticHost.StartAsync(clock: clock);
        await using var bot = await host.InWorldAsync("Fault");
        var session = Session(host, bot);
        var service = host.Service<WildAfkService>();
        await Owned(host, session, async () =>
        {
            host.Service<GameLoop>().Remove(service);
            FixtureSql.Execute(host.Service<AccountStore>(),
                "CREATE TRIGGER fail_midnight BEFORE UPDATE OF wild_afk ON role BEGIN SELECT RAISE(ABORT, 'midnight fixture'); END");
            clock.Set(Local(3));
            try
            {
                await Assert.ThrowsAnyAsync<Exception>(() => service.ReconcileAsync(session));
                Assert.Equal(3600, session.WildAfk!.Info.HighMultiplierTimeSeconds);
                Assert.Equal(Local(2).ToUnixTimeSeconds(), session.WildAfk.AllowanceEpoch);
                Assert.Equal(Local(2).ToUnixTimeSeconds(), Stored(host, session.Role!.Id).AllowanceEpoch);
            }
            finally { FixtureSql.Execute(host.Service<AccountStore>(), "DROP TRIGGER fail_midnight"); }
            await service.ReconcileAsync(session);
        });
        Assert.Equal(7200, await Query(bot));
        Assert.Equal(7200, await Query(bot));
    }
}
