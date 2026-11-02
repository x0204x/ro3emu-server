using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Accounts;

/// <summary>
/// Keeps <c>operation_receipt</c> from growing with every kill and activity. A receipt's payload and outcome bytes are
/// needed while its operation can still be replayed after a lost acknowledgement or resolved after a crash, which
/// happens within seconds of the commit; after <c>ReceiptRetention:Days</c> (2) they are dropped by
/// <see cref="ReceiptCompactionPlan"/>, in small writer transactions off the game loop. Hashes, child receipts, legs
/// and source claims stay, so replay and idempotency keep working (<see cref="ReceiptBlobs"/>) and the offline ledger
/// audit still checks the sequence, legs and claims; it skips the intent checks of a compacted receipt.
/// SQLite reuses the freed pages for new rows, so the file stops growing; it shrinks only with an offline VACUUM.
/// </summary>
public sealed partial class ReceiptRetention(IOptions<ServerOptions> options, AccountStore accounts, TimeProvider clock,
    ILogger<ReceiptRetention> log) : BackgroundService
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(3), BatchPause = TimeSpan.FromMilliseconds(50);

    /// <summary>A date this far ahead of the clock is a wrong clock, not a young receipt.</summary>
    private static readonly TimeSpan FutureMargin = TimeSpan.FromDays(1);

    /// <summary>Most receipts in one writer transaction: about 20 ms on a desktop disk (500, the default, about 10 ms; 10000
    /// measured 240 ms). That stays near group commit's 25 ms, the time other game writes are ready to wait.</summary>
    internal const int MaxBatch = 1000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(FirstRun, clock, stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CompactAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is SqliteException or IOException or PersistenceException or InvalidOperationException)
            { LogFailed(log, e); }
            var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.ReceiptRetention.IntervalMinutes));
            try { await Task.Delay(interval, clock, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Receipts the last run walked past (compacted or not due for compaction); 0 when it found nothing new.</summary>
    internal long LastPassed { get; private set; }

    /// <summary>
    /// One run: batches from the stored watermark until the first receipt that is not due yet. Returns how many receipts
    /// were compacted. A run right after another walks nothing, so its cost no longer grows with the realm's age.
    /// </summary>
    internal async Task<long> CompactAsync(CancellationToken ct = default)
    {
        LastPassed = 0;
        var settings = options.Value.ReceiptRetention;
        if (settings.Days <= 0) return 0;
        var now = clock.GetUtcNow();
        long cutoff = now.AddDays(-settings.Days).ToUnixTimeMilliseconds();
        long future = now.Add(FutureMargin).ToUnixTimeMilliseconds();
        int batch = Math.Clamp(settings.BatchSize, 1, MaxBatch);
        long total = 0, passed = 0, misdated = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var result = await accounts.WriteAsync<ReceiptCompactionPlan.Batch>(new ReceiptCompactionPlan(cutoff, future, batch));
            total += result.Compacted;
            passed += result.Passed;
            misdated += result.Future;
            LastPassed = passed;
            if (!result.More) break;
            // Game writes share the single writer: leave it room between batches.
            await Task.Delay(BatchPause, clock, ct);
        }
        if (total > 0) LogCompacted(log, total, settings.Days);
        if (misdated > 0) LogMisdated(log, misdated);
        return total;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Compacted {Count} economic receipts older than {Days} days")]
    private static partial void LogCompacted(ILogger logger, long count, int days);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} economic receipts are dated more than a day ahead of the clock, so the clock was wrong when they were written or is wrong now; compaction passed them and kept their data")]
    private static partial void LogMisdated(ILogger logger, long count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Receipt compaction failed; it runs again at the next interval")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
