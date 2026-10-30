using System.Globalization;
using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Http;

/// <summary>
/// Broadcasts: the GM marquee (BROADCAST_NOTIFY 83270, a raw string the client shows on screen, GmCommands.ReplyAsync)
/// to every session with a character. A repeating one is a global deadline of the loop (Deadlines.SetGlobal); repeats
/// live in memory, so a restart ends them (inferred: the client has no scheduled-notice message we know).
/// </summary>
public sealed partial class PanelAdminTools
{
    private const string BroadcastKey = "panel-broadcast:";
    private readonly object _broadcastGate = new();
    private readonly Dictionary<long, PanelBroadcast> _broadcasts = [];
    private long _nextBroadcast;

    public IReadOnlyList<PanelBroadcast> Broadcasts()
    {
        lock (_broadcastGate) return [.. _broadcasts.Values.OrderBy(b => b.Next)];
    }

    public async Task<int> BroadcastAsync(string admin, string text, int everyMinutes, int repeats)
    {
        int reached;
        try
        {
            reached = await loop.InvokeAsync(async () =>
            {
                int count = await SendBroadcastAsync(text);
                if (repeats > 0)
                {
                    PanelBroadcast next;
                    lock (_broadcastGate)
                    {
                        next = new PanelBroadcast(++_nextBroadcast, text, admin, everyMinutes, repeats, clock.GetUtcNow().AddMinutes(everyMinutes));
                        _broadcasts[next.Id] = next;
                    }
                    Schedule(next);
                }
                return count;
            });
        }
        catch (OperationCanceledException) { throw new PanelRefusalException("The server is stopping."); }
        await AuditAsync(admin, AdminAudit.Broadcast, "everyone", text + (repeats > 0
            ? string.Create(CultureInfo.InvariantCulture, $" (then every {everyMinutes} min, {repeats} more times)") : ""));
        return reached;
    }

    /// <summary>On the loop: the marquee to every session with a character; how many there were.</summary>
    private async Task<int> SendBroadcastAsync(string text)
    {
        var targets = sessions.All().Where(s => s.Role != null).ToList();
        foreach (var session in targets)
        {
            try { await GmCommands.ReplyAsync(session, text); }
            catch (Exception e) when (e is not OperationCanceledException) { LogBroadcastFailed(log, e, session.Account ?? ""); }
        }
        return targets.Count;
    }

    /// <summary>On the loop: the next showing of a repeating broadcast, which schedules the one after it.</summary>
    private void Schedule(PanelBroadcast broadcast) =>
        deadlines.SetGlobal(BroadcastKey + broadcast.Id.ToString(CultureInfo.InvariantCulture), broadcast.Next, async (_, _, _) =>
        {
            PanelBroadcast? after;
            lock (_broadcastGate)
            {
                if (!_broadcasts.TryGetValue(broadcast.Id, out var current) || current != broadcast) return;
                after = current.Remaining > 1 ? current with { Remaining = current.Remaining - 1, Next = current.Next.AddMinutes(current.EveryMinutes) } : null;
                if (after != null) _broadcasts[after.Id] = after;
                else _broadcasts.Remove(current.Id);
            }
            await SendBroadcastAsync(broadcast.Text);
            if (after != null) Schedule(after);
        });

    public async Task<bool> CancelBroadcastAsync(string admin, long id)
    {
        PanelBroadcast? cancelled;
        lock (_broadcastGate)
            if (!_broadcasts.Remove(id, out cancelled)) return false;
        try
        {
            await loop.InvokeAsync(() =>
            {
                deadlines.CancelGlobal(BroadcastKey + id.ToString(CultureInfo.InvariantCulture));
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException) { } // stopping: no deadline fires any more
        await AuditAsync(admin, AdminAudit.Broadcast, "everyone", "cancelled the repeats of: " + cancelled.Text);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Panel broadcast to {Account} failed")]
    private static partial void LogBroadcastFailed(ILogger logger, Exception error, string account);
}
