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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(FirstRun, clock, stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CompactAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is SqliteException or IOException or InvalidOperationException) { LogFailed(log, e); }
            var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.ReceiptRetention.IntervalMinutes));
            try { await Task.Delay(interval, clock, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One run: batches until every due receipt is compacted. Returns how many were.</summary>
    internal async Task<long> CompactAsync(CancellationToken ct = default)
    {
        var settings = options.Value.ReceiptRetention;
        if (settings.Days <= 0) return 0;
        long cutoff = clock.GetUtcNow().AddDays(-settings.Days).ToUnixTimeMilliseconds();
        int batch = Math.Clamp(settings.BatchSize, 1, 10_000);
        long total = 0, after = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (compacted, last) = await accounts.WriteAsync<(int, long)>(new ReceiptCompactionPlan(cutoff, after, batch));
            total += compacted;
            if (last <= after) break;
            after = last;
            // Game writes share the single writer: leave it room between batches.
            await Task.Delay(BatchPause, clock, ct);
        }
        if (total > 0) LogCompacted(log, total, settings.Days);
        return total;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Compacted {Count} economic receipts older than {Days} days")]
    private static partial void LogCompacted(ILogger logger, long count, int days);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Receipt compaction failed; it runs again at the next interval")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
