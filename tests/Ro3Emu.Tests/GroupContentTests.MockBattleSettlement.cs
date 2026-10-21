using System.Text.Json;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Mock Battle settlement is durable and idempotent: each stayed player's payout obligation is saved before the match
/// counts as settled, a refused payout stays pending and is retried (tick while online, login after a restart), and
/// nobody is paid twice.
/// </summary>
public sealed partial class GroupContentTests
{
    private const string CasualReceiptLabel = "economy/event/casual-battle";

    private async Task<BotClient> LogIn(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        await bot.EnterWorldAsync(bot.Roles.Single().Id);
        return bot;
    }

    /// <summary>Alice (camp 1) kills Bob twice: she wins with 40 points (pools 85201 + 85202), he loses with 0 (no
    /// items). Returns the match (room) id, with the clock just before the deadline.</summary>
    private async Task<long> PlayMockBattle(BotClient alice, BotClient bob)
    {
        foreach (var bot in new[] { alice, bob }) await Gm(bot, "!lvl 99");
        await Gm(alice, "!casual open");
        Assert.Equal(0, (await SignUp(alice)).Result);
        Assert.Equal(0, (await SignUp(bob)).Result);
        await Gm(alice, "!casual start");
        long match = 0;
        foreach (var bot in new[] { alice, bob }) (match, _) = await Load(bot, CasualBattleService.Scene);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await Tick();
        await Next(alice, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, m => m.Stage == 1);
        await OnLoop(async () =>
        {
            await Service<CasualBattleService>().PlayerKilledAsync(Session(alice), Session(bob));
            await Service<CasualBattleService>().PlayerKilledAsync(Session(alice), Session(bob));
        });
        await Next(bob, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser,
            m => m.Vars.Any(v => v.VarId == 304 && v.Vi64[1] == 40));
        return match;
    }

    private Task<TimedBattleService.State> CasualState(BotClient bot) => Service<GameLoop>().InvokeAsync(() =>
        Task.FromResult(JsonSerializer.Deserialize<TimedBattleService.State>(
            JsonSerializer.SerializeToUtf8Bytes(Session(bot).TimedBattle!))!));

    private TimedBattleService.State SavedCasualState(long role) => Service<RoleStore>().LoadTimedBattle(role) is { } bytes
        ? JsonSerializer.Deserialize<TimedBattleService.State>(bytes)! : new TimedBattleService.State();

    /// <summary>Every bag stack and currency the role holds, as one number: a payout always adds to it.</summary>
    private Task<long> Holdings(BotClient bot) => Service<GameLoop>().InvokeAsync(() =>
        Task.FromResult(Session(bot).Items.Sum(i => (long)i.Stack) + Session(bot).Progress!.Currency.Values.Sum()));

    private long CasualSql(string sql, params (string Name, object Value)[] args)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_dataDir, "ro3emu.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>The ledger's receipts for one role's payout of one match (the natural source dedupes them).</summary>
    private long CasualReceipts(long match, long role) => CasualSql(
        "SELECT count(*) FROM operation_receipt WHERE command_kind='event/casual-battle' AND source_key=$source",
        ("$source", CasualBattleService.SourceKey(match, role)));

    private long CasualObligations(long match, int delivered) => CasualSql(
        "SELECT count(*) FROM casual_battle_obligation WHERE match_id=$match AND delivered=$delivered",
        ("$match", match), ("$delivered", delivered));

