using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class DbWriteGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-d1-" + Guid.NewGuid().ToString("N"));
    private AccountStore Store() => new(Options.Create(new ServerOptions { DataDir = _dir }));
    private static JsonElement Metrics(AccountStore accounts) => JsonSerializer.SerializeToElement(accounts.AccessMetrics.Snapshot());
    private static long Counter(AccountStore accounts, string name) => Metrics(accounts).GetProperty(name).GetInt64();

    [Fact]
    public void CompatibilityHandleDeniesDmlDdlAndPragmaBypassesAndWriterStillWorks()
    {
        using var accounts = Store();
        var roles = new RoleStore(accounts);
        var role = roles.Create("guard", 1, "Guard", 1, 1000, 1)!;
        using var read = accounts.Open();
        foreach (string sql in new[] { "UPDATE role SET level=99", "CREATE TABLE bypass(id INTEGER)",
            "PRAGMA query_only=OFF", "PRAGMA writable_schema=ON", "PRAGMA wal_checkpoint", "ATTACH ':memory:' AS bypass" })
        {
            using var command = read.CreateCommand();
            command.CommandText = sql;
            Assert.Equal(23, Assert.Throws<SqliteException>(() => command.ExecuteNonQuery()).SqliteErrorCode);
        }
        Assert.Equal(6, Counter(accounts, "nonWriterWriteAttempts"));
        Assert.Equal(0, Counter(accounts, "writeGuardErrors"));
        Assert.Equal(1, roles.Get(role.Id)!.Level);
        roles.SaveSettings(role.Id, [1, 2, 3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, roles.LoadSettings(role.Id));
        Assert.Equal(6, Metrics(accounts).GetProperty("writeDenials").GetArrayLength());
    }

    private sealed class BadReadPlan : DbReadPlan
    {
        internal override int Bytes => 64;
        internal override string Label => "test/illegal-read-write";
        internal override object Execute(SqliteConnection db, SqliteTransaction tx)
        {
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "DELETE FROM account";
            return command.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task ReaderWorkerDeniesMisclassifiedPlanAndRemainsUsable()
    {
        using var accounts = Store();
        Assert.Equal(AccountStore.LoginResult.Registered, await accounts.LoginAsync("survivor", "pw"));
        Assert.Equal(23, (await Assert.ThrowsAsync<SqliteException>(() => accounts.ReadAsync<int>(new BadReadPlan()))).SqliteErrorCode);
        Assert.True(await accounts.VerifyAsync("survivor", "pw"));
        Assert.Equal(1, Counter(accounts, "nonWriterWriteAttempts"));
        Assert.Equal("test/illegal-read-write", Metrics(accounts).GetProperty("writeDenials")[0].GetProperty("source").GetString());
        Assert.Equal(0, Counter(accounts, "writeGuardErrors"));
    }

    private sealed class WrongThreadPlan : DbPlan
    {
        internal override int Bytes => 64;
        internal override string Label => "test/foreign-thread";
        internal override object Execute(SqliteConnection db, SqliteTransaction tx)
        {
            int code = 0;
            Exception? unexpected = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using var command = db.CreateCommand();
                    command.Transaction = tx;
                    command.CommandText = "DELETE FROM account";
                    command.ExecuteNonQuery();
                }
                catch (SqliteException error) { code = error.SqliteErrorCode; }
                catch (Exception error) { unexpected = error; }
            }) { IsBackground = true };
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("D1 wrong-thread probe did not finish.");
            if (unexpected != null) throw unexpected;
            return code;
        }
    }

    [Fact]
    public async Task WriterHandleDeniesWritesFromForeignThreadAndRemainsUsable()
    {
        using var accounts = Store();
        await accounts.LoginAsync("survivor", "pw");
        Assert.Equal(23, await accounts.WriteAsync<int>(new WrongThreadPlan()));
        Assert.True(await accounts.VerifyAsync("survivor", "pw"));
        Assert.Equal(1, Counter(accounts, "nonWriterWriteAttempts"));
        var snapshot = Metrics(accounts);
        Assert.NotEqual(snapshot.GetProperty("writerThreadId").GetInt64(),
            snapshot.GetProperty("writeDenials")[0].GetProperty("thread").GetInt64());
        Assert.Equal(0, Counter(accounts, "writeGuardErrors"));
    }

