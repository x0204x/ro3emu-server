using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

public sealed partial class NewActivityTests
{
    private async Task Eventually(Func<bool> condition, Func<string>? detail = null)
    {
        // 15 s: under the full suite a deadline's tick and its save can lag well behind the advanced clock.
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < until)
        {
            // Read loop-owned state on its owner, after the current synchronous mutation/save step.
            // A test-thread read can otherwise observe RefreshCalendar before its following DB write.
            bool satisfied = false;
            await OnLoop(() => { satisfied = condition(); return Task.CompletedTask; });
            if (satisfied) return;
            await Task.Delay(25);
        }
        await OnLoop(() =>
        {
            Assert.True(condition(), "Activity deadline was not observed within 15 seconds. " + detail?.Invoke());
            return Task.CompletedTask;
        });
    }

    private Task WaitForActivityRefreshDrain(GameSession session)
    {
        var queuedField = typeof(NewActivityService).GetField("_queued",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(queuedField);
        return Eventually(() =>
        {
            var queued = (HashSet<(GameSession Session, long Attachment)>)queuedField.GetValue(Service<NewActivityService>())!;
            return session.ActivityGate.CurrentCount == 1 &&
                !queued.Any(entry => ReferenceEquals(entry.Session, session));
        });
    }

    [ClientDataFact]
    public async Task LocalMidnightAdvancesSevenDayOnceAndPersistsWithoutARequest()
    {
        await using var bot = await InWorld("ActMidnight");
        await Gm(bot, "!lvl 30");
        _clock.SetLocal(2026, 10, 8, 23, 59, 59);
        await Reconcile(bot);
        var session = Session(bot);
        Assert.Equal(1, session.NewActivities!.ActivityInfo[113].ActivityParam.SevenDay.LoginDays);
        long role = session.Role!.Id;
        // Move the fake clock between complete reconciliation passes. A queued GM refresh
        // can otherwise sample the old day before an await and the new day after it.
        await OnLoop(() => Service<OwnerRegistry>().RunAsync("test/activity-clock", [ReservationKey.Realm], () =>
        {
            _clock.AdvanceSeconds(1);
            return Task.CompletedTask;
        }));
        await Eventually(() => session.NewActivities!.ActivityInfo[113].ActivityParam.SevenDay.LoginDays == 2
            && DBNewActivityInfo.Parser.ParseFrom(Service<RoleStore>().LoadNewActivities(role))
                .ActivityInfo[113].ActivityParam.SevenDay.LoginDays == 2);
        Assert.Equal(2, DBNewActivityInfo.Parser.ParseFrom(Service<RoleStore>().LoadNewActivities(role))
            .ActivityInfo[113].ActivityParam.SevenDay.LoginDays);
        Assert.Equal(5, session.ActivityServerDay);

        await OnLoop(() => Service<OwnerRegistry>().RunAsync("test/activity-clock", [ReservationKey.Realm], () =>
        {
            _clock.AdvanceDays(3);
            return Task.CompletedTask;
        }));
        await Eventually(() => session.NewActivities!.ActivityInfo[113].ActivityParam.SevenDay.LoginDays == 3
            && DBNewActivityInfo.Parser.ParseFrom(Service<RoleStore>().LoadNewActivities(role))
                .ActivityInfo[113].ActivityParam.SevenDay.LoginDays == 3);
        Assert.Equal(3, DBNewActivityInfo.Parser.ParseFrom(Service<RoleStore>().LoadNewActivities(role))
            .ActivityInfo[113].ActivityParam.SevenDay.LoginDays);
    }

    [ClientDataFact]
    public async Task MonthCardAndTreasureExpireAtDeadlinesWithoutARequest()
    {
        await using var bot = await InWorld("ActExpiry");
        await Gm(bot, "!lvl 99");
        _clock.SetLocal(2026, 10, 8, 23, 59, 59);
        await Reconcile(bot);
        var session = Session(bot);
        long id = GameData.Int(Service<GameData>().Table("MonthCardConfig").Values.First(), "_iId");
        long today = Service<ActivityClock>().Day(_clock.GetUtcNow().ToUnixTimeSeconds());
        var expiryLocal = new DateTime(2026, 10, 12);
        long expiry = new DateTimeOffset(expiryLocal, _clock.LocalTimeZone.GetUtcOffset(expiryLocal)).ToUnixTimeSeconds();
        await OnLoop(() =>
        {
            session.NewActivities!.ActivityInfo[199].ActivityParam.MonthCard.Infos[id] =
                new MonthCardInfo { MonthCardId = id, ExpireTs = expiry, LastDays = 5, DayAward = true };
            session.ActivityRuntime.MonthCards[id] = new MonthCardRuntime { ClaimedDay = today };
            session.ActivityRuntime.AdventureTreasure = new AdventureTreasureCursor
            { PointId = 1, GroupId = 1, ItemId = 1112001, Began = _clock.GetUtcNow().ToUnixTimeSeconds() - 10799 };
            Service<RoleStore>().SaveNewActivities(session.Role!.Id, session.NewActivities.ToByteArray());
            Service<RoleStore>().SaveActivityRuntime(session.Role.Id,
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(session.ActivityRuntime));
            return Task.CompletedTask;
        });
        await Reconcile(bot); // re-arm from the committed state
        _clock.AdvanceSeconds(1);
        await Eventually(() => session.NewActivities!.ActivityInfo[199].ActivityParam.MonthCard.Infos[id].LastDays == 4
            && !session.NewActivities.ActivityInfo[199].ActivityParam.MonthCard.Infos[id].DayAward
            && session.ActivityRuntime.AdventureTreasure.PointId == 0,
            () => $"card days={session.NewActivities!.ActivityInfo[199].ActivityParam.MonthCard.Infos[id].LastDays}, "
                + $"award={session.NewActivities.ActivityInfo[199].ActivityParam.MonthCard.Infos[id].DayAward}, "
                + $"treasure={session.ActivityRuntime.AdventureTreasure.PointId}, day={session.ActivityServerDay}");
        long role = session.Role!.Id;
        var saved = DBNewActivityInfo.Parser.ParseFrom(Service<RoleStore>().LoadNewActivities(role));
        Assert.Equal(4, saved.ActivityInfo[199].ActivityParam.MonthCard.Infos[id].LastDays);
        Assert.False(saved.ActivityInfo[199].ActivityParam.MonthCard.Infos[id].DayAward);
        var runtime = System.Text.Json.JsonSerializer.Deserialize<ActivityRuntimeState>(Service<RoleStore>().LoadActivityRuntime(role)!)!;
        Assert.Equal(0, runtime.AdventureTreasure.PointId);

        _clock.SetLocal(2026, 10, 12, 0, 0);
        await Eventually(() => session.NewActivities!.ActivityInfo[199].ActivityParam.MonthCard.Infos[id].LastDays == 0
            && session.NewActivities.ActivityInfo[199].ActivityParam.MonthCard.Infos[id].DayAward);
        saved = DBNewActivityInfo.Parser.ParseFrom(Service<RoleStore>().LoadNewActivities(role));
        Assert.Equal(0, saved.ActivityInfo[199].ActivityParam.MonthCard.Infos[id].LastDays);
        Assert.True(saved.ActivityInfo[199].ActivityParam.MonthCard.Infos[id].DayAward);
    }

    [ClientDataFact]
    public async Task CalendarEndProjectsAndPersistsAtExactBoundary()
    {
        await using var bot = await InWorld("ActCalEnd");
        await Gm(bot, "!lvl 99");
        _clock.SetLocal(2026, 10, 19, 23, 59, 59);
        await Reconcile(bot);
        var session = Session(bot);
        Assert.NotEqual("_close", session.NewActivities!.ActivityInfo[200].State);
        _clock.AdvanceSeconds(1);
        await Eventually(() => session.NewActivities!.ActivityInfo[200].State == "_close"
            && DBNewActivityInfo.Parser.ParseFrom(Service<RoleStore>().LoadNewActivities(session.Role!.Id))
                .ActivityInfo[200].State == "_close");
        Assert.Equal("_close", DBNewActivityInfo.Parser.ParseFrom(Service<RoleStore>().LoadNewActivities(session.Role!.Id))
            .ActivityInfo[200].State);
    }

    [ClientDataFact]
    public async Task CalendarStartProjectsAndPersistsWithoutARequest()
    {
        await using var bot = await InWorld("ActCalStart");
        await Gm(bot, "!lvl 99");
        _clock.SetLocal(2026, 10, 13, 23, 59, 59);
        await Reconcile(bot);
        var session = Session(bot);
        Assert.Equal("_close", session.NewActivities!.ActivityInfo[118].State);
        _clock.AdvanceSeconds(1);
        await Eventually(() => session.NewActivities!.ActivityInfo[118].State == "Start"
            && DBNewActivityInfo.Parser.ParseFrom(Service<RoleStore>().LoadNewActivities(session.Role!.Id))
                .ActivityInfo[118].State == "Start");
        Assert.Equal("Start", DBNewActivityInfo.Parser.ParseFrom(Service<RoleStore>().LoadNewActivities(session.Role!.Id))
            .ActivityInfo[118].State);
    }

    [ClientDataFact]
    public async Task LockedExpiredMonthCardDoesNotRepeatImmediateDeadline()
    {
        await using var bot = await InWorld("ActCardLock"); // level one does not satisfy activity 199's level-20 gate
        _clock.SetLocal(2026, 10, 8, 23, 59, 59);
        await Reconcile(bot);
        var session = Session(bot);
        long id = GameData.Int(Service<GameData>().Table("MonthCardConfig").Values.First(), "_iId");
        int projections = 0;
        await OnLoop(() =>
        {
            session.NewActivities!.ActivityInfo[199].ActivityParam.MonthCard.Infos[id] =
                new MonthCardInfo
                {
                    MonthCardId = id, ExpireTs = _clock.GetUtcNow().ToUnixTimeSeconds() - 1,
                    LastDays = 4, DayAward = false
                };
            Service<RoleStore>().SaveNewActivities(session.Role!.Id, session.NewActivities.ToByteArray());
            Service<NewActivityService>().ProjectionChanged.Add(s =>
            {
                if (ReferenceEquals(s, session)) Interlocked.Increment(ref projections);
            });
            return Task.CompletedTask;
        });
        await Reconcile(bot);
        int before = Volatile.Read(ref projections);
        _clock.AdvanceSeconds(1);
        await OnLoop(() => { Service<Deadlines>().Tick(default); return Task.CompletedTask; });
        await Eventually(() => Volatile.Read(ref projections) > before);
        int settled = Volatile.Read(ref projections);
        for (int i = 0; i < 4; i++)
        {
            await OnLoop(() => { Service<Deadlines>().Tick(default); return Task.CompletedTask; });
            await OnLoop(() => Task.CompletedTask); // process any callback posted by Tick
        }
        Assert.Equal(settled, Volatile.Read(ref projections));
        Assert.Equal(4, session.NewActivities!.ActivityInfo[199].ActivityParam.MonthCard.Infos[id].LastDays);
    }

    [ClientDataFact]
    public async Task EveDeadlineWaitingOnSocialGateCannotSettleIntoNewRole()
    {
        var eveWindow = Assert.Single(Service<ActivityClock>().Segments(1011, _clock.GetUtcNow().ToUnixTimeSeconds()));
        _clock.SetLocal(2026, 10, 8, 0, 1);
        Assert.True(eveWindow.Contains(_clock.GetUtcNow().ToUnixTimeSeconds()));
        await using var bot = await InWorld("EveOld");
        // Finish the preceding SceneLoaded handler on this connection before the
        // test deliberately blocks activity work and requests a role switch.
        await bot.SendGameAsync(CharacterService.ItemUse,
            new ItemUseReq { ItemGuid = long.MaxValue, ItemCount = 1 });
        Assert.Equal(ErrorCodes.ItemMissing,
            (await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        await Gm(bot, "!lvl 99");
        await Reconcile(bot);
        var session = Session(bot);
        // !lvl may still have a coalesced refresh in flight. Drain it before
        // observing the expired Eve callback's own wait on the social gate.
        await WaitForActivityRefreshDrain(session);
        long oldRole = session.Role!.Id;
        Assert.True(session.NewActivities!.ActivityTypeState[19].ActivityState[117].EnterTime > 0);
        var gate = (SemaphoreSlim)typeof(NewActivityService)
            .GetField("_adventureSocialGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(Service<NewActivityService>())!;
        var socialWaiter = typeof(SemaphoreSlim).GetField("m_asyncHead",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(socialWaiter);
        await OnLoop(() => { Assert.Null(socialWaiter.GetValue(gate)); return Task.CompletedTask; });
        await gate.WaitAsync();
        Task? enter = null;
        long newRole = 0;
        try
        {
            _clock.SetLocal(2026, 10, 12, 0, 0, 1);
            await OnLoop(() => { Service<Deadlines>().Tick(default); return Task.CompletedTask; });
            // ActivityGate alone can be held before SettleEveExpiredAsync reaches
            // WaitAsync. Observe the actual queued waiter before role-switch cancellation.
            await Eventually(() => session.ActivityGate.CurrentCount == 0 &&
                socialWaiter.GetValue(gate) != null);
            await bot.SwitchRoleAsync();
            newRole = (await bot.CreateRoleAsync("EveNew")).RoleBaseInfo.Id;
            enter = bot.EnterWorldAsync(newRole);
            await Eventually(() => session.Role?.Id == newRole);
        }
        finally { gate.Release(); }
        Assert.NotNull(enter);
        await enter;
        Assert.Equal(newRole, session.Role!.Id);
        static int MailCount(byte[]? bytes) => bytes == null ? 0
            : System.Text.Json.JsonSerializer.Deserialize<MailService.Mailbox>(bytes)!.Letters.Count;
        Assert.Equal(0, MailCount(Service<RoleStore>().LoadMailbox(newRole)));
        Assert.Equal(0, MailCount(Service<RoleStore>().LoadMailbox(oldRole)));
        Assert.Equal(0, System.Text.Json.JsonSerializer.Deserialize<ActivityRuntimeState>(
            Service<RoleStore>().LoadActivityRuntime(oldRole)!)!.EveSettledCycleStart);
    }

    [ClientDataFact]
    public async Task FailedMonthCardAfterPassCommitRetriesTheCurrentAttachment()
    {
        await using var bot = await InWorld("ActRetry");
        await Gm(bot, "!lvl 99");
        var deadlines = Service<Deadlines>();
        await OnLoop(() => { Service<GameLoop>().Remove(deadlines); return Task.CompletedTask; });
        _clock.SetLocal(2026, 10, 8, 23, 59, 59);
        await Reconcile(bot);
        var session = Session(bot);
        var queued = (HashSet<(GameSession Session, long Attachment)>)typeof(NewActivityService)
            .GetField("_queued", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(Service<NewActivityService>())!;
        bool RefreshIdle()
        {
            lock (queued) return !queued.Any(item => ReferenceEquals(item.Session, session))
                && session.ActivityGate.CurrentCount == 1;
        }
        long cardId = GameData.Int(Service<GameData>().Table("MonthCardConfig").Values.First(), "_iId");
        var expiryLocal = new DateTime(2026, 10, 12);
        long expiry = new DateTimeOffset(expiryLocal, _clock.LocalTimeZone.GetUtcOffset(expiryLocal)).ToUnixTimeSeconds();
        long today = Service<ActivityClock>().Day(_clock.GetUtcNow().ToUnixTimeSeconds());
        const string activityDeadline = "new-activity";
        DeadlineLease firstLease = default;
        bool firstCompleted = false;
        // Observe idle and arm the fault in the same loop callback. A queued refresh
        // can otherwise start between a successful idle poll and a separate callback.
        await Eventually(() =>
        {
            if (!RefreshIdle()) return false;
            Assert.True(RefreshIdle());
            var pass = session.NewActivities!.ActivityInfo[200];
            session.ActivityRuntime.PassProgress[200] = new PassProgressRuntime
            { Initialized = true, LastBalance = -1, ActivityInstance = pass.ActivityInstance };
            session.NewActivities.ActivityInfo[199].ActivityParam.MonthCard.Infos[cardId] =
                new MonthCardInfo { MonthCardId = cardId, ExpireTs = expiry, LastDays = 5, DayAward = true };
            session.ActivityRuntime.MonthCards[cardId] = new MonthCardRuntime { ClaimedDay = today };
            Service<RoleStore>().SaveNewActivities(session.Role!.Id, session.NewActivities.ToByteArray());
            Service<RoleStore>().SaveActivityRuntime(session.Role.Id,
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(session.ActivityRuntime));
            FixtureSql.Execute(Service<AccountStore>(), """
                CREATE TABLE activity_retry_counter(n INTEGER NOT NULL);
                INSERT INTO activity_retry_counter(n) VALUES(0);
                CREATE TRIGGER activity_retry_fault AFTER UPDATE OF activity_runtime ON role BEGIN
                    UPDATE activity_retry_counter SET n=n+1;
                    SELECT CASE WHEN (SELECT n FROM activity_retry_counter)>1
                        THEN RAISE(ABORT,'activity-month-retry-test') END;
                END;
                """);
            var scheduled = deadlines.ScheduledLease(session, activityDeadline);
            Assert.True(scheduled.HasValue);
            firstLease = scheduled.Value;
            Assert.True(deadlines.ScheduledDue(session, activityDeadline) is { } due
                && due <= _clock.GetUtcNow().AddSeconds(1));
            deadlines.CallbackCompletedForTest = completed =>
            {
                if (completed != firstLease) return;
                deadlines.CallbackCompletedForTest = null;
                firstCompleted = true;
            };
            _clock.AdvanceSeconds(1);
            int waiting;
            do { waiting = deadlines.WaitingCount; deadlines.Tick(default); }
            while (deadlines.WaitingCount < waiting);
            return true;
        });
        await Eventually(() => firstCompleted && RefreshIdle()
            && session.NewActivities!.ActivityInfo[200].ActivityParam.BattlePass.Exp == 1,
            () => $"pass exp={session.NewActivities!.ActivityInfo[200].ActivityParam.BattlePass.Exp}, "
                + $"pass level={session.NewActivities.ActivityInfo[200].ActivityParam.BattlePass.Level}, "
                + $"card award={session.NewActivities.ActivityInfo[199].ActivityParam.MonthCard.Infos[cardId].DayAward}, "
                + $"activity gate={session.ActivityGate.CurrentCount}, callback completed={firstCompleted}");
        Assert.True(session.NewActivities!.ActivityInfo[199].ActivityParam.MonthCard.Infos[cardId].DayAward);
        using (var db = Service<AccountStore>().Open())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT n FROM activity_retry_counter";
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }
        FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER activity_retry_fault");
        bool retryCompleted = false;
        await OnLoop(() =>
        {
            var retry = deadlines.ScheduledLease(session, activityDeadline);
            Assert.True(retry.HasValue && retry.Value != firstLease);
            Assert.Equal(_clock.GetUtcNow().AddSeconds(1), deadlines.ScheduledDue(session, activityDeadline));
            deadlines.CallbackCompletedForTest = completed =>
            {
                if (completed != retry.Value) return;
                deadlines.CallbackCompletedForTest = null;
                retryCompleted = true;
            };
            _clock.AdvanceSeconds(1);
            int waiting;
            do { waiting = deadlines.WaitingCount; deadlines.Tick(default); }
            while (deadlines.WaitingCount < waiting);
            return Task.CompletedTask;
        });
        await Eventually(() => retryCompleted
            && !session.NewActivities!.ActivityInfo[199].ActivityParam.MonthCard.Infos[cardId].DayAward);
        var saved = DBNewActivityInfo.Parser.ParseFrom(Service<RoleStore>().LoadNewActivities(session.Role!.Id));
        Assert.Equal(1, saved.ActivityInfo[200].ActivityParam.BattlePass.Exp);
        Assert.False(saved.ActivityInfo[199].ActivityParam.MonthCard.Infos[cardId].DayAward);
    }

    [ClientDataFact]
    public async Task EveKeyIsSharedUntilTheSettlementLeadWindowOnEveryActivityPath()
    {
        // Every activity path (local, social, login) settles an ended Eve cycle before refreshing the calendar, so
        // the Eve global key must be exclusive by the time a settlement can be due, not only once it is.
        _clock.SetLocal(2026, 10, 8, 0, 1);
        await using var bot = await InWorld("EveKey");
        await Gm(bot, "!lvl 99");
        await Reconcile(bot);
        var session = Session(bot);
        var activities = Service<NewActivityService>();
        ReservationKey? Key(CommonActivityReq? request = null)
        {
            ReservationKey? key = null;
            OnLoop(() => { key = activities.EveKeyFor(session, request); return Task.CompletedTask; }).GetAwaiter().GetResult();
            return key;
        }
        long end = 0;
        await OnLoop(() => { end = session.NewActivities!.ActivityInfo[117].CycleEndTime; return Task.CompletedTask; });
        Assert.True(end > _clock.GetUtcNow().ToUnixTimeSeconds());

        Assert.Equal("shared", Key()!.Value.Part);
        Assert.NotEqual("shared", Key(new CommonActivityReq { ActivityId = 117 })!.Value.Part); // Eve adventures write

        _clock.AdvanceSeconds((int)(end - _clock.GetUtcNow().ToUnixTimeSeconds() - 299));
        Assert.NotEqual("shared", Key()!.Value.Part);
    }
}
