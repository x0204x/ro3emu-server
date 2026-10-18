using Phonest.Proto;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Game-gate sessions by account. One live session per account: a new login closes the old connection with a
/// DisconnectClientInfo (the client shows "logged in elsewhere" style kick), like the original server's replace kick.
/// </summary>
public sealed class SessionRegistry(ILogger<SessionRegistry> log, Ownership.OwnerRegistry? owners = null,
    Ownership.RoleHandover? handover = null)
{
    /// <summary>Phonest DisconnectClientReason BE_KICKED; the client then calls onKick and shows its dialog.</summary>
    private const int ReasonKicked = 21;

    private readonly object _sync = new();
    private readonly Dictionary<string, GameSession> _byAccount = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _generations = new(StringComparer.Ordinal);
    /// <summary>Copy-on-write snapshot for <see cref="All"/>: tickers enumerate it many times a second; it changes
    /// only on (un)registration.</summary>
    private volatile GameSession[] _snapshot = [];
    internal Action<GameSession>? UnregisteredForTest { get; set; }

    /// <summary>
    /// The one account transition of a game connection; nothing else sets <see cref="GameSession.Account"/>. The first
    /// sign-in binds the account (and registers the session for a new connection). Another account on the same
    /// connection first drops the old account's key, so no key of it keeps pointing here, and is refused (false) while a
    /// role of the old account is selected: that role's world state belongs to the old account.
    /// </summary>
    public async Task<bool> SignInAsync(GameSession session, string account, bool register)
    {
        if (session.Account != null && !string.Equals(session.Account, account, StringComparison.Ordinal))
        {
            if (session.Role != null) return false;
            Unregister(session);
        }
        session.Account = account;
        if (register) await RegisterAsync(session);
        return true;
    }

    public async Task RegisterAsync(GameSession session)
    {
        owners?.AssertLoop();
        session.Owners = owners;
        GameSession? previous;
        lock (_sync)
        {
            _byAccount.TryGetValue(session.Account!, out previous);
            _generations.TryGetValue(session.Account!, out long generation);
            session.AccountGeneration = checked(generation + 1);
            _generations[session.Account!] = session.AccountGeneration;
            _byAccount[session.Account!] = session;
            _snapshot = _byAccount.Values.ToArray();
        }
        if (previous == null || previous == session) return;
        // Claim the newest account generation before any kick send or old cleanup can suspend.
        Task oldHandover = handover?.Begin(previous) ?? Task.CompletedTask;
        if ((!oldHandover.IsCompleted || !previous.PredecessorFence.IsCompleted) && owners != null)
            owners.HandoverConflicts++;
        session.PredecessorFence = Task.WhenAll(previous.PredecessorFence, oldHandover);
        log.LogInformation("{Account} logged in again; closing the older connection {Remote}", session.Account, previous.Channel.Remote);
        try
        {
            await previous.Channel.SendAsync(GameGateServer.DisconnectClient, new DisconnectClientInfo { Reason = ReasonKicked });
        }
        catch (Exception e) { log.LogWarning(e, "Optional replacement kick notice failed for {Account}", session.Account); }
        try { await previous.Channel.DisposeAsync(); }
        catch (Exception e) { log.LogWarning(e, "Old connection close failed for {Account}; handover remains active", session.Account); }
    }

    public bool IsWinner(GameSession session)
    {
        owners?.AssertLoop();
        lock (_sync)
            return session.Account != null && _byAccount.TryGetValue(session.Account, out var current) &&
                ReferenceEquals(current, session) && _generations[session.Account] == session.AccountGeneration;
    }

    public void Unregister(GameSession session)
    {
        lock (_sync)
        {
            if (session.Account != null && _byAccount.TryGetValue(session.Account, out var current) && current == session)
            {
                _byAccount.Remove(session.Account);
                _snapshot = _byAccount.Values.ToArray();
                UnregisteredForTest?.Invoke(session);
            }
        }
    }

    /// <summary>The registered sessions; an immutable snapshot (callers must not cast and mutate it).</summary>
    public IReadOnlyList<GameSession> All() => _snapshot;
}
