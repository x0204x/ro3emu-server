using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>
/// The shared durable payout obligations (RoleObligations, docs/systems/durable-settlement.md) on a real database and game
/// loop, without the client's game data: a refused save or payment is retried, a restart is paid at login, nothing
/// is paid twice, and rows written by the per-activity plans before the shared component are paid exactly once.
/// </summary>
public sealed class RoleObligationsTests : IAsyncLifetime
{
    private const string PayLabel = "test/pay", FreezeLabel = "test/freeze";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-obligations-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
    private readonly GameLoop _loop = new(NullLogger<GameLoop>.Instance);
    private readonly Dictionary<long, GameSession> _online = new();
    /// <summary>Committed payments per source: the "goods" of a payment, counted in its commit's publish.</summary>
    private readonly ConcurrentDictionary<string, int> _paid = new(StringComparer.Ordinal);
    private AccountStore _accounts = null!;

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    public async Task InitializeAsync()
    {
        _accounts = Open();
        await _loop.StartAsync(default);
    }

    public async Task DisposeAsync()
    {
        await _loop.StopAsync(default);
        _loop.Dispose();
        _accounts.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private AccountStore Open() => new(Microsoft.Extensions.Options.Options.Create(new ServerOptions { DataDir = _dir }));

    /// <summary>A process restart: the database is reopened (its schema plans run again) and nothing in memory stays.</summary>
    private void Restart()
    {
        _accounts.Dispose();
        SqliteConnection.ClearAllPools();
        _accounts = Open();
    }

    private Task<T> OnLoop<T>(Func<Task<T>> work) => _loop.InvokeAsync(work);
    private Task OnLoop(Func<Task> work) => _loop.InvokeAsync(work);

    private GameSession Online(long role)
    {
        var session = new GameSession(null!, null!);
        _online[role] = session;
        return session;
    }

    /// <summary>The activity's payment: its goods (counted on commit) and the delivered mark in one transaction.</summary>
    private async Task<bool> Pay<TRow>(GameSession _, TRow row) where TRow : class, IRoleObligationRow<TRow>
    {
        var tx = _accounts.NewTransaction();
        tx.Add(RoleObligations<TRow>.DeliverPlan(row));
        await tx.CommitAsync(_accounts, PayLabel, _ => _paid.AddOrUpdate(row.SourceKey, 1, (_, n) => n + 1));
        return true;
    }

    private RoleObligations<TRow> Obligations<TRow>(ObligationRetry retry = ObligationRetry.TickWithBackoff,
        Func<GameSession, TRow, Task<bool>>? pay = null) where TRow : class, IRoleObligationRow<TRow> =>
        new("Test payout", _accounts, _loop, _clock, role => _online.GetValueOrDefault(role), _ => true, pay ?? Pay,
            retry, NullLogger.Instance);

    private static CasualBattleObligationRow Casual(long match, long role, int score = 40) =>
        new(CasualBattleService.SourceKey(match, role), match, role, JsonSerializer.SerializeToUtf8Bytes(
            new CasualBattleService.Payout
            {
                MatchId = match, RoleId = role, Outcome = 1, Won = true, MmrDelta = 5,
                Stats = new TimedBattleService.CasualStats { PersonalScore = score }
            }));

    private long Rows(string table, int delivered)
    {
        using var db = _accounts.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {table} WHERE delivered=$delivered";
        cmd.Parameters.AddWithValue("$delivered", delivered);
        return (long)cmd.ExecuteScalar()!;
    }

    private async Task Ticks(RoleObligations<CasualBattleObligationRow> obligations, int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            await OnLoop(() => { obligations.Tick(); return Task.CompletedTask; });
            await Task.Delay(100); // a posted retry reads and commits off the loop, then resumes on it
            await OnLoop(() => Task.CompletedTask);
        }
    }

