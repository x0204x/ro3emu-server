using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Actual process death between the committed 5057 library and its required quest Core.</summary>
public sealed class CardCrashRecoveryTests
{
    private const long Quest = 1700212;
    private const string CrashDirEnv = "RO3_STEP08_CRASH_DIR", BarrierEnv = "RO3_STEP08_CRASH_BARRIER",
        PhaseEnv = "RO3_STEP08_CRASH_PHASE";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task CrashChildAfterLibraryCommit()
    {
        string? dataDir = Environment.GetEnvironmentVariable(CrashDirEnv);
        string? barrier = Environment.GetEnvironmentVariable(BarrierEnv);
        if (dataDir == null || barrier == null) return; // Only the parent test launches the crash child.
        var (http, micro, game) = TestPorts.FreePorts();
        var app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        await app.StartAsync();
        var bot = new BotClient($"http://127.0.0.1:{http}", File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("crashalice", "pw");
        await bot.EnterGateAsync();
        int card = HighQualityCard(app.Services.GetRequiredService<GameData>());
        if (Environment.GetEnvironmentVariable(PhaseEnv) == "task-login")
        {
            long existing = bot.Roles.Single().Id;
            app.Services.GetRequiredService<TaskService>().AfterLoginSettlementForTest = _ =>
            {
                PublishBarrier(barrier, existing, card);
                return Task.Delay(Timeout.InfiniteTimeSpan);
            };
            await bot.EnterWorldAsync(existing);
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return;
        }
        long role = (await bot.CreateRoleAsync("CrashAlice")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(role);
        using (var client = new HttpClient())
        {
            (await client.PostAsync($"http://127.0.0.1:{http}/ro3emu/gm?account=crashalice",
                new StringContent($"!quest accept {Quest}"))).EnsureSuccessStatusCode();
            (await client.PostAsync($"http://127.0.0.1:{http}/ro3emu/gm?account=crashalice",
                new StringContent($"!item {card} 1"))).EnsureSuccessStatusCode();
        }
        app.Services.GetRequiredService<TaskService>().BeforeProgress = session =>
        {
            if (session.Library?.Cards.TryGetValue(card, out var value) != true || !value.TreasureUnlock)
                return Task.CompletedTask;
            PublishBarrier(barrier, role, card);
            return Task.Delay(Timeout.InfiniteTimeSpan);
        };
        await bot.SendGameAsync(CardService.LibraryCardUnlock, new LibraryCardUnlockReq { CardId = card, IsTreasure = true });
        await Task.Delay(Timeout.InfiniteTimeSpan); // The parent kills this process tree; disposal is intentionally bypassed.
    }

    [ClientDataFact]
    public async Task ProcessCrashAfterLibraryCommitRecoversQuestOnceOnReconnect()
    {
        string scratchRoot = Path.GetFullPath(Path.Combine(RepoRoot(), ".loadtest-temp"));
        string dataDir = Path.Combine(scratchRoot, "step08-card-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        string barrier = Path.Combine(dataDir, "committed.barrier");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot(), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in new[] { "vstest", typeof(CardCrashRecoveryTests).Assembly.Location,
            "--TestCaseFilter:FullyQualifiedName~CrashChildAfterLibraryCommit",
            "--ResultsDirectory:" + Path.Combine(RepoRoot(), ".loadtest", "step08-results") })
            start.ArgumentList.Add(argument);
        start.Environment[CrashDirEnv] = dataDir;
        start.Environment[BarrierEnv] = barrier;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start crash child.");
        Task<string> output = child.StandardOutput.ReadToEndAsync(), errors = child.StandardError.ReadToEndAsync();
        WebApplication? app = null;
        try
        {
            var deadline = DateTime.UtcNow + Wait;
            while (!File.Exists(barrier) && DateTime.UtcNow < deadline && !child.HasExited)
                await Task.Delay(50);
            if (!File.Exists(barrier))
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
                throw new Xunit.Sdk.XunitException(
                    $"Crash child did not reach committed library barrier. stdout={await output} stderr={await errors}");
            }
            string[] source = File.ReadAllText(barrier).Split(':');
            long role = long.Parse(source[0]); int card = int.Parse(source[1]);
            await KillAtBarrierAsync(child, source);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(dataDir, "ro3emu.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            {
                db.Open();
                using (var integrity = db.CreateCommand())
                {
                    integrity.CommandText = "PRAGMA integrity_check";
                    Assert.Equal("ok", integrity.ExecuteScalar());
                }
                using var command = db.CreateCommand();
                command.CommandText = "SELECT library,tasks,task_followup FROM role WHERE id=$role";
                command.Parameters.AddWithValue("$role", role);
                using var row = command.ExecuteReader();
                Assert.True(row.Read());
                Assert.True(DBLibraryInfo.Parser.ParseFrom((byte[])row[0]).Cards[card].TreasureUnlock);
                Assert.Equal(0, DBTaskInfo.Parser.ParseFrom((byte[])row[1]).CompleteTaskNum.GetValueOrDefault(Quest));
                Assert.True(((byte[])row[2]).Length > 2);
            }
            var beforeRecovery = Snapshot(dataDir, role);
            string secondBarrier = Path.Combine(dataDir, "task-login.barrier");
            start.Environment[BarrierEnv] = secondBarrier;
            start.Environment[PhaseEnv] = "task-login";
            using (var second = Process.Start(start) ?? throw new InvalidOperationException("Could not start task-login crash child."))
            {
                Task<string> secondOutput = second.StandardOutput.ReadToEndAsync(), secondErrors = second.StandardError.ReadToEndAsync();
                try
                {
                    var secondDeadline = DateTime.UtcNow + Wait;
                    while (!File.Exists(secondBarrier) && DateTime.UtcNow < secondDeadline && !second.HasExited)
                        await Task.Delay(50);
                    if (!File.Exists(secondBarrier))
                    {
                        if (!second.HasExited) { second.Kill(entireProcessTree: true); await second.WaitForExitAsync(); }
                        throw new Xunit.Sdk.XunitException(
                            $"Task-login child did not reach pre-card-load barrier. stdout={await secondOutput} stderr={await secondErrors}");
                    }
                    // Task Login settled before CardService loaded 5057. An interruption here must
                    // leave the natural card source pending and must not deliver a reward.
                    using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = Path.Combine(dataDir, "ro3emu.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
                    {
                        db.Open();
                        using var command = db.CreateCommand();
                        command.CommandText = "SELECT tasks,task_followup FROM role WHERE id=$role";
                        command.Parameters.AddWithValue("$role", role);
                        using var row = command.ExecuteReader();
                        Assert.True(row.Read());
                        Assert.Equal(0, DBTaskInfo.Parser.ParseFrom((byte[])row[0]).CompleteTaskNum.GetValueOrDefault(Quest));
                        Assert.True(((byte[])row[1]).Length > 2);
                    }
                    // Close the parent's WAL reader before process death and wait for the actual
                    // testhost PID, not merely its `dotnet vstest` launcher.
                    await KillAtBarrierAsync(second, File.ReadAllText(secondBarrier).Split(':'));
                }
                finally { if (!second.HasExited) { second.Kill(entireProcessTree: true); await second.WaitForExitAsync(); } }
            }
            Assert.Equal(beforeRecovery, Snapshot(dataDir, role));
            var (http, micro, game) = TestPorts.FreePorts();
            app = ServerHost.Create([], o =>
            {
                o.DataDir = dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
                o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game;
            });
            await app.StartAsync();
            for (int login = 0; login < 2; login++)
            {
                await using var bot = new BotClient($"http://127.0.0.1:{http}", File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
                await bot.LoginAsync("crashalice", "pw");
                await bot.EnterGateAsync();
                await bot.EnterWorldAsync(bot.Roles.Single().Id);
                var roles = app.Services.GetRequiredService<RoleStore>();
                Assert.Equal(1, DBTaskInfo.Parser.ParseFrom(roles.LoadTasks(role)).CompleteTaskNum.GetValueOrDefault(Quest));
                Assert.False(roles.HasTaskFollowup(role));
                Assert.Equal(beforeRecovery, Snapshot(dataDir, role));
            }
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            if (app != null) { await app.StopAsync(); await app.DisposeAsync(); }
            if (Path.GetFullPath(dataDir).StartsWith(scratchRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    private static (long Exp, long JobExp, string Currency, string Items) Snapshot(string dataDir, long role)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(dataDir, "ro3emu.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        db.Open();
        long exp, jobExp; string currency;
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT exp,job_exp,currency FROM role WHERE id=$role";
            command.Parameters.AddWithValue("$role", role);
            using var row = command.ExecuteReader();
            Assert.True(row.Read());
            exp = row.GetInt64(0); jobExp = row.GetInt64(1);
            currency = string.Join(',', row.GetString(2).Split(',', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        }
        var items = new List<string>();
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT guid,entry,stack,pack,grid,bind_type,bind_expire FROM item WHERE role_id=$role ORDER BY guid";
            command.Parameters.AddWithValue("$role", role);
            using var row = command.ExecuteReader();
            while (row.Read()) items.Add(string.Join(':', Enumerable.Range(0, 7).Select(row.GetValue)));
        }
        return (exp, jobExp, currency, string.Join('|', items));
    }

    private static void PublishBarrier(string path, long role, int card)
    {
        string temporary = path + ".writing";
        File.WriteAllText(temporary, $"{role}:{card}:{Environment.ProcessId}");
        File.Move(temporary, path); // Parent can only observe a complete post-commit barrier.
    }

    private static async Task KillAtBarrierAsync(Process launcher, string[] barrier)
    {
        int testhostPid = int.Parse(barrier[2]);
        if (testhostPid == launcher.Id) throw new Xunit.Sdk.XunitException("Crash barrier was published by the launcher, not testhost.");
        using (var testhost = Process.GetProcessById(testhostPid))
        {
            testhost.Kill(entireProcessTree: true);
            await testhost.WaitForExitAsync();
        }
        if (!launcher.HasExited) launcher.Kill(entireProcessTree: true);
        await launcher.WaitForExitAsync();
    }

    private static int HighQualityCard(GameData data) => (int)GameData.Int(data.Table("ItemConfig").Values.First(row =>
        GameData.Int(row, "_iType") == 4 && GameData.Int(row, "_iSubType") == 400 &&
        GameData.Int(row, "_iQuality") >= 4), "_iID");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "data"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo data");
    }
}
