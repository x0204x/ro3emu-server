using Microsoft.Data.Sqlite;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Diagnostics;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class PersistenceRoutingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-persistence-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private readonly FriendStore _friends;

    public PersistenceRoutingTests()
    {
        _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _dir }));
        _ = new RoleStore(_accounts);
        _friends = new FriendStore(_accounts);
        foreach (long role in new long[] { 1, 2, 3, 13 })
            FixtureSql.Execute(_accounts, """
                INSERT INTO role(id,account,area,name,sex,career,level,hair,create_time,last_login_time)
                VALUES($id,'friends-fixture',1,$name,1,1000,1,1,0,0)
                """, ("$id", role), ("$name", $"FriendsFixture{role}"));
        _accounts.WriteSync<int>(new RoleSchemaPlan());
    }

    public void Dispose()
    {
        _accounts.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task ConcurrentAccountRegistrationAndTokensAreSerializedAndDurable()
    {
        var attempts = await Task.WhenAll(_accounts.LoginAsync("same", "alpha"), _accounts.LoginAsync("same", "beta"));
        Assert.Contains(AccountStore.LoginResult.Registered, attempts);
        Assert.Contains(AccountStore.LoginResult.BadPassword, attempts);
        string winning = await _accounts.VerifyAsync("same", "alpha") ? "alpha" : "beta";
        Assert.True(await _accounts.VerifyAsync("same", winning));
        Assert.False(await _accounts.VerifyAsync("same", winning == "alpha" ? "beta" : "alpha"));

        FixtureSql.Execute(_accounts, "INSERT INTO auth_token(token,kind,account,expires_utc) VALUES('expired','sdk','same',0)");
        var sdkIssues = Enumerable.Range(0, 8).Select(_ => _accounts.IssueSdkTokenAsync("same")).ToArray();
        var engineIssues = Enumerable.Range(0, 8).Select(_ => _accounts.IssueEngineSignAsync("same")).ToArray();
        string[] sdkTokens = await Task.WhenAll(sdkIssues), engineTokens = await Task.WhenAll(engineIssues);
        string sdk = sdkTokens[0], engine = engineTokens[0];
        Assert.Equal(8, sdkTokens.Distinct().Count());
        Assert.Equal(8, engineTokens.Distinct().Count());
        Assert.Matches("^[0-9a-f]{48}$", sdk);
        Assert.Matches("^[0-9a-f]{32}$", engine);
        Assert.Equal("same", await _accounts.ResolveSdkTokenAsync(sdk));
        Assert.Equal("same", await _accounts.ResolveEngineSignAsync(engine));
        Assert.Null(await _accounts.ResolveSdkTokenAsync(engine));
        Assert.Null(await _accounts.ResolveSdkTokenAsync("expired"));
        var resolved = await Task.WhenAll(sdkTokens.Select(_accounts.ResolveSdkTokenAsync));
        Assert.All(resolved, value => Assert.Equal("same", value));
        using var verify = _accounts.Open();
        using var count = verify.CreateCommand();
        count.CommandText = "SELECT count(*) FROM auth_token WHERE token='expired'";
        Assert.Equal(0L, count.ExecuteScalar());
    }

    [Fact]
    public void FriendGroupAndBlockFailuresRollbackTheWholePlan()
    {
        _friends.Link(1, 2);
        _friends.Apply(1, 2);
        _friends.Apply(2, 1);
        FixtureSql.Execute(_accounts,
            "CREATE TRIGGER refuse_member BEFORE INSERT ON friend_chat_member WHEN NEW.role=13 BEGIN SELECT RAISE(FAIL,'member'); END");
        Assert.Throws<SqliteException>(() => _friends.CreateGroup(1, "Crew", [1, 13]));
        using (var db = _accounts.Open())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM friend_chat_group";
            Assert.Equal(0L, cmd.ExecuteScalar());
        }
        FixtureSql.Execute(_accounts, "DROP TRIGGER refuse_member");
        var group = _friends.CreateGroup(1, "Crew", [1, 2]);
        string paddedId = group.Id.PadLeft(4, '0');
        Assert.Equal(paddedId, _friends.Group(paddedId)!.Id);
        FixtureSql.Execute(_accounts,
            "CREATE TRIGGER refuse_change BEFORE INSERT ON friend_chat_member WHEN NEW.role=13 BEGIN SELECT RAISE(FAIL,'change'); END");
        Assert.Throws<SqliteException>(() => _friends.ChangeMembers(group.Id, [3L, 13L], true));
        Assert.DoesNotContain(_friends.Group(group.Id)!.Members, member => member.Role == 3);
        FixtureSql.Execute(_accounts, "DROP TRIGGER refuse_change");
        FixtureSql.Execute(_accounts,
            "CREATE TRIGGER refuse_dismiss BEFORE DELETE ON friend_chat_group BEGIN SELECT RAISE(FAIL,'dismiss'); END");
        Assert.Throws<SqliteException>(() => _friends.DismissGroup(group.Id));
        Assert.Equal(2, _friends.Group(group.Id)!.Members.Count);
        FixtureSql.Execute(_accounts,
            "DROP TRIGGER refuse_dismiss; CREATE TRIGGER refuse_unlink BEFORE DELETE ON friend WHEN OLD.owner=2 BEGIN SELECT RAISE(FAIL,'unlink'); END");
        Assert.Throws<SqliteException>(() => _friends.Block(1, 2));
        Assert.False(_friends.Blocks(1, 2));
        Assert.True(_friends.AreFriends(1, 2));
        Assert.True(_friends.AreFriends(2, 1));
        Assert.True(_friends.HasApplied(1, 2));
        Assert.True(_friends.HasApplied(2, 1));
    }

    [Fact]
    public async Task QueuedPlansOwnCopiesOfProtobufBytesAndCollections()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = _accounts.WriteAsync<int>(new HoldWritePlan(entered, release));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var metadata = new DBFriendSystemInfo { TopChats = new FriendChatRecordList() };
            metadata.TopChats.Items.Add(new FriendChatListItem { RoleId = 2 });
            var save = _friends.SaveMetadataAsync(1, metadata);
            metadata.TopChats.Items[0].RoleId = 3;
            release.Set();
            await hold;
            await save;
            Assert.Equal(2, Assert.Single(_friends.LoadMetadata(1).TopChats.Items).RoleId);
        }
        finally { release.Set(); }

        using var rawEntered = new ManualResetEventSlim();
        using var rawRelease = new ManualResetEventSlim();
        var rawHold = _accounts.WriteAsync<int>(new HoldWritePlan(rawEntered, rawRelease));
        Assert.True(rawEntered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            byte[] bytes = [8, 7];
            var plan = new FriendWritePlan(FriendWriteKind.SaveMetadata, 2, blob: bytes);
            plan.Expect([_accounts.ReadSync<RoleRevisionVector>(new RoleRevisionReadPlan(2))]);
            var write = _accounts.WriteAsync<bool>(plan);
            bytes[1] = 9;
            rawRelease.Set();
            await rawHold;
            await write;
            using var db = _accounts.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT data FROM friend_metadata WHERE role=2";
            Assert.Equal(new byte[] { 8, 7 }, (byte[])cmd.ExecuteScalar()!);
        }
        finally { rawRelease.Set(); }

        var group = _friends.CreateGroup(1, "Crew", [1]);
        string padded = group.Id.PadLeft(4, '0');
        Assert.Equal(padded, (await _friends.GroupAsync(padded))!.Id);
        using var entered2 = new ManualResetEventSlim();
        using var release2 = new ManualResetEventSlim();
        var hold2 = _accounts.WriteAsync<int>(new HoldWritePlan(entered2, release2));
        Assert.True(entered2.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var members = new List<long> { 2 };
            var plan = new FriendWritePlan(FriendWriteKind.ChangeMembers, long.Parse(group.Id), value: 1, members: members);
            plan.ResolveRoleWrites(_accounts);
            plan.Expect(plan.RolePins.Select(role => _accounts.ReadSync<RoleRevisionVector>(new RoleRevisionReadPlan(role))));
            plan.ExpectEntities(plan.EntityWrites.Select(key => _accounts.ReadSync<OwnerRevisionValue>(new OwnerRevisionReadPlan(key))));
            var change = _accounts.WriteAsync<bool>(plan);
            members[0] = 3;
            release2.Set();
            await hold2;
            await change;
            Assert.Contains(_friends.Group(group.Id)!.Members, m => m.Role == 2);
            Assert.DoesNotContain(_friends.Group(group.Id)!.Members, m => m.Role == 3);
        }
        finally { release2.Set(); }
    }

    [Fact]
    public async Task DedicatedWorkersAreReadOnlyAndBounded()
    {
        var metrics = new DbAccessMetrics();
        using var writer = new DbWriter($"Data Source={Path.Combine(_dir, "ro3emu.db")}", metrics);
        using var readers = new DbReadPool(Path.Combine(_dir, "ro3emu.db"), metrics);
        int caller = Environment.CurrentManagedThreadId;
        var write = await writer.ExecuteAsync<(int Thread, long Sync)>(new WriterProbePlan());
        Assert.NotEqual(caller, write.Thread);
        Assert.Equal(2L, write.Sync); // PRAGMA synchronous=FULL
        var reads = await Task.WhenAll(readers.ExecuteAsync<(int Thread, long QueryOnly)>(new ReaderProbePlan()),
            readers.ExecuteAsync<(int Thread, long QueryOnly)>(new ReaderProbePlan()));
        Assert.All(reads, r => { Assert.NotEqual(caller, r.Thread); Assert.Equal(1L, r.QueryOnly); });
        await Assert.ThrowsAsync<SqliteException>(() => readers.ExecuteAsync<bool>(new ForbiddenReadWritePlan()));

        using (var bothEntered = new CountdownEvent(2))
        using (var readRelease = new ManualResetEventSlim())
        {
            var active = new[] { readers.ExecuteAsync<int>(new HoldReadPlan(bothEntered, readRelease)),
                readers.ExecuteAsync<int>(new HoldReadPlan(bothEntered, readRelease)) };
            Assert.True(bothEntered.Wait(TimeSpan.FromSeconds(5)));
            try
            {
                var waiting = Enumerable.Range(0, DbReadPool.MaxWaitingJobs)
                    .Select(_ => readers.ExecuteAsync<(int Thread, long QueryOnly)>(new ReaderProbePlan())).ToArray();
                await Assert.ThrowsAsync<DbQueueFullException>(() => readers.ExecuteAsync<int>(new HoldReadPlan(bothEntered, readRelease)));
                readRelease.Set();
                var actual = await Task.WhenAll(active);
                Assert.Equal(2, actual.Distinct().Count());
                await Task.WhenAll(waiting);
            }
            finally { readRelease.Set(); }
        }

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hold = writer.ExecuteAsync<int>(new HoldWritePlan(entered, release));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var pending = Enumerable.Range(0, DbWriter.MaxDurableJobs - 1).Select(_ => writer.ExecuteAsync<int>(new NoopWritePlan())).ToArray();
            await Assert.ThrowsAsync<DbQueueFullException>(() => writer.ExecuteAsync<int>(new NoopWritePlan()));
            await Assert.ThrowsAsync<DbQueueFullException>(() => writer.ExecuteAsync<int>(new HugeWritePlan()));
            release.Set();
            await hold;
            await Task.WhenAll(pending);
        }
        finally { release.Set(); }

        using var entered2 = new ManualResetEventSlim();
        using var release2 = new ManualResetEventSlim();
        var hold2 = writer.ExecuteAsync<int>(new HoldWritePlan(entered2, release2));
        Assert.True(entered2.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            await Assert.ThrowsAsync<DbQueueFullException>(() => writer.ExecuteAsync<int>(new HugeWritePlan()));
            await Assert.ThrowsAsync<DbQueueFullException>(() => writer.ExecuteAsync<int>(new HugeWritePlan(), lazy: true));
            var nearlyFull = writer.ExecuteAsync<int>(new SizedWritePlan(DbWriter.MaxDurableBytes - 64));
            await Assert.ThrowsAsync<DbQueueFullException>(() => writer.ExecuteAsync<int>(new NoopWritePlan()));
            var lazyFull = writer.ExecuteAsync<int>(new SizedWritePlan(DbWriter.MaxLazyBytes), lazy: true);
            await Assert.ThrowsAsync<DbQueueFullException>(() => writer.ExecuteAsync<int>(new NoopWritePlan(), lazy: true));
            release2.Set();
            await Task.WhenAll(hold2, nearlyFull, lazyFull);
        }
        finally { release2.Set(); }
        Assert.False(writer.WorkerExited.IsCompleted);
        writer.Dispose();
        readers.Dispose();
        await Task.WhenAll(writer.WorkerExited, readers.WorkersExited);
        var snapshot = System.Text.Json.JsonSerializer.Serialize(metrics.Snapshot());
        using var doc = System.Text.Json.JsonDocument.Parse(snapshot);
        Assert.Equal(0, doc.RootElement.GetProperty("writerJobs").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("readerJobs").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("writerStopTimedOut").GetInt64());
        Assert.Equal(0, doc.RootElement.GetProperty("readerStopTimedOut").GetInt64());
    }

    [Fact]
    public async Task WriterStopLetsAnAcceptedTransactionFinishOnItsOwnConnection()
    {
        var metrics = new DbAccessMetrics();
        using var writer = new DbWriter($"Data Source={Path.Combine(_dir, "ro3emu.db")}", metrics);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var accepted = writer.ExecuteAsync<int>(new HoldWritePlan(entered, release));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var stopping = Task.Run(writer.Dispose);
            Assert.True(SpinWait.SpinUntil(() => writer.StopRequested, TimeSpan.FromSeconds(5)));
            Assert.False(writer.WorkerExited.IsCompleted);
            release.Set();
            Assert.Equal(1, await accepted);
            await stopping;
            await writer.WorkerExited;
            await Assert.ThrowsAsync<ObjectDisposedException>(() => writer.ExecuteAsync<int>(new NoopWritePlan()));
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task ForcedStopTimeoutReportsDebtAndWorkersStillFinishAcceptedWork()
    {
        var metrics = new DbAccessMetrics();
        var log = new RecordingLogger();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var writer = new DbWriter($"Data Source={Path.Combine(_dir, "ro3emu.db")}", metrics,
            log: log, stopTimeout: TimeSpan.Zero);
        var accepted = writer.ExecuteAsync<int>(new HoldWritePlan(entered, release));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            writer.Dispose();
            Assert.False(writer.WorkerExited.IsCompleted);
            Assert.Contains(log.Messages, message => message.Contains("writer shutdown timed out") && message.Contains("test/hold"));
            using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(metrics.Snapshot()));
            Assert.Equal(1, doc.RootElement.GetProperty("writerStopTimedOut").GetInt64());
            Assert.Equal(1, doc.RootElement.GetProperty("writerJobs").GetInt32());
            release.Set();
            Assert.Equal(1, await accepted);
            await writer.WorkerExited.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); }

        using var readEntered = new CountdownEvent(1);
        using var readRelease = new ManualResetEventSlim();
        using var readers = new DbReadPool(Path.Combine(_dir, "ro3emu.db"), metrics,
            log: log, stopTimeout: TimeSpan.Zero);
        var snapshot = readers.ExecuteAsync<int>(new HoldReadPlan(readEntered, readRelease));
        Assert.True(readEntered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            readers.Dispose();
            Assert.False(readers.WorkersExited.IsCompleted);
            Assert.Contains(log.Messages, message => message.Contains("reader shutdown timed out") && message.Contains("test/read-hold"));
            using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(metrics.Snapshot()));
            Assert.Equal(1, doc.RootElement.GetProperty("readerStopTimedOut").GetInt64());
            Assert.Equal(1, doc.RootElement.GetProperty("readerJobs").GetInt32());
            readRelease.Set();
            await snapshot;
            await readers.WorkersExited.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { readRelease.Set(); }
    }

    [Fact]
    public async Task AccountStoreDefersWorkerShutdownUntilAdmittedLoopContinuationFinishes()
    {
        using var metrics = new ServerMetrics(observeRuntime: false);
        var loop = new GameLoop(NullLogger<GameLoop>.Instance, metrics);
        using var store = new AccountStore(Options.Create(new ServerOptions { DataDir = Path.Combine(_dir, "drain") }), metrics, loop);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await loop.StartAsync(default);
        try
        {
            var admitted = loop.InvokeAsync(async () =>
            {
                entered.TrySetResult();
                await release.Task;
                var login = store.Login("late-account", "pw");
                var token = store.IssueSdkToken("late-account");
                return (login, store.ResolveSdkToken(token));
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await loop.StopAsync(cancelled.Token);
            Assert.Equal(GameLoopPhase.Draining, loop.DrainStatus.Phase);
            store.Dispose();
            release.TrySetResult();
            var result = await admitted.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(AccountStore.LoginResult.Registered, result.login);
            Assert.Equal("late-account", result.Item2);
            await loop.WorkerExited.WaitAsync(TimeSpan.FromSeconds(5));
            await store.PersistenceWorkersExited.WaitAsync(TimeSpan.FromSeconds(5));
            using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(store.AccessMetrics.Snapshot()));
            Assert.Equal(0, doc.RootElement.GetProperty("writerJobs").GetInt32());
            Assert.Equal(0, doc.RootElement.GetProperty("writerStopTimedOut").GetInt64());
        }
        finally { release.TrySetResult(); loop.Dispose(); }
    }

    [Fact]
    public void GiftEnlistedFriendWritesRollbackWithInventoryPayment()
    {
        var roles = new RoleStore(_accounts);
        var items = new ItemStore(_accounts);
        _friends.Link(1, 2);
        var gift = new InvItem { Entry = 1004000, Stack = 1, Pack = 1, Sort = 1 };
        items.Insert(1, gift);
        var sender = new DBFriendSystemInfo { TopChats = new FriendChatRecordList() };
        var receiver = new DBFriendSystemInfo { TopChats = new FriendChatRecordList() };
        sender.TopChats.Items.Add(new FriendChatListItem { RoleId = 2 });
        receiver.TopChats.Items.Add(new FriendChatListItem { RoleId = 1 });
        byte[] senderBytes = sender.ToByteArray(), receiverBytes = receiver.ToByteArray();
        FixtureSql.Execute(_accounts,
            "CREATE TRIGGER refuse_gift BEFORE UPDATE ON friend WHEN NEW.owner=2 BEGIN SELECT RAISE(FAIL,'gift'); END");
        Assert.Throws<SqliteException>(() => roles.WithTransaction(tx =>
        {
            gift.Stack = 0;
            tx.ItemUpdate(gift);
            tx.FriendMetadata(1, senderBytes);
            tx.FriendMetadata(2, receiverBytes);
            tx.FriendIntimacy(1, 2, 1);
            tx.FriendIntimacy(2, 1, 1);
        }));
        Assert.Equal(1, Assert.Single(items.Load(1)).Stack);
        Assert.Empty(_friends.LoadMetadata(1).TopChats.Items);
        Assert.Empty(_friends.LoadMetadata(2).TopChats.Items);
        Assert.Equal(0, _friends.Intimacy(1, 2));
        Assert.Equal(0, _friends.Intimacy(2, 1));
    }

    [Fact]
    public async Task LegacySyncCountsActualLoopBlockedWait()
    {
        using var metrics = new ServerMetrics(observeRuntime: false);
        var loop = new GameLoop(NullLogger<GameLoop>.Instance, metrics);
        using var store = new AccountStore(Options.Create(new ServerOptions { DataDir = Path.Combine(_dir, "loop") }), metrics, loop);
        await loop.StartAsync(default);
        try
        {
            Assert.Equal(AccountStore.LoginResult.Registered,
                await loop.InvokeAsync(() => Task.FromResult(store.Login("loop-account", "pw"))));
            var snapshot = System.Text.Json.JsonSerializer.Serialize(store.AccessMetrics.Snapshot());
            using var doc = System.Text.Json.JsonDocument.Parse(snapshot);
            Assert.Equal(1, doc.RootElement.GetProperty("legacyLoopSync").GetInt64());
            Assert.True(doc.RootElement.GetProperty("legacyLoopBlockedMs").GetDouble() > 0);
            Assert.Equal(0, doc.RootElement.GetProperty("legacyWriterLoopSync").GetInt64());
            _ = new RoleStore(store);
            foreach (long role in new long[] { 1, 2 })
                FixtureSql.Execute(store, """
                    INSERT INTO role(id,account,area,name,sex,career,level,hair,create_time,last_login_time)
                    VALUES($id,'loop-fixture',1,$name,1,1000,1,1,0,0)
                    """, ("$id", role), ("$name", $"LoopFixture{role}"));
            store.WriteSync<int>(new RoleSchemaPlan());
            var friends = new FriendStore(store);
            await loop.InvokeAsync(() =>
            {
                friends.Link(1, 2);
                Assert.Equal(0, friends.Intimacy(1, 2));
                return Task.CompletedTask;
            });
            using var routed = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(store.AccessMetrics.Snapshot()));
            Assert.Equal(5, routed.RootElement.GetProperty("legacyLoopSync").GetInt64());
            Assert.Equal(1, routed.RootElement.GetProperty("legacyWriterLoopSync").GetInt64());
            Assert.True(routed.RootElement.GetProperty("legacyWriterLoopBlockedMs").GetDouble() > 0);
            Assert.True(routed.RootElement.GetProperty("legacyLoopBlockedMs").GetDouble()
                >= routed.RootElement.GetProperty("legacyWriterLoopBlockedMs").GetDouble());
        }
        finally { await loop.StopAsync(default); loop.Dispose(); }
    }

    private sealed class HoldWritePlan(ManualResetEventSlim entered, ManualResetEventSlim release) : DbPlan
    {
        internal override int Bytes => 64;
        internal override string Label => "test/hold";
        internal override object Execute(SqliteConnection db, SqliteTransaction tx)
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Writer test handshake timed out.");
            return 1;
        }
    }
    private sealed class NoopWritePlan : DbPlan
    {
        internal override int Bytes => 64;
        internal override string Label => "test/noop";
        internal override object Execute(SqliteConnection db, SqliteTransaction tx) => 1;
    }
    private sealed class SizedWritePlan : DbPlan
    {
        private readonly byte[] _payload;
        internal SizedWritePlan(int bytes) => _payload = new byte[bytes];
        internal override int Bytes => _payload.Length;
        internal override string Label => "test/sized";
        internal override object Execute(SqliteConnection db, SqliteTransaction tx) => _payload.Length;
    }
    private sealed class HugeWritePlan : DbPlan
    {
        internal override int Bytes => DbWriter.MaxDurableBytes + 1;
        internal override string Label => "test/huge";
        internal override object Execute(SqliteConnection db, SqliteTransaction tx) => 1;
    }
    private sealed class WriterProbePlan : DbPlan
    {
        internal override int Bytes => 64;
        internal override string Label => "test/writer-probe";
        internal override object Execute(SqliteConnection db, SqliteTransaction tx)
        {
            using var cmd = DbSql.Command(db, tx, "PRAGMA synchronous");
            return (Environment.CurrentManagedThreadId, (long)cmd.ExecuteScalar()!);
        }
    }
    private sealed class ReaderProbePlan : DbReadPlan
    {
        internal override int Bytes => 64;
        internal override string Label => "test/reader-probe";
        internal override object Execute(SqliteConnection db, SqliteTransaction tx)
        {
            using var cmd = DbSql.Command(db, tx, "PRAGMA query_only");
            return (Environment.CurrentManagedThreadId, (long)cmd.ExecuteScalar()!);
        }
    }
    private sealed class ForbiddenReadWritePlan : DbReadPlan
    {
        internal override int Bytes => 64;
        internal override string Label => "test/read-write";
        internal override object Execute(SqliteConnection db, SqliteTransaction tx)
        {
            using var cmd = DbSql.Command(db, tx, "INSERT INTO account(name,password_hash,salt,created_utc) VALUES('forbidden',X'00',X'00','now')");
            cmd.ExecuteNonQuery();
            return true;
        }
    }
    private sealed class HoldReadPlan(CountdownEvent entered, ManualResetEventSlim release) : DbReadPlan
    {
        internal override int Bytes => 64;
        internal override string Label => "test/read-hold";
        internal override object Execute(SqliteConnection db, SqliteTransaction tx)
        {
            entered.Signal();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Reader test handshake timed out.");
            return Environment.CurrentManagedThreadId;
        }
    }
    private sealed class RecordingLogger : ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();
        public string[] Messages => [.. _messages];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => _messages.Enqueue(formatter(state, exception));
    }
}