    [Fact]
    public async Task ARefusedPaymentStaysPendingAndTheTickPaysItExactlyOnce()
    {
        var obligations = Obligations<CasualBattleObligationRow>();
        var alice = Online(1); var bob = Online(2);
        CasualBattleObligationRow[] rows = [Casual(7, 1), Casual(7, 2, score: 0)];
        Assert.True(await OnLoop(() => obligations.FreezeAsync(rows, FreezeLabel, 7)));
        int refuse = 1, refused = 0;
        _accounts.BeforeCommitForTest = label =>
        {
            if (label != PayLabel || Interlocked.Exchange(ref refuse, 0) != 1) return;
            Interlocked.Increment(ref refused);
            throw new IOException("Test-only refused payout.");
        };
        await OnLoop(async () =>
        {
            foreach (var row in rows) obligations.Track(row);
            Assert.False(await obligations.TryPayAsync(alice, rows[0]));
            Assert.True(await obligations.TryPayAsync(bob, rows[1]));
        });
        Assert.Equal(1, refused);
        Assert.False(_paid.ContainsKey(rows[0].SourceKey));
        Assert.Equal(1, _paid[rows[1].SourceKey]);
        Assert.Equal(1, Rows("casual_battle_obligation", delivered: 0));
        Assert.Equal(1, obligations.TrackedCount);

        // Not due before its 5-second backoff.
        await Ticks(obligations);
        Assert.False(_paid.ContainsKey(rows[0].SourceKey));
        _clock.Advance(TimeSpan.FromSeconds(5));
        await Ticks(obligations);
        for (int i = 0; i < 100 && !_paid.ContainsKey(rows[0].SourceKey); i++) await Task.Delay(10);
        Assert.Equal(1, _paid[rows[0].SourceKey]);
        for (int i = 0; i < 100 && await OnLoop(() => Task.FromResult(obligations.TrackedCount)) > 0; i++) await Task.Delay(10);
        Assert.Equal(0, await OnLoop(() => Task.FromResult(obligations.TrackedCount)));

        // Later ticks, a second login pass and a direct second payment change nothing.
        _clock.Advance(TimeSpan.FromMinutes(10));
        await Ticks(obligations, 3);
        await OnLoop(() => obligations.DeliverPendingAsync(alice, 1, () => true));
        await Assert.ThrowsAnyAsync<Exception>(() => OnLoop(() => Pay<CasualBattleObligationRow>(alice, rows[0])));
        Assert.Equal(1, _paid[rows[0].SourceKey]);
        Assert.Equal(1, _paid[rows[1].SourceKey]);
        Assert.Equal(2, Rows("casual_battle_obligation", delivered: 1));
        Assert.Equal(1, refused);
    }

    [Fact]
    public async Task ARefusedSaveLeavesNothingAndItsRetrySavesOnce()
    {
        var obligations = Obligations<CasualBattleObligationRow>();
        CasualBattleObligationRow[] rows = [Casual(8, 1), Casual(8, 2)];
        int refused = 0;
        _accounts.BeforeCommitForTest = label =>
        {
            if (label == FreezeLabel && Interlocked.Increment(ref refused) == 1) throw new IOException("Test-only refused save.");
        };
        Assert.False(await OnLoop(() => obligations.FreezeAsync(rows, FreezeLabel, 8)));
        Assert.Equal(0, Rows("casual_battle_obligation", 0) + Rows("casual_battle_obligation", 1));
        Assert.True(await OnLoop(() => obligations.FreezeAsync(rows, FreezeLabel, 8)));
        Assert.Equal(2, Rows("casual_battle_obligation", 0));
        Assert.Equal(2, (await OnLoop(() => obligations.LoginKeysAsync(1))).Length + (await OnLoop(() => obligations.LoginKeysAsync(2))).Length);
    }

    [Fact]
    public async Task ASaveWhoseAcknowledgementWasLostIsAdoptedOnlyWhenItIsTheSameResult()
    {
        var obligations = Obligations<CasualBattleObligationRow>();
        CasualBattleObligationRow[] rows = [Casual(9, 1), Casual(9, 2)];
        _accounts.BeforeOwnedApplyForTest = _ => throw new IOException("Test-only lost acknowledgement.");
        Assert.True(await OnLoop(() => obligations.FreezeAsync(rows, FreezeLabel, 9)));
        _accounts.BeforeOwnedApplyForTest = null;
        Assert.Equal(2, Rows("casual_battle_obligation", 0));
        // A different result for the same match is never adopted (and cannot overwrite the stored one).
        CasualBattleObligationRow[] other = [Casual(9, 1, score: 1), Casual(9, 2)];
        Assert.False(await OnLoop(() => obligations.FreezeAsync(other, FreezeLabel, 9)));
        var stored = await OnLoop(() => obligations.PendingAsync(1));
        Assert.True(Assert.Single(stored).Payout.AsSpan().SequenceEqual(rows[0].Payout));
    }

