using System.Diagnostics;

namespace Ro3Emu.Server.Game.Messaging;

public delegate Task MessageHandler(GameSession session, uint msgId, ReadOnlyMemory<byte> body);

/// <summary>Whether an admission rule applies to this message from this session.</summary>
public delegate bool AdmissionTest(GameSession session, uint msgId, ReadOnlyMemory<byte> body);

/// <summary>Returns true when it handled the message; false lets the next interceptor or the route have it.</summary>
public delegate Task<bool> MessageInterceptor(GameSession session, uint msgId, ReadOnlyMemory<byte> body);

/// <summary>
/// Game-gate message routes (audit F4.1): one owner per message id, declared where the message is handled.
/// A duplicate id or an overlapping range is a startup error instead of a silently shadowed switch arm. Every route
/// counts its calls, loop time and failures (the per-message handling-time view).
/// </summary>
public sealed class MessageRoutes
{
    public sealed class Route(uint from, uint to, string owner, MessageHandler handler)
    {
        public uint From { get; } = from;
        public uint To { get; } = to;
        public string Owner { get; } = owner;
        private long _calls, _failures, _ticks;
        public long Calls => Interlocked.Read(ref _calls);
        public long Failures => Interlocked.Read(ref _failures);
        public double TotalMs => Interlocked.Read(ref _ticks) * 1000d / Stopwatch.Frequency;

        public async Task InvokeAsync(GameSession session, uint msgId, ReadOnlyMemory<byte> body)
        {
            Interlocked.Increment(ref _calls);
            long started = Stopwatch.GetTimestamp();
            try { await handler(session, msgId, body); }
            catch { Interlocked.Increment(ref _failures); throw; }
            finally { Interlocked.Add(ref _ticks, Stopwatch.GetTimestamp() - started); }
        }
    }

    private readonly Dictionary<uint, Route> _exact = new();
    private readonly List<Route> _ranges = new();
    private readonly List<(string Owner, Func<uint, bool> Claims, MessageInterceptor Handler)> _interceptors = new();

    /// <summary>
    /// A handler tried before the message's route, in registration order; the first that returns true has handled
    /// the message. For messages whose owner depends on the player's state: a siege, a territory battle or an ordinary
    /// dungeon answer the same statistics request, and several systems own their own NPCs behind one interaction.
    /// </summary>
    public void Intercept(string owner, Func<uint, bool> claims, MessageInterceptor handler) =>
        _interceptors.Add((owner, claims, handler));

    private readonly List<AdmissionTest> _economic = new();
    private readonly List<(Func<uint, bool> Ids, string Origin)> _runtime = new();

    /// <summary>
    /// Messages admitted as economic requests: their handlers reserve exactly what they change themselves, so the
    /// dispatcher reserves nothing up front.
    /// </summary>
    public void AdmitEconomic(AdmissionTest when) => _economic.Add(when);

    public void AdmitEconomic(Func<uint, bool> ids) => _economic.Add((_, id, _) => ids(id));

    /// <summary>Messages that continue work another operation already owns (scene load, logout, heartbeat...).</summary>
    public void AdmitRuntime(Func<uint, bool> ids, string origin) => _runtime.Add((ids, origin));

    private readonly List<Func<uint, bool>> _role = new();
    private readonly List<Func<uint, bool>> _account = new();

    /// <summary>Messages that change only the sender's role: they reserve that role instead of the whole realm.</summary>
    public void AdmitRole(Func<uint, bool> ids) => _role.Add(ids);

    /// <summary>Messages that change only the sender's account (role creation on the role screen).</summary>
    public void AdmitAccount(Func<uint, bool> ids) => _account.Add(ids);

    private readonly List<(SessionStage Stage, Func<uint, bool> Ids)> _early = [];

    /// <summary>
    /// Messages the client sends before its role is in the world, from <paramref name="stage"/> on. Every message not
    /// declared here needs a role; the dispatcher refuses it earlier (<see cref="Accepts"/>).
    /// </summary>
    public void AllowFrom(SessionStage stage, Func<uint, bool> ids) => _early.Add((stage, ids));

    /// <summary>The earliest stage that accepts a message: the lowest it is declared for, else the world.</summary>
    public SessionStage RequiredStage(uint msgId) =>
        _early.Where(early => early.Ids(msgId)).Select(early => early.Stage).DefaultIfEmpty(SessionStage.World).Min();

