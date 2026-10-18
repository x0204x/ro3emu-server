using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>How a payout that could not be made is tried again.</summary>
internal enum ObligationRetry
{
    /// <summary>The tick retries it with backoff (5 s doubling to 5 min) while the role is online; a payout that
    /// fails inside a login stays pending instead of failing the login.</summary>
    TickWithBackoff,
    /// <summary>Only the activity's next recovery point (its next settlement, the next login) pays it; a failure
    /// propagates to the caller and a refused payout (<c>false</c>) stops the pass.</summary>
    NextRecovery,
}

/// <summary>
/// The durable payout obligations of one activity (docs/systems/durable-settlement.md): one row per (settlement, role) in
/// the activity's own table, frozen before anyone is told the result, then paid on its own in one commit that also
/// marks it delivered (and writes the activity's receipt). This class owns the rules every activity used to repeat:
/// <list type="bullet">
/// <item>the save of a settlement's rows (<see cref="FreezeAsync"/>, or <see cref="CreatePlan"/> inside a larger
/// settlement commit), adopting a save whose acknowledgement was lost;</item>
/// <item>one payment per row (<see cref="TryPayAsync"/>): the activity's pay callback must commit
/// <see cref="DeliverPlan"/> with its goods and receipt, so a second payment can never commit;</item>
/// <item>the retry (<see cref="Tick"/>) with backoff while the role is online;</item>
/// <item>the login: the role select reserves <see cref="LoginKeysAsync"/> and <see cref="DeliverPendingAsync"/>
/// pays what is left after a failure, a disconnect or a restart.</item>
/// </list>
/// Loop-only, like the services that own it.
/// </summary>
internal sealed class RoleObligations<TRow> where TRow : class, IRoleObligationRow<TRow>
{
    private sealed class Entry
    {
        public required TRow Row;
        public long NextAttempt;
        public int Failures;
        public bool Running;
    }

    private readonly string name;
    private readonly AccountStore accounts;
    private readonly GameLoop loop;
    private readonly TimeProvider time;
    private readonly Func<long, GameSession?> online;
    private readonly Func<GameSession, bool> ready;
    private readonly Func<GameSession, TRow, Task<bool>> pay;
    private readonly ObligationRetry retry;
    private readonly ILogger log;
    private readonly Dictionary<string, Entry> pending = new(StringComparer.Ordinal);

    /// <param name="name">What the obligations are, for logs and the retry's loop origin ("Mock Battle payout").</param>
    /// <param name="online">The session of a role that is online, or null.</param>
    /// <param name="ready">Whether a session has loaded what a payment needs (a retry waits until it has).</param>
    /// <param name="pay">Pays one row to its role's session in one commit that includes <see cref="DeliverPlan"/>;
    /// true once paid (or found already paid), false when it is not paid now. A throw is a failed attempt.</param>
    internal RoleObligations(string name, AccountStore accounts, GameLoop loop, TimeProvider time,
        Func<long, GameSession?> online, Func<GameSession, bool> ready, Func<GameSession, TRow, Task<bool>> pay,
        ObligationRetry retry, ILogger log)
    {
        this.name = name; this.accounts = accounts; this.loop = loop; this.time = time; this.online = online;
        this.ready = ready; this.pay = pay; this.retry = retry; this.log = log;
    }

    private long Now => time.GetUtcNow().ToUnixTimeSeconds();

    /// <summary>The reservation of one row: payments and the role select hold it.</summary>
    internal static ReservationKey Key(string source) => ReservationKey.Entity(TRow.Table.RevisionKind, source);
    /// <summary>Seconds before the next attempt after <paramref name="failures"/> failures: 5, 10, 20 ... 300.</summary>
    internal static long Backoff(int failures) => Math.Min(300, 5L << Math.Clamp(failures - 1, 0, 6));
    /// <summary>Inserts a settlement's rows, for an activity whose settlement commit writes more than its rows.</summary>
    internal static DbPlan CreatePlan(IEnumerable<TRow> rows) => new RoleObligationCreatePlan<TRow>(rows);
    /// <summary>The delivered mark; the pay callback commits it with the payment.</summary>
    internal static DbPlan DeliverPlan(TRow row) => new RoleObligationDeliverPlan<TRow>(row.SourceKey, row.RoleId);

    /// <summary>The role's undelivered rows, in payment order.</summary>
    internal static Task<List<TRow>> PendingAsync(AccountStore accounts, long role) =>
        accounts.ReadAsync<List<TRow>>(new RoleObligationPendingReadPlan<TRow>(role));

    /// <summary>C/D 15: the role's pending rows, read before the role select admits so it can reserve them.</summary>
    internal static async Task<ReservationKey[]> LoginKeysAsync(AccountStore accounts, long role) =>
        [.. (await PendingAsync(accounts, role)).Select(row => Key(row.SourceKey)).Distinct()];

    internal Task<List<TRow>> PendingAsync(long role) => PendingAsync(accounts, role);
    internal Task<ReservationKey[]> LoginKeysAsync(long role) => LoginKeysAsync(accounts, role);

    /// <summary>Rows waiting for the tick in this process (tests and diagnostics).</summary>
    internal int TrackedCount => pending.Count;