    [Fact]
    public async Task APayoutRefusedUntilARestartIsPaidOnceAtLoginAndNeverAgain()
    {
        var obligations = Obligations<CasualBattleObligationRow>();
        var alice = Online(1);
        var row = Casual(10, 1);
        Assert.True(await OnLoop(() => obligations.FreezeAsync([row], FreezeLabel, 10)));
        _accounts.BeforeCommitForTest = label => { if (label == PayLabel) throw new IOException("Test-only refused payout."); };
        await OnLoop(async () => { obligations.Track(row); Assert.False(await obligations.TryPayAsync(alice, row)); });
        _clock.Advance(TimeSpan.FromSeconds(5));
        await Ticks(obligations); // the retry is refused too
        _accounts.BeforeCommitForTest = null;
        Assert.Empty(_paid);

        for (int restart = 0; restart < 2; restart++)
        {
            Restart();
            _online.Clear();
            var fresh = Obligations<CasualBattleObligationRow>();
            var keys = await OnLoop(() => fresh.LoginKeysAsync(1));
            Assert.Equal(restart == 0 ? new[] { RoleObligations<CasualBattleObligationRow>.Key(row.SourceKey) }
                : Array.Empty<ReservationKey>(), keys);
            var session = Online(1);
            await OnLoop(() => fresh.DeliverPendingAsync(session, 1, () => true));
            Assert.Equal(1, _paid[row.SourceKey]);
            Assert.Equal(1, Rows("casual_battle_obligation", delivered: 1));
            Assert.Equal(0, fresh.TrackedCount);
        }
    }

    [Fact]
    public async Task NextRecoveryStopsAtARefusalAndLeavesTheRestPending()
    {
        var calls = new List<string>();
        var obligations = Obligations<CasualBattleObligationRow>(ObligationRetry.NextRecovery, async (s, row) =>
        {
            calls.Add(row.SourceKey);
            if (row.MatchId == 11) return false; // refused: the pass stops here
            return await Pay(s, row);
        });
        CasualBattleObligationRow[] first = [Casual(11, 1)], second = [Casual(12, 1)];
        Assert.True(await OnLoop(() => obligations.FreezeAsync(first, FreezeLabel, 11)));
        Assert.True(await OnLoop(() => obligations.FreezeAsync(second, FreezeLabel, 12)));
        var alice = Online(1);
        await OnLoop(() => obligations.DeliverPendingAsync(alice, 1, () => true));
        Assert.Equal(new[] { first[0].SourceKey }, calls); // in payment order (match_id), stopped at the refusal
        Assert.Empty(_paid);
        Assert.Equal(0, obligations.TrackedCount); // no tick retry in this mode
        var failing = Obligations<CasualBattleObligationRow>(ObligationRetry.NextRecovery,
            (_, _) => throw new InvalidOperationException("Test-only failed payout."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => OnLoop(() => failing.DeliverPendingAsync(alice, 1, () => true)));
        Assert.Equal(2, Rows("casual_battle_obligation", delivered: 0));
    }

    /// <summary>
    /// Rows in the shape the per-activity plans wrote before the shared component (same tables, same columns; the MVP
    /// and timed-battle rows with the owner_revision row their create plans bumped, the Mock Battle one without, as a
    /// row from before revision rows): after a restart each is read as pending, reserved and paid exactly once.
    /// </summary>
    [Fact]
    public async Task PendingRowsWrittenBeforeTheSharedComponentArePaidExactlyOnce()
    {
        Restart(); // the schema exists
        using (var db = new SqliteConnection($"Data Source={_accounts.DatabasePath};Pooling=False"))
        {
            db.Open();
            void Exec(string sql, params (string, object)[] args)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = sql;
                foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
                Assert.Equal(1, cmd.ExecuteNonQuery());
            }
            Exec("INSERT INTO casual_battle_obligation(source_key,match_id,role_id,payout) VALUES($s,$m,$r,$p)",
                ("$s", "casual-battle/20/1"), ("$m", 20L), ("$r", 1L), ("$p", Casual(20, 1).Payout));
            Exec("""
                INSERT INTO mvp_award_obligation(source_key,cfg_id,death_ms,player_id,awards,result,delivered)
                VALUES($s,1,1000,1,$a,$r,0)
                """, ("$s", "mvp/kill/1/1000/1"), ("$a", "[]"u8.ToArray()), ("$r", Array.Empty<byte>()));
            Exec("INSERT INTO owner_revision(kind,id,part,revision) VALUES('mvp_obligation','mvp/kill/1/1000/1','state',1)");
            Exec("""
                INSERT INTO timed_battle_obligation(source_key,match_id,entry,role_id,grants,costs,next_state,result)
                VALUES($s,30,2,1,$g,$c,$n,$r)
                """, ("$s", "timed_battle/30/1"), ("$g", "[]"u8.ToArray()), ("$c", "[]"u8.ToArray()),
                ("$n", "{}"u8.ToArray()), ("$r", Array.Empty<byte>()));
            Exec("INSERT INTO owner_revision(kind,id,part,revision) VALUES('timed_battle_obligation','timed_battle/30/1','state',1)");
        }
        for (int restart = 0; restart < 2; restart++)
        {
            Restart();
            await Migrated<CasualBattleObligationRow>("casual-battle/20/1", restart == 0);
            await Migrated<MvpObligationRow>("mvp/kill/1/1000/1", restart == 0);
            await Migrated<TimedBattleObligationRow>("timed_battle/30/1", restart == 0);
        }
        using var check = _accounts.Open();
        using var revisions = check.CreateCommand();
        revisions.CommandText = """
            SELECT kind || '=' || revision FROM owner_revision
            WHERE kind IN ('casual_battle_obligation','mvp_obligation','timed_battle_obligation') ORDER BY kind
            """;
        using var reader = revisions.ExecuteReader();
        var seen = new List<string>();
        while (reader.Read()) seen.Add(reader.GetString(0));
        // The startup seed gave the old Mock Battle row its revision row; each delivery bumped its row once.
        Assert.Equal(new[] { "casual_battle_obligation=1", "mvp_obligation=2", "timed_battle_obligation=2" }, seen);
    }

