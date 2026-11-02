using System.Diagnostics;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// How long work waits for room when a database queue refuses it. A refusal (<see cref="DbQueueFullException"/>) means
/// nothing ran, so trying again is safe, and a queue is full only while the disk stalls. The wait is an await, so the
/// game loop keeps ticking. Real time on purpose: a disk stall does not follow a test's or the game's clock.
/// </summary>
internal sealed class QueueFullPatience(TimeSpan total)
{
    private static readonly TimeSpan First = TimeSpan.FromMilliseconds(50), Longest = TimeSpan.FromSeconds(2);
    private readonly long _started = Stopwatch.GetTimestamp();
    private TimeSpan _next = First;

    /// <summary>The refusal in <paramref name="error"/> or its inner exceptions, when there is still time to wait for room.</summary>
    internal bool MayRetry(Exception error) => Refused(error) && Stopwatch.GetElapsedTime(_started) < total;

    internal Task WaitAsync()
    {
        var delay = _next;
        _next = TimeSpan.FromTicks(Math.Min(_next.Ticks * 2, Longest.Ticks));
        return Task.Delay(delay);
    }

    internal static bool Refused(Exception? error)
    {
        for (; error != null; error = error.InnerException)
            if (error is DbQueueFullException) return true;
        return false;
    }

    /// <summary>Runs <paramref name="work"/> until it is admitted or the patience ends; then its refusal is thrown.</summary>
    internal static async Task<T> RunAsync<T>(Func<Task<T>> work, TimeSpan total)
    {
        var patience = new QueueFullPatience(total);
        while (true)
        {
            try { return await work(); }
            catch (Exception e) when (patience.MayRetry(e)) { }
            await patience.WaitAsync();
        }
    }

    /// <summary>The same for work that nothing retries later, such as a settlement for the others when a member leaves.</summary>
    internal static Task RunAsync(Func<Task> work) => RunAsync(async () => { await work(); return true; }, Departure);

    private static readonly TimeSpan Departure = TimeSpan.FromSeconds(60);
}