    /// <summary>
    /// Saves one settlement's rows in one transaction. True once they are durable: committed now, or committed by an
    /// earlier attempt whose acknowledgement was lost (the stored rows of the scope are exactly these). False when
    /// the save was refused: nothing is durable, the caller keeps its settlement and tries again later.
    /// </summary>
    internal async Task<bool> FreezeAsync(IReadOnlyList<TRow> rows, string label, long scope)
    {
        if (rows.Count == 0) return true;
        try
        {
            var tx = accounts.NewTransaction();
            tx.Add(CreatePlan(rows));
            await tx.CommitAsync(accounts, label);
            return true;
        }
        catch (Exception error)
        {
            try
            {
                var stored = await accounts.ReadAsync<List<TRow>>(new RoleObligationScopeReadPlan<TRow>(scope));
                if (stored.Count == rows.Count && rows.All(row => stored.Any(old =>
                        string.Equals(old.SourceKey, row.SourceKey, StringComparison.Ordinal) && row.SameFrozen(old))))
                    return true;
            }
            catch (Exception readError) { log.LogWarning(readError, "{Name} {Scope}: stored rows unreadable", name, scope); }
            log.LogError(error, "{Name} {Scope} not saved; the settlement is retried", name, scope);
            return false;
        }
    }

    /// <summary>A durable row this process will pay: kept pending (not yet due for the tick) until an attempt.</summary>
    internal void Track(TRow row)
    {
        if (retry == ObligationRetry.TickWithBackoff)
            pending[row.SourceKey] = new Entry { Row = row, NextAttempt = long.MaxValue };
    }

    /// <summary>Leaves a row to the login (its role is not here to be paid).</summary>
    internal void Forget(string source) => pending.Remove(source);

    /// <summary>Pays one row now; on failure it stays pending with backoff. True once it is paid.</summary>
    internal async Task<bool> TryPayAsync(GameSession recipient, TRow row)
    {
        if (!pending.TryGetValue(row.SourceKey, out var entry))
            pending[row.SourceKey] = entry = new Entry { Row = row };
        if (entry.Running) return false;
        entry.Running = true;
        try
        {
            if (await pay(recipient, row))
            {
                pending.Remove(row.SourceKey);
                return true;
            }
            Failed(entry, null);
            return false;
        }
        catch (Exception error)
        {
            Failed(entry, error);
            return false;
        }
        finally { entry.Running = false; }
    }

    private void Failed(Entry entry, Exception? error)
    {
        entry.Failures++;
        entry.NextAttempt = Now + Backoff(entry.Failures);
        log.LogError(error, "{Name} {Source} remains pending (attempt {Attempt})", name, entry.Row.SourceKey, entry.Failures);
    }

    /// <summary>Queues the retries that are due (once each; the attempt sets the next one).</summary>
    internal void Tick()
    {
        if (pending.Count == 0) return;
        long now = Now;
        foreach (var entry in pending.Values.Where(e => !e.Running && e.NextAttempt <= now).ToArray())
        {
            entry.NextAttempt = long.MaxValue;
            loop.PostLegacy(() => RetryAsync(entry), name + " retry");
        }
    }

    /// <summary>The tick's retry of a pending row: paid while the role is online, else left to its login.</summary>
    private async Task RetryAsync(Entry entry)
    {
        var row = entry.Row;
        if (entry.Running || !pending.ContainsKey(row.SourceKey)) return;
        if (online(row.RoleId) is not { } s) { pending.Remove(row.SourceKey); return; }
        if (!ready(s)) { entry.NextAttempt = Now + Backoff(1); return; }
        List<TRow> stored;
        try { stored = await PendingAsync(row.RoleId); }
        catch (Exception error)
        {
            entry.Failures++;
            entry.NextAttempt = Now + Backoff(entry.Failures);
            log.LogError(error, "{Name} {Source} could not be read for its retry", name, row.SourceKey);
            return;
        }
        // Delivered meanwhile (a login, or a commit whose acknowledgement was lost): nothing left to pay.
        if (!stored.Any(r => string.Equals(r.SourceKey, row.SourceKey, StringComparison.Ordinal)))
        { pending.Remove(row.SourceKey); return; }
        await TryPayAsync(s, row);
    }

    /// <summary>
    /// Pays the role's pending rows (login, or another recovery point of the activity). <paramref name="current"/> is
    /// checked before each row: once false the pass stops. Inside the role select only the rows it reserved are paid;
    /// one frozen after its admission is left to the tick (or to the next recovery point).
    /// </summary>
    internal async Task DeliverPendingAsync(GameSession recipient, long role, Func<bool> current)
    {
        foreach (var row in await PendingAsync(role))
        {
            if (!current()) return;
            if (loop.Owners is { } owners && !owners.CurrentCovers(Key(row.SourceKey)))
            {
                if (retry == ObligationRetry.TickWithBackoff && !pending.ContainsKey(row.SourceKey))
                    pending[row.SourceKey] = new Entry { Row = row, NextAttempt = Now + Backoff(1) };
                continue;
            }
            if (retry == ObligationRetry.NextRecovery)
            {
                if (!await pay(recipient, row)) return;
                continue;
            }
            if (!await TryPayAsync(recipient, row) && recipient.Owner?.State == RoleOwnerState.RecoveryRequired)
                throw new OperationCanceledException(name + " needs owner recovery.");
        }
    }
}