    private async Task Migrated<TRow>(string source, bool pending) where TRow : class, IRoleObligationRow<TRow>
    {
        _online.Clear();
        var obligations = Obligations<TRow>(typeof(TRow) == typeof(MvpObligationRow)
            ? ObligationRetry.NextRecovery : ObligationRetry.TickWithBackoff);
        var keys = await OnLoop(() => obligations.LoginKeysAsync(1));
        Assert.Equal(pending ? new[] { RoleObligations<TRow>.Key(source) } : Array.Empty<ReservationKey>(), keys);
        var row = (await OnLoop(() => obligations.PendingAsync(1))).SingleOrDefault();
        Assert.Equal(pending, row != null);
        if (row != null) Assert.Equal(source, row.SourceKey);
        await OnLoop(() => obligations.DeliverPendingAsync(Online(1), 1, () => true));
        Assert.Equal(1, _paid[source]);
        Assert.Empty(await OnLoop(() => obligations.PendingAsync(1)));
        Assert.Equal(1, Rows(TRow.Table.Table, delivered: 1));
    }

    /// <summary>
    /// Every obligation table on the shared plans matches its schema plan (its columns are the descriptor's plus
    /// delivered) and is classified by the economy snapshot reader, so a new activity cannot skip either.
    /// </summary>
    [Fact]
    public void EveryObligationTableMatchesItsSchemaAndIsKnownToTheEconomyReader()
    {
        var kinds = typeof(AccountStore).Assembly.GetTypes()
            .Where(type => type.GetInterfaces().Any(i => i.IsGenericType
                && i.GetGenericTypeDefinition() == typeof(IRoleObligationRow<>)))
            .ToArray();
        Assert.Equal(new[] { "CasualBattleObligationRow", "MvpObligationRow", "TimedBattleObligationRow" },
            kinds.Select(type => type.Name).Order(StringComparer.Ordinal).ToArray());
        var known = (Dictionary<string, string>)typeof(EconomySnapshotReader)
            .GetField("KnownColumns", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        using var db = _accounts.Open();
        foreach (var type in kinds)
        {
            var table = (RoleObligationTable)type.GetProperty("Table", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            var expected = table.Columns.Append("delivered").ToArray();
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"SELECT name FROM pragma_table_info('{table.Table}') ORDER BY cid";
            using var reader = cmd.ExecuteReader();
            var actual = new List<string>();
            while (reader.Read()) actual.Add(reader.GetString(0));
            Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
            Assert.Equal("source_key", table.Columns[0]);
            Assert.Contains(table.RoleColumn, table.Columns);
            Assert.True(known.TryGetValue(table.Table, out var columns), table.Table + " is unknown to the economy reader");
            Assert.Equal(expected.Order(StringComparer.Ordinal), columns.Split(' ').Order(StringComparer.Ordinal));
        }
    }
}