#if DEBUG
    [Fact]
    public void ManagedDebugCheckRejectsReadHandleOutsideNativeCallback()
    {
        using var accounts = Store();
        using var read = accounts.Open();
        Assert.Throws<InvalidOperationException>(() => DbWriteGuard.AssertWriter(read));
        Assert.Equal(0, Counter(accounts, "writeGuardErrors"));
    }
#endif

    [Fact]
    public async Task HttpMicroGameLoopTimerAndBackupHaveZeroNonWriterWritesAfterStartup()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        await using var app = ServerHost.Create([], o =>
        {
            o.DataDir = _dir; o.GameDataDir = SyntheticData.Dir; o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        await app.StartAsync();
        try
        {
            var accounts = app.Services.GetRequiredService<AccountStore>();
            var roles = app.Services.GetRequiredService<RoleStore>();
            var loop = app.Services.GetRequiredService<GameLoop>();
            long bootstrap = Counter(accounts, "bootstrapWriteAuthorizations");
            long before = Counter(accounts, "writerWriteAuthorizations");
            string key = File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem"));
            await using var alice = new BotClient($"http://127.0.0.1:{http}", key);
            await using var bob = new BotClient($"http://127.0.0.1:{http}", key);
            await alice.LoginAsync("guardalice", "pw");
            await bob.LoginAsync("guardbob", "pw");
            Assert.True(Counter(accounts, "writerWriteAuthorizations") > before); // HTTP registration/tokens.
            before = Counter(accounts, "writerWriteAuthorizations");
            await alice.EnterGateAsync(); await bob.EnterGateAsync();
            long a = (await alice.CreateRoleAsync("GuardAlice")).RoleBaseInfo.Id;
            long b = (await bob.CreateRoleAsync("GuardBob")).RoleBaseInfo.Id;
            await alice.EnterWorldAsync(a); await bob.EnterWorldAsync(b);
            Assert.True(Counter(accounts, "writerWriteAuthorizations") > before); // Game gate create/login.
            before = Counter(accounts, "writerWriteAuthorizations");
            var applied = await alice.MicroAsync("FriendService", "apply", new() { ["Uid"] = a.ToString(), ["Target"] = b.ToString() });
            Assert.True(((object?[])applied["Ret"]!)[0] is true);
            var accepted = await bob.MicroAsync("FriendService", "accept", new() { ["Uid"] = b.ToString(), ["Applicant"] = a.ToString() });
            Assert.Equal(a.ToString(), accepted["Applicant"]);
            Assert.True(Counter(accounts, "writerWriteAuthorizations") > before);

            var timer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await loop.InvokeAsync(() =>
            {
                roles.SaveSettings(a, [4, 5, 6]);
                loop.Schedule(TimeSpan.FromMilliseconds(10), async () =>
                {
                    try
                    {
                        await app.Services.GetRequiredService<MailService>().SendRawAsync(b, "D1 timer", "durable");
                        timer.TrySetResult();
                    }
                    catch (Exception error) { timer.TrySetException(error); }
                });
                return Task.CompletedTask;
            });
            await timer.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new byte[] { 4, 5, 6 }, roles.LoadSettings(a));
            Assert.NotEmpty(roles.LoadMailbox(b)!);
            var backup = app.Services.GetServices<IHostedService>().OfType<DatabaseBackup>().Single();
            string copy = backup.Backup();
            using (var db = new SqliteConnection($"Data Source={copy};Mode=ReadOnly;Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM role";
                Assert.Equal(2L, (long)command.ExecuteScalar()!);
            }
            Assert.Equal(bootstrap, Counter(accounts, "bootstrapWriteAuthorizations"));
            Assert.Equal(0, Counter(accounts, "nonWriterWriteAttempts"));
            Assert.Equal(0, Counter(accounts, "writeGuardErrors"));
            Assert.True(Counter(accounts, "legacyWriterLoopSync") > 0);
            Assert.True(Counter(accounts, "writerThreadId") > 0);
            var snapshot = Metrics(accounts);
            Assert.Equal(0, snapshot.GetProperty("unlistedWriteOrigins").GetInt64());
            foreach (var origin in snapshot.GetProperty("writeOrigins").EnumerateArray().Where(o => o.GetProperty("access").GetString() == "writer"))
                Assert.Equal(snapshot.GetProperty("writerThreadId").GetInt64(), origin.GetProperty("thread").GetInt64());
            Assert.Equal(0, loop.OrphanContinuations);
        }
        finally { await app.StopAsync(); }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