    public bool Accepts(GameSession session, uint msgId) => SessionStages.Of(session) >= RequiredStage(msgId);

    /// <summary>
    /// How a message is admitted: economic, then runtime, then an id the client never sends, else under the sender's
    /// account, its role, or the whole realm for whatever is not declared narrower.
    /// </summary>
    public Admission Admit(GameSession session, uint msgId, ReadOnlyMemory<byte> body, Func<uint, bool> unknown)
    {
        foreach (var economic in _economic)
            if (economic(session, msgId, body)) return Admission.Economic;
        foreach (var (ids, origin) in _runtime)
            if (ids(msgId)) return Admission.Runtime(origin);
        if (unknown(msgId)) return Admission.Runtime("dispatcher/unknown message");
        return Admission.Session(SessionKeys(session, msgId),
            TryGet(msgId, out _) ? $"dispatcher/{msgId}" : "dispatcher/unrouted");
    }

    private Ownership.ReservationKey[] SessionKeys(GameSession session, uint msgId)
    {
        if (_account.Any(ids => ids(msgId))) return [Ownership.ReservationKey.Account(session.Account ?? "unbound")];
        if (session.Role is { } role && _role.Any(ids => ids(msgId))) return [Ownership.ReservationKey.Role(role.Id)];
        return [Ownership.ReservationKey.Realm];
    }

    /// <summary>Runs the interceptors that claim the message, then its route, or <paramref name="unrouted"/>.</summary>
    public async Task DispatchAsync(GameSession session, uint msgId, ReadOnlyMemory<byte> body, MessageHandler unrouted)
    {
        foreach (var (_, claims, handler) in _interceptors)
            if (claims(msgId) && await handler(session, msgId, body)) return;
        if (TryGet(msgId, out var route)) await route.InvokeAsync(session, msgId, body);
        else await unrouted(session, msgId, body);
    }

    public IReadOnlyCollection<Route> Exact => _exact.Values;
    public IReadOnlyList<Route> Ranges => _ranges;

    public void Map(uint id, string owner, MessageHandler handler)
    {
        if (_exact.TryGetValue(id, out var existing))
            throw new InvalidOperationException($"Message {id} is routed twice ({existing.Owner}, {owner}).");
        if (_ranges.FirstOrDefault(r => id >= r.From && id <= r.To) is { } range)
            throw new InvalidOperationException($"Message {id} ({owner}) falls in {range.Owner}'s range {range.From}-{range.To}.");
        _exact.Add(id, new Route(id, id, owner, handler));
        Diagnostics.MessageCoverage.Route(id, id, owner);
    }

    public void MapRange(uint from, uint to, string owner, MessageHandler handler)
    {
        if (from > to) throw new ArgumentException($"Empty range {from}-{to} for {owner}.");
        if (_exact.Keys.FirstOrDefault(id => id >= from && id <= to) is var id && id != 0)
            throw new InvalidOperationException($"Range {from}-{to} ({owner}) covers routed message {id} ({_exact[id].Owner}).");
        if (_ranges.FirstOrDefault(r => from <= r.To && r.From <= to) is { } overlap)
            throw new InvalidOperationException($"Range {from}-{to} ({owner}) overlaps {overlap.Owner}'s {overlap.From}-{overlap.To}.");
        _ranges.Add(new Route(from, to, owner, handler));
        Diagnostics.MessageCoverage.Route(from, to, owner);
    }

    public bool TryGet(uint id, out Route route)
    {
        if (_exact.TryGetValue(id, out route!)) return true;
        foreach (var range in _ranges)
            if (id >= range.From && id <= range.To) { route = range; return true; }
        return false;
    }

    /// <summary>Slowest routes by total loop time (diagnostics).</summary>
    public object Snapshot(int top = 20) => _exact.Values.Concat(_ranges)
        .Where(r => r.Calls > 0).OrderByDescending(r => r.TotalMs).Take(top)
        .Select(r => new
        {
            id = r.From == r.To ? r.From.ToString() : $"{r.From}-{r.To}", owner = r.Owner, calls = r.Calls,
            totalMs = Math.Round(r.TotalMs, 1), avgMs = Math.Round(r.TotalMs / r.Calls, 3), failures = r.Failures
        })
        .ToArray();
}
