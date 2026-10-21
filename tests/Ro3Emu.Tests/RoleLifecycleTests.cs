using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Romsg;

namespace Ro3Emu.Tests;

/// <summary>Step 07 attachment, handover and delete races using the ordinary game gate.</summary>
public sealed class RoleLifecycleTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-lifecycle-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private GameLoop _loop = null!;
    private RoleStore _roles = null!;
    private ItemStore _items = null!;
    private MailService _mail = null!;
    private OwnerRegistry _owners = null!;
    private SessionRegistry _sessions = null!;
    private RoleSessionService _handler = null!;
    private RoleHandover _handover = null!;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class LoopErrors : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<Exception> Exceptions { get; } = new();
        public ILogger CreateLogger(string categoryName) => categoryName == typeof(GameLoop).FullName ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Exceptions.Enqueue(exception ?? new InvalidOperationException(formatter(state, exception)));
        }
        public void Dispose() { }
    }

    public async Task InitializeAsync()
    {
        // The synthetic game data: a small invented set that also holds the denomination tables guild initialization's
        // E05d receipt needs.
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], options =>
        {
            options.DataDir = _directory;
            options.GameDataDir = SyntheticData.Dir;
            options.HttpPort = httpPort;
            options.MicroGatePort = microPort;
            options.GameGatePort = gamePort;
            options.PublicHost = "127.0.0.1";
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
        _loop = _app.Services.GetRequiredService<GameLoop>();
        _roles = _app.Services.GetRequiredService<RoleStore>();
        _items = _app.Services.GetRequiredService<ItemStore>();
        _mail = _app.Services.GetRequiredService<MailService>();
        _owners = _app.Services.GetRequiredService<OwnerRegistry>();
        _sessions = _app.Services.GetRequiredService<SessionRegistry>();
        _handler = _app.Services.GetRequiredService<RoleSessionService>();
        _handover = _app.Services.GetRequiredService<RoleHandover>();
    }

    public async Task DisposeAsync()
    {
        _handler.SelectionPhaseForTest = null;
        _handler.CleanupFailureForTest = null;
        _handover.PhaseForTest = null;
        _sessions.UnregisteredForTest = null;
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private BotClient Bot() => new(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));

    private async Task<int> Delete(string account, string password, string role)
    {
        using var client = new HttpClient();
        using var response = await client.PostAsync($"{_http}/ro3emu/account/delete-role", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["identity"] = account, ["credential"] = password, ["role"] = role }));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("code").GetInt32();
    }

    [Fact]
    public async Task QuiescingFirstTimerRoleIsSkippedAndAttachedSecondStillReconciles()
    {
        var season = _app.Services.GetRequiredService<SeasonService>();
        var afk = _app.Services.GetRequiredService<WildAfkService>();
        var errors = new LoopErrors();
        _app.Services.GetRequiredService<ILoggerFactory>().AddProvider(errors);
        await _loop.InvokeAsync(() =>
        {
            _loop.Remove(season);
            _loop.Remove(afk);
            return Task.CompletedTask;
        });
        await using var firstBot = Bot();
        await firstBot.LoginAsync("tickfirst", "pw");
        await firstBot.EnterGateAsync();
        long firstId = (await firstBot.CreateRoleAsync("TickFirst")).RoleBaseInfo.Id;
        await firstBot.EnterWorldAsync(firstId);
        await using var secondBot = Bot();
        await secondBot.LoginAsync("ticksecond", "pw");
        await secondBot.EnterGateAsync();
        long secondId = (await secondBot.CreateRoleAsync("TickSecond")).RoleBaseInfo.Id;
        await secondBot.EnterWorldAsync(secondId);

        var seasonDone = Signal(); var afkDone = Signal();
        int afkAttempts = 0;
        GameSession first = null!, second = null!;
        long firstAccounted = 0, secondAccounted = 0;
        var firstRevision = _roles.ReadRevisionVector(firstId).Revision("wild_afk");
        var firstStored = _roles.LoadWildAfk(firstId);
        try
        {
            await _loop.InvokeAsync(() =>
            {
                first = _sessions.All().Single(s => s.Role?.Id == firstId);
                second = _sessions.All().Single(s => s.Role?.Id == secondId);
                first.SeasonState!.StageId = long.MaxValue;
                second.SeasonState!.StageId = long.MaxValue;
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                firstAccounted = first.WildAfk!.AccountedAtMs = now - 1000;
                first.WildAfk.Info.HighMultiplierTimeSeconds++;
                secondAccounted = second.WildAfk!.AccountedAtMs = now - 1000;
                _owners.BeginQuiescing(first.Owner!, first.Attachment!);
                first.Attachment!.Cancelled = true;
                season.TickBatchCompletedForTest = () => seasonDone.TrySetResult();
                afk.TickAttemptCompletedForTest = _ => { if (++afkAttempts == 2) afkDone.TrySetResult(); };
                var tick = new GameTime(_loop.Now + TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1));
                season.Tick(tick);
                afk.Tick(tick);
                return Task.CompletedTask;
            });
            await Task.WhenAll(seasonDone.Task, afkDone.Task).WaitAsync(TimeSpan.FromSeconds(10));
            await _loop.InvokeAsync(() =>
            {
                Assert.Equal(long.MaxValue, first.SeasonState!.StageId);
                Assert.Equal(firstAccounted, first.WildAfk!.AccountedAtMs);
                Assert.Equal(season.StageId, second.SeasonState!.StageId);
                Assert.True(second.WildAfk!.AccountedAtMs > secondAccounted);
                Assert.Equal(firstRevision, _roles.ReadRevisionVector(firstId).Revision("wild_afk"));
                Assert.Equal(firstStored, _roles.LoadWildAfk(firstId));
                return Task.CompletedTask;
            });
            Assert.Empty(errors.Exceptions);
        }
        finally
        {
            await _loop.InvokeAsync(() =>
            {
                season.TickBatchCompletedForTest = null;
                afk.TickAttemptCompletedForTest = null;
                if (first.Owner is { } owner && owner.State == RoleOwnerState.Quiescing)
                {
                    owner.State = RoleOwnerState.Attached;
                    first.Attachment!.Cancelled = false;
                }
                return Task.CompletedTask;
            });
        }
    }

    [Fact]
    public async Task AfkTimerWaitingOnOldGateSkipsQuiescedLeaseButDirectReconcileStillWorks()
    {
        var afk = _app.Services.GetRequiredService<WildAfkService>();
        var festival = _app.Services.GetRequiredService<FestivalService>();
        var errors = new LoopErrors();
        _app.Services.GetRequiredService<ILoggerFactory>().AddProvider(errors);
        // This fixture manually quiesces its role to exercise AFK alone. Stop the
        // unrelated festival projection before login so it cannot pin that role.
        await _loop.InvokeAsync(() => { _loop.Remove(afk); _loop.Remove(festival); return Task.CompletedTask; });
        await _loop.InvokeAsync(() => Task.CompletedTask); // drain an already posted empty projection
        await using var bot = Bot();
        await bot.LoginAsync("afkgate", "pw");
        await bot.EnterGateAsync();
        long roleId = (await bot.CreateRoleAsync("AfkGate")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(roleId);
        // ENTITYSELF/end-frame precede asynchronous Loaded hooks. Finish that real
        // scene admission before this fixture manually cancels the attachment.
        await _loop.InvokeAsync(() => _owners.RunAsync("test AFK loaded fence", [ReservationKey.Realm], () =>
        {
            var selected = _sessions.All().Single(s => s.Role?.Id == roleId);
            Assert.Equal(RoleOwnerState.Attached, selected.Owner!.State);
            Assert.Null(selected.Handoff);
            return Task.CompletedTask;
        }));
        var completed = Signal();
        GameSession session = null!;
        WildAfkService.State state = null!;
        bool gateHeld = false;
        long accounted = 0;
        var stored = _roles.LoadWildAfk(roleId);
        try
        {
            await _loop.InvokeAsync(() =>
            {
                session = _sessions.All().Single(s => s.Role?.Id == roleId);
                state = session.WildAfk!;
                Assert.True(state.Gate.Wait(0));
                gateHeld = true;
                accounted = state.AccountedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1000;
                state.Info.HighMultiplierTimeSeconds++;
                afk.TickAttemptCompletedForTest = s => { if (ReferenceEquals(s, session)) completed.TrySetResult(); };
                afk.Tick(new GameTime(_loop.Now + TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1)));
                return Task.CompletedTask;
            });
            // The posted runtime callback reaches the held AFK gate before this queued barrier.
            await _loop.InvokeAsync(() => Task.CompletedTask);
            await _loop.InvokeAsync(() =>
            {
                _owners.BeginQuiescing(session.Owner!, session.Attachment!);
                session.Attachment!.Cancelled = true;
                state.Gate.Release(); gateHeld = false;
                return Task.CompletedTask;
            });
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await _loop.InvokeAsync(() =>
            {
                Assert.Same(state, session.WildAfk);
                Assert.Equal(accounted, state.AccountedAtMs);
                Assert.Equal(stored, _roles.LoadWildAfk(roleId));
                return Task.CompletedTask;
            });
            Assert.Empty(errors.Exceptions);
            await _loop.InvokeAsync(async () =>
            {
                session.Owner!.State = RoleOwnerState.Attached;
                session.Attachment!.Cancelled = false;
                await afk.ReconcileAsync(session);
                Assert.True(state.AccountedAtMs > accounted);
            });
        }
        finally
        {
            await _loop.InvokeAsync(() =>
            {
                afk.TickAttemptCompletedForTest = null;
                if (gateHeld) state.Gate.Release();
                if (session.Owner is { } owner && owner.State == RoleOwnerState.Quiescing)
                {
                    owner.State = RoleOwnerState.Attached;
                    session.Attachment!.Cancelled = false;
                }
                return Task.CompletedTask;
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementWaitsFirstAdmittedMutationAndOnlyNewestAttaches(bool thirdLogin)
    {
        await using var first = Bot();
        await first.LoginAsync("owner", "pw");
        await first.EnterGateAsync();
        long id = (await first.CreateRoleAsync("OwnerOne")).RoleBaseInfo.Id;
        var seeded = _roles.LoadProgress(id);
        seeded.Currency[1] = 500;
        _roles.SaveProgress(id, seeded);
        await first.EnterWorldAsync(id);
        byte[] setting = [0x10, 0x01, 0x08, 0x01];
        await first.SaveClientSettingAsync(10, setting);
        long startingZeny = _roles.LoadProgress(id).Currency[1];
        Assert.True(startingZeny >= 100);
        var before = _roles.ReadRevisionVector(id);

        var entered = Signal(); var release = Signal();
        var oldMutation = _loop.InvokeAsync(() => _owners.RunAsync("held old progress", [ReservationKey.Role(id)], async () =>
        {
            _owners.Current!.Pin(_owners.Get(id));
            var current = Assert.Single(_sessions.All());
            current.Position = new Vec3 { X = 170, Y = 1.5f, Z = 200 };
            entered.TrySetResult();
            await release.Task;
            var progress = _roles.LoadProgress(id);
            progress.Currency[1] = progress.Currency.GetValueOrDefault(1) - 100;
            _roles.SaveProgress(id, progress);
            current.Progress = progress;
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await using var second = Bot();
            await second.LoginAsync("owner", "pw");
            await second.EnterGateAsync();
            await using var third = Bot();
            if (thirdLogin)
            {
                await third.LoginAsync("owner", "pw");
                await third.EnterGateAsync();
            }
            var newest = thirdLogin ? third : second;
            var offered = Signal();
            newest.OnGameMessageOffered = msg => { if (msg == MsgId.LoginRoleSelect) offered.TrySetResult(); };
            var newestSelect = newest.EnterWorldAsync(id);
            await offered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await _loop.InvokeAsync(() =>
            {
                Assert.Equal(RoleOwnerState.Quiescing, _owners.Get(id).State);
                Assert.False(newestSelect.IsCompleted);
                return Task.CompletedTask;
            });
            release.TrySetResult();
            await oldMutation.WaitAsync(TimeSpan.FromSeconds(10));
            var self = await newestSelect.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal((ulong)id, self.Uuid);
            Assert.Equal(170f, self.MovementInfo.Pos.X);
            Assert.Equal(1.5f, self.MovementInfo.Pos.Y);
            Assert.Equal(200f, self.MovementInfo.Pos.Z);
            Assert.Equal(startingZeny - 100, _roles.LoadProgress(id).Currency[1]);
            var settings = DBSettingInfoIncrement.Parser.ParseFrom((await newest.Inbox.WaitAsync(
                e => e.IsGame(MsgId.AutoSyncSettings), TimeSpan.FromSeconds(5))).Body);
            Assert.Equal(setting, settings.ClientSetting[10].All.ToByteArray());
            await _loop.InvokeAsync(() =>
            {
                var winner = Assert.Single(_sessions.All());
                Assert.Equal(thirdLogin ? 3 : 2, winner.AccountGeneration);
                Assert.Equal(id, winner.Role?.Id);
                Assert.Equal(RoleOwnerState.Attached, _owners.Get(id).State);
                Assert.Same(winner.Attachment, _owners.Get(id).Attachment);
                Assert.True(_owners.HandoverConflicts >= 1);
                Assert.Equal(startingZeny - 100, winner.Progress?.Currency[1]);
                var durable = _roles.ReadRevisionVector(id);
                foreach (string part in new[] { "progress", "settings", "public_position" })
                {
                    Assert.True(durable.Revision(part) >= before.Revision(part));
                    Assert.Equal(durable.Revision(part), _owners.Get(id).Revisions[part]);
                }
                Assert.True(durable.Revision("progress") > before.Revision("progress"));
                Assert.True(durable.Revision("public_position") > before.Revision("public_position"));
                return Task.CompletedTask;
            });
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ForeignAndDeletedSelectionPreserveCurrentRoleAndDeleteRefusesAttachedRole()
    {
        await using var alice = Bot();
        await alice.LoginAsync("alice", "pw");
        await alice.EnterGateAsync();
        long kept = (await alice.CreateRoleAsync("AliceKeep")).RoleBaseInfo.Id;
        long removed = (await alice.CreateRoleAsync("AliceGone")).RoleBaseInfo.Id;
        await alice.EnterWorldAsync(kept);
        await using var bob = Bot();
        await bob.LoginAsync("bob", "pw");
        await bob.EnterGateAsync();
        long foreign = (await bob.CreateRoleAsync("BobRole")).RoleBaseInfo.Id;
        Assert.Equal(409, await Delete("alice", "pw", "AliceKeep"));
        Assert.Equal(0, await Delete("alice", "pw", "AliceGone"));

        async Task Refused(long target)
        {
            using var expected = alice.Inbox.Expect(e => e.IsGame(MsgId.LoginRoleSelect));
            await alice.SendGameAsync(MsgId.LoginRoleSelect, new AccountLoginSelectRoleReq
            { Token = alice.EngineSign, Account = alice.Account!, AreaId = 1, Id = target });
            var result = AccountLoginSelectRoleResp.Parser.ParseFrom((await expected.WaitAsync(TimeSpan.FromSeconds(5))).Body);
            Assert.NotEqual(0, result.Result);
            await _loop.InvokeAsync(() =>
            {
                var online = _sessions.All().Single(s => s.Account == "alice");
                Assert.Equal(kept, online.Role?.Id);
                Assert.Equal(RoleOwnerState.Attached, _owners.Get(kept).State);
                return Task.CompletedTask;
            });
        }
        await Refused(foreign);
        await Refused(removed);
        Assert.Equal((ulong)kept, (await alice.EnterWorldAsync(kept)).Uuid); // same-role reselect crosses its old fence
    }

    [Fact]
    public async Task DirectOtherRoleSelectionFencesOldReturnPointAndReplacesAttachment()
    {
        await using var bot = Bot();
        await bot.LoginAsync("directswitch", "pw");
        await bot.EnterGateAsync();
        long firstId = (await bot.CreateRoleAsync("FirstRole")).RoleBaseInfo.Id;
        long secondId = (await bot.CreateRoleAsync("SecondRole")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(firstId);
        var before = _roles.ReadRevisionVector(firstId);
        await _loop.InvokeAsync(() =>
        {
            Assert.Single(_sessions.All()).Position = new Vec3 { X = 174, Y = 1.5f, Z = 202 };
            return Task.CompletedTask;
        });
        var self = await bot.EnterWorldAsync(secondId);
        Assert.Equal((ulong)secondId, self.Uuid);
        Assert.Equal("SecondRole", self.Name);
        var old = _roles.Get(firstId)!;
        Assert.Equal(174f, old.X);
        Assert.Equal(202f, old.Z);
        Assert.True(_roles.ReadRevisionVector(firstId).Revision("public_position") > before.Revision("public_position"));
        await _loop.InvokeAsync(() =>
        {
            Assert.Equal(RoleOwnerState.Detached, _owners.Get(firstId).State);
            Assert.Null(_owners.Get(firstId).Attachment);
            Assert.Equal(RoleOwnerState.Attached, _owners.Get(secondId).State);
            Assert.Equal(secondId, Assert.Single(_sessions.All()).Role?.Id);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("BeforeAttach")]
    [InlineData("BeforePublish")]
    public async Task DisconnectDuringSelectNeverPublishesAnAttachment(string phase)
    {
        var bot = Bot();
        await bot.LoginAsync("disconnect", "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("DisconnectMe")).RoleBaseInfo.Id;
        var reached = Signal(); var release = Signal(); var unregistered = Signal();
        _handler.SelectionPhaseForTest = async (at, _) =>
        {
            if (at != phase) return;
            reached.TrySetResult();
            await release.Task;
        };
        _sessions.UnregisteredForTest = _ => unregistered.TrySetResult();
        var selecting = bot.EnterWorldAsync(id);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await bot.DisposeAsync(); }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<Exception>(() => selecting);
        await unregistered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await _loop.InvokeAsync(() =>
        {
            var owner = _owners.Cached(id);
            Assert.True(owner == null || owner.State == RoleOwnerState.Detached);
            Assert.Null(owner?.Attachment);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task FailedInitializationUsesTheCommonHandoverAndReleasesLoadingLease()
    {
        await using var bot = Bot();
        await bot.LoginAsync("failedinit", "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("FailedInit")).RoleBaseInfo.Id;
        var detached = Signal();
        _handler.SelectionPhaseForTest = (phase, _) => phase == "BeforePublish"
            ? Task.FromException(new IOException("injected initialization failure")) : Task.CompletedTask;
        _handover.PhaseForTest = (phase, _) => { if (phase == "Detached") detached.TrySetResult(); };
        await Assert.ThrowsAnyAsync<Exception>(() => bot.EnterWorldAsync(id));
        await detached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await _loop.InvokeAsync(() =>
        {
            var owner = _owners.Get(id);
            Assert.Equal(RoleOwnerState.Detached, owner.State);
            Assert.Null(owner.Attachment);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task FailedEarlyOtherRoleInitializationCannotCleanOldStateIntoNewRole()
    {
        await using var bot = Bot();
        await bot.LoginAsync("earlyinit", "pw");
        await bot.EnterGateAsync();
        long first = (await bot.CreateRoleAsync("EarlyFirst")).RoleBaseInfo.Id;
        long second = (await bot.CreateRoleAsync("EarlySecond")).RoleBaseInfo.Id;
        var guildStore = _app.Services.GetRequiredService<GuildStore>();
        var guild = new GuildStore.State { Name = "EarlyGuild" };
        guild.Members[first] = new GuildStore.Member { RoleId = first, Name = "EarlyFirst", Official = 1, Level = 10, Career = 1000 };
        guild.Members[second] = new GuildStore.Member { RoleId = second, Name = "EarlySecond", Level = 42, Career = 1000 };
        Assert.True(guildStore.Save(guild));
        _roles.SavePosition(second, RoleStore.SpawnSceneEntry, 130, 1.5f, 145, 0);
        var secondPosition = _roles.Get(second)!;
        Assert.Null(_roles.LoadDaily(second));

        await bot.EnterWorldAsync(first);
        await _loop.InvokeAsync(() =>
        {
            var active = Assert.Single(_sessions.All());
            Assert.NotNull(active.Daily);
            active.Daily!.DailyEpoch = 0; // a stale A boundary would write a B daily row
            active.Daily.WeeklyEpoch = 0;
            active.Progress!.Level = 77; // a stale A progress view would overwrite B's guild level
            active.Position = new Vec3 { X = 173, Y = 1.5f, Z = 201 };
            return Task.CompletedTask;
        });
        var detached = Signal();
        GameSession? failedSession = null;
        _handler.SelectionPhaseForTest = (phase, session) => phase == "AfterBindBeforeLoad" && session.Role?.Id == second
            ? FailEarly() : Task.CompletedTask;
        Task FailEarly()
        {
            failedSession = _sessions.All().Single();
            return Task.FromException(new IOException("injected early B loader failure"));
        }
        _handover.PhaseForTest = (phase, session) =>
        {
            if (phase == "Detached" && session.Account == "earlyinit" && session.Role == null) detached.TrySetResult();
        };
        await Assert.ThrowsAnyAsync<Exception>(() => bot.EnterWorldAsync(second));
        await detached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(173f, _roles.Get(first)!.X); // A's committed cleanup is retained
        var after = _roles.Get(second)!;
        Assert.Equal(secondPosition.X, after.X);
        Assert.Equal(secondPosition.Z, after.Z);
        Assert.Null(_roles.LoadDaily(second));
        Assert.Equal(42, guildStore.Get(guild.Id)!.Members[second].Level);
        await _loop.InvokeAsync(() =>
        {
            Assert.NotNull(failedSession);
            Assert.Null(failedSession.Role);
            Assert.Null(failedSession.Progress);
            Assert.Null(failedSession.Daily);
            Assert.Null(failedSession.WildAfk);
            Assert.Equal(RoleOwnerState.Detached, _owners.Get(second).State);
            return Task.CompletedTask;
        });
        _handler.SelectionPhaseForTest = null;
        await using var retry = Bot();
        await retry.LoginAsync("earlyinit", "pw");
        await retry.EnterGateAsync();
        Assert.Equal((ulong)second, (await retry.EnterWorldAsync(second)).Uuid);
    }

    [Fact]
    public async Task ReplacedAccountStageRequestCannotCreateRoleAfterItsRealmWait()
    {
        var old = Bot();
        await old.LoginAsync("createfence", "pw");
        await old.EnterGateAsync();
        await using var newest = Bot();
        await newest.LoginAsync("createfence", "pw");
        var entered = Signal(); var release = Signal(); var deferred = Signal();
        _owners.DeferredForTest = origin => { if (origin == "dispatcher") deferred.TrySetResult(); };
        var holding = _loop.InvokeAsync(() => _owners.RunAsync("held account Realm", [ReservationKey.Realm], async () =>
        {
            entered.TrySetResult();
            await release.Task;
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await old.SendGameAsync(MsgId.LoginRoleCreate, new AccountLoginCreateRoleReq
            { Token = old.EngineSign, Account = old.Account!, AreaId = 1, Career = 1000, RoleName = "StaleCreate", Sex = 1 });
            await deferred.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await newest.EnterGateAsync();
            release.TrySetResult();
            await holding.WaitAsync(TimeSpan.FromSeconds(10));
            await _loop.InvokeAsync(() =>
            {
                Assert.True(_sessions.IsWinner(Assert.Single(_sessions.All())));
                Assert.Empty(_roles.ForAccount("createfence"));
                return Task.CompletedTask;
            });
        }
        finally { release.TrySetResult(); await old.DisposeAsync(); }
    }

    [Fact]
    public async Task OfflineMailOfferedDuringHandoverWaitsForItsDurableFence()
    {
        var bot = Bot();
        await bot.LoginAsync("mailfence", "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("MailFence")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        // Do not block the final SceneLoaded owner scope before the socket can observe disconnect.
        await bot.Inbox.WaitAsync(e => e.IsGame(GuildService.SceneGuildId), TimeSpan.FromSeconds(5));
        var entered = Signal(); var release = Signal(); var quiescing = Signal();
        _handover.PhaseForTest = (phase, session) =>
        {
            if (phase == "Quiescing" && session.Role?.Id == id) quiescing.TrySetResult();
        };
        var admitted = _loop.InvokeAsync(() => _owners.RunAsync("held mail predecessor", [ReservationKey.Role(id)], async () =>
        {
            _owners.Current!.Pin(_owners.Get(id));
            entered.TrySetResult();
            await release.Task;
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await bot.DisposeAsync();
            await quiescing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var delivery = _mail.SendRawAsync(id, "Offline", "After handover");
            Assert.False(delivery.IsCompleted);
            release.TrySetResult();
            await admitted.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(string.IsNullOrEmpty(await delivery.WaitAsync(TimeSpan.FromSeconds(10))));
            Assert.NotNull(_roles.LoadMailbox(id));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task EssentialCleanupFaultRetainsLeaseButLaterActivityUnlinksStillRun()
    {
        var bot = Bot();
        await bot.LoginAsync("cleanup", "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("CleanupRole")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        GameSession session = null!;
        await _loop.InvokeAsync(() =>
        {
            session = Assert.Single(_sessions.All());
            session.DungeonSoloMatch = true;
            session.EventFaction = 7;
            _handler.CleanupFailureForTest = (step, _) => step == "festival" ? new IOException("injected festival unlink failure") : null;
            return Task.CompletedTask;
        });
        // A client message that reaches the cancelled attachment ends the connection (by design), so the server may
        // close it before the bot does: observe the unregistration from here on.
        var unregistered = Signal();
        _sessions.UnregisteredForTest = old => { if (ReferenceEquals(old, session)) unregistered.TrySetResult(); };
        await Assert.ThrowsAnyAsync<Exception>(() => _loop.InvokeAsync(() => _handover.Begin(session)));
        await _loop.InvokeAsync(() =>
        {
            Assert.Equal(RoleOwnerState.RecoveryRequired, _owners.Get(id).State);
            Assert.NotNull(_owners.Get(id).Attachment);
            Assert.False(session.DungeonSoloMatch);
            Assert.Equal(0, session.EventFaction);
            return Task.CompletedTask;
        });
        Assert.Equal(409, await Delete("cleanup", "pw", "CleanupRole"));
        await bot.DisposeAsync();
        await unregistered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await _loop.InvokeAsync(() =>
        {
            Assert.Empty(_sessions.All());
            Assert.Equal(RoleOwnerState.RecoveryRequired, _owners.Get(id).State);
            Assert.NotNull(_owners.Get(id).Attachment);
            return Task.CompletedTask;
        });
        Assert.Equal(409, await Delete("cleanup", "pw", "CleanupRole"));
    }

    [Fact]
    public async Task DeleteWaitsAdmittedOfflineItemAndMailThenRejectsNewGrantsAndReusesName()
    {
        var bot = Bot();
        await bot.LoginAsync("offline", "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("OfflineOne")).RoleBaseInfo.Id;
        var disconnected = Signal();
        _sessions.UnregisteredForTest = _ => disconnected.TrySetResult();
        await bot.DisposeAsync();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var entered = Signal(); var release = Signal(); var deleteDeferred = Signal();
        await _loop.InvokeAsync(() =>
        {
            _owners.DeferredForTest = origin => { if (origin == "HTTP delete") deleteDeferred.TrySetResult(); };
            return Task.CompletedTask;
        });
        var grant = _loop.InvokeAsync(() => _owners.RunAsync("offline grant", [ReservationKey.Realm], async () =>
        {
            entered.TrySetResult();
            await release.Task;
            _items.Insert(id, new InvItem { Entry = 1004000, Stack = 1, Pack = 1 });
            await _mail.SendRawAsync(id, "Old", "Admitted before deletion");
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<int>? deleting = null;
        try
        {
            deleting = Delete("offline", "pw", "OfflineOne");
            await deleteDeferred.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(deleting.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await grant.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await deleting!.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Single(_items.Load(id));
        Assert.NotNull(_roles.LoadMailbox(id));
        var deleted = _roles.ReadRevisionVector(id);
        Assert.True(deleted.Deleted);
        Assert.Equal(2, deleted.Epoch);
        Assert.ThrowsAny<Exception>(() => _items.Insert(id, new InvItem { Entry = 1004000, Stack = 1, Pack = 1 }));
        await Assert.ThrowsAnyAsync<Exception>(() => _mail.SendRawAsync(id, "Late", "Must refuse"));
        var replacement = _roles.Create("offline", 1, "OfflineOne", 1, 1000, 1);
        Assert.NotNull(replacement);
        Assert.NotEqual(id, replacement!.Id);
        Assert.Equal(1, _roles.ReadRevisionVector(replacement.Id).Epoch);
    }

    [Fact]
    public async Task DeleteWaitsSelectingRoleBeforeCheckingWhetherItIsOnline()
    {
        await using var bot = Bot();
        await bot.LoginAsync("selectdelete", "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("SelectDelete")).RoleBaseInfo.Id;
        var selectingEntered = Signal(); var release = Signal(); var deleteDeferred = Signal();
        _handler.SelectionPhaseForTest = async (phase, _) =>
        {
            if (phase != "BeforeAttach") return;
            selectingEntered.TrySetResult();
            await release.Task;
        };
        _owners.DeferredForTest = origin => { if (origin == "HTTP delete") deleteDeferred.TrySetResult(); };
        var selection = bot.EnterWorldAsync(id);
        await selectingEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<int>? deletion = null;
        try
        {
            deletion = Delete("selectdelete", "pw", "SelectDelete");
            await deleteDeferred.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(deletion.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal((ulong)id, (await selection.WaitAsync(TimeSpan.FromSeconds(15))).Uuid);
        Assert.Equal(409, await deletion!.WaitAsync(TimeSpan.FromSeconds(10)));
        await _loop.InvokeAsync(() =>
        {
            Assert.Equal(RoleOwnerState.Attached, _owners.Get(id).State);
            Assert.False(_roles.ReadRevisionVector(id).Deleted);
            return Task.CompletedTask;
        });
    }
}