    private async Task WaitCasualPaid(BotClient bot, long match)
    {
        for (int i = 0; i < 200; i++)
        {
            if ((await CasualState(bot)).CasualSettled.Contains(match)) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("Mock Battle payout was not made.");
    }

    private static void AssertPaidOnce(TimedBattleService.State state, long match, int mmr, int outcome, long score)
    {
        Assert.Contains(match, state.CasualSettled);
        Assert.Equal(mmr, state.CasualMmr);
        var record = Assert.Single(state.CasualRecords);
        Assert.Equal(outcome, record.Outcome);
        Assert.Equal(score, state.CasualTotal.PersonalScore);
    }

    [ClientDataFact]
    public async Task MockBattleRefusedPayoutIsRetriedAndEveryonePaidExactlyOnce()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        long aliceRole = (long)alice.Self!.Uuid, bobRole = (long)bob.Self!.Uuid;
        long match = await PlayMockBattle(alice, bob);
        long before = await Holdings(alice);

        // Alice's payout commit (the only one with goods) is refused once.
        int refused = 0;
        var accounts = Service<AccountStore>();
        accounts.BeforeCommitForTest = label =>
        {
            if (label == CasualReceiptLabel && Interlocked.Increment(ref refused) == 1)
                throw new IOException("Test-only refused Mock Battle payout.");
        };
        try
        {
            _clock.Advance(TimeSpan.FromSeconds(600));
            await Tick();
            // The result notices are best-effort and still reach both players.
            Assert.Equal(1, (await Next(alice, CasualBattleService.ResultNotify, DungeonResultNotify.Parser)).ResultInfo.Result);
            Assert.Equal(2, (await Next(bob, CasualBattleService.ResultNotify, DungeonResultNotify.Parser)).ResultInfo.Result);
        }
        finally { accounts.BeforeCommitForTest = null; }
        Assert.Equal(1, refused);

        // Bob was paid; Alice's obligation is durable and pending, nothing of it applied.
        AssertPaidOnce(await CasualState(bob), match, 997, 2, 0);
        AssertPaidOnce(SavedCasualState(bobRole), match, 997, 2, 0);
        var unpaid = await CasualState(alice);
        Assert.DoesNotContain(match, unpaid.CasualSettled);
        Assert.Empty(unpaid.CasualRecords);
        Assert.DoesNotContain(match, SavedCasualState(aliceRole).CasualSettled);
        Assert.Equal(before, await Holdings(alice));
        Assert.Equal(0, CasualReceipts(match, aliceRole));
        Assert.Equal(1, CasualObligations(match, delivered: 0));
        Assert.Equal(1, CasualObligations(match, delivered: 1));

        // The fault is gone: the tick's retry (after its backoff) pays Alice exactly once.
        _clock.Advance(TimeSpan.FromSeconds(10));
        await Tick();
        await WaitCasualPaid(alice, match);
        AssertPaidOnce(await CasualState(alice), match, 1005, 1, 40);
        AssertPaidOnce(SavedCasualState(aliceRole), match, 1005, 1, 40);
        long paid = await Holdings(alice);
        Assert.True(paid > before, "The retried payout carried no items.");
        Assert.Equal(1, CasualReceipts(match, aliceRole));
        Assert.Equal(2, CasualObligations(match, delivered: 1));

        // Further ticks change nothing for anyone.
        for (int i = 0; i < 3; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(400));
            await Tick();
        }
        await Task.Delay(200);
        AssertPaidOnce(await CasualState(alice), match, 1005, 1, 40);
        AssertPaidOnce(await CasualState(bob), match, 997, 2, 0);
        Assert.Equal(paid, await Holdings(alice));
        Assert.Equal(1, CasualReceipts(match, aliceRole));
        Assert.Equal(0, CasualReceipts(match, bobRole));
        Assert.Equal(1005, ((DBCasualBattleInfo)Session(alice).Synced[CasualBattleService.Sync]).Mmr);
    }

    [ClientDataFact]
    public async Task MockBattleResultSaveRefusedIsRetriedBeforeAnyPayout()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        long match = await PlayMockBattle(alice, bob);
        int refused = 0;
        var accounts = Service<AccountStore>();
        accounts.BeforeCommitForTest = label =>
        {
            if (label == "casual-battle/freeze" && Interlocked.Increment(ref refused) == 1)
                throw new IOException("Test-only refused Mock Battle result save.");
        };
        try
        {
            _clock.Advance(TimeSpan.FromSeconds(600));
            await Tick();
            for (int i = 0; i < 100 && Volatile.Read(ref refused) == 0; i++) await Task.Delay(20);
            await Task.Delay(200);
            Assert.Equal(1, refused);
            // Not settled: no obligation, no payout, no result notice; the battle itself is over.
            Assert.Equal(0, CasualObligations(match, delivered: 0) + CasualObligations(match, delivered: 1));
            foreach (var bot in new[] { alice, bob })
            {
                Assert.DoesNotContain(match, (await CasualState(bot)).CasualSettled);
                Assert.DoesNotContain(bot.Inbox.Snapshot(), packet => packet.IsGame(CasualBattleService.ResultNotify));
            }
            await OnLoop(() =>
            {
                Assert.False(Service<CasualBattleService>().CanFight(Session(alice), Session(bob)));
                return Task.CompletedTask;
            });

            // The timeline retries the save after its backoff; then both are paid once.
            _clock.Advance(TimeSpan.FromSeconds(10));
            await Tick();
            Assert.Equal(1, (await Next(alice, CasualBattleService.ResultNotify, DungeonResultNotify.Parser)).ResultInfo.Result);
            Assert.Equal(2, (await Next(bob, CasualBattleService.ResultNotify, DungeonResultNotify.Parser)).ResultInfo.Result);
        }
        finally { accounts.BeforeCommitForTest = null; }
        Assert.Equal(2, refused);
        AssertPaidOnce(await CasualState(alice), match, 1005, 1, 40);
        AssertPaidOnce(await CasualState(bob), match, 997, 2, 0);
        Assert.Equal(2, CasualObligations(match, delivered: 1));
        Assert.Equal(1, CasualReceipts(match, (long)alice.Self!.Uuid));
    }

    [ClientDataFact]
    public async Task MockBattlePayoutRefusedUntilRestartIsPaidOnceAtLogin()
    {
        long match, aliceRole, bobRole, before;
        await using (var alice = await InWorld("Alice"))
        await using (var bob = await InWorld("Bob"))
        {
            aliceRole = (long)alice.Self!.Uuid;
            bobRole = (long)bob.Self!.Uuid;
            match = await PlayMockBattle(alice, bob);
            before = await Holdings(alice);
            var accounts = Service<AccountStore>();
            accounts.BeforeCommitForTest = label =>
            {
                if (label == CasualReceiptLabel) throw new IOException("Test-only refused Mock Battle payout.");
            };
            try
            {
                _clock.Advance(TimeSpan.FromSeconds(600));
                await Tick();
                await Next(alice, CasualBattleService.ResultNotify, DungeonResultNotify.Parser);
                await Next(bob, CasualBattleService.ResultNotify, DungeonResultNotify.Parser);
                // A retry while the fault holds refuses again and leaves the obligation pending.
                _clock.Advance(TimeSpan.FromSeconds(10));
                await Tick();
                await Task.Delay(200);
            }
            finally { accounts.BeforeCommitForTest = null; }
            Assert.DoesNotContain(match, SavedCasualState(aliceRole).CasualSettled);
            AssertPaidOnce(SavedCasualState(bobRole), match, 997, 2, 0);
            Assert.Equal(1, CasualObligations(match, delivered: 0));
        }

        // Restart between the failure and the retry: the login pays the durable obligation.
        long? paid = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            await InitializeAsync();
            await using (var alice = await LogIn("Alice"))
            {
                await WaitCasualPaid(alice, match);
                AssertPaidOnce(await CasualState(alice), match, 1005, 1, 40);
                AssertPaidOnce(SavedCasualState(aliceRole), match, 1005, 1, 40);
                Assert.Equal(1005, ((DBCasualBattleInfo)Session(alice).Synced[CasualBattleService.Sync]).Mmr);
                long now = await Holdings(alice);
                Assert.True(now > before, "The payout at login carried no items.");
                paid ??= now; // the second restart must not add anything
                Assert.Equal(paid, now);
                _clock.Advance(TimeSpan.FromSeconds(400));
                await Tick();
                await Task.Delay(200);
                Assert.Equal(paid, await Holdings(alice));
            }
            await using (var bob = await LogIn("Bob"))
                AssertPaidOnce(await CasualState(bob), match, 997, 2, 0);
            Assert.Equal(1, CasualReceipts(match, aliceRole));
            Assert.Equal(0, CasualReceipts(match, bobRole));
            Assert.Equal(2, CasualObligations(match, delivered: 1));
        }
    }
}
