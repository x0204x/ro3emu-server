using Phonest.Proto;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Ro3Emu.Bot;

public enum BotEventKind { ConnectResponse, Entity, Rpc, Game, Disconnect }

/// <summary>One thing the server sent to the bot.</summary>
public sealed record BotEvent(BotEventKind Kind)
{
    /// <summary>RPC method name or channel entity type.</summary>
    public string? Name { get; init; }

    public uint MsgId { get; init; }

    public byte[] Body { get; init; } = [];

    public ConnectServerResp.Types.RespType ConnectResult { get; init; }

    public bool IsGame(uint msgId) => Kind == BotEventKind.Game && MsgId == msgId;
}

/// <summary>Everything received, in order; waiters consume the first unconsumed matching event.</summary>
public sealed class BotInbox
{
    private readonly List<BotEvent> _events = [];
    private readonly HashSet<BotEvent> _consumed = new(ReferenceEqualityComparer.Instance);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly List<BotExpectation> _expectations = [];
    private int _capacity = int.MaxValue;
    private Action<BotEvent>? _observer;
    private long _dropped;
    private Exception? _failure;
    /// <summary>Called for a typed expectation when its reply arrives, measured from registration before send.</summary>
    public Action<BotEvent, double>? OnExpectedReply { get; set; }

    /// <summary>Opt-in bounded typed awaiters for sustained load; unsolicited bodies go only to the observer.</summary>
    public void ConfigureBounded(int capacity, Action<BotEvent> observer)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        lock (_events) { _capacity = Math.Min(capacity, 32); _observer = observer; }
    }

    public long Dropped => Interlocked.Read(ref _dropped);

    public void Add(BotEvent e)
    {
        Action<BotEvent>? observer;
        Action<BotEvent, double>? expectedObserver = null;
        double expectedMs = 0;
        BotExpectation? matched = null;
        lock (_events)
        {
            observer = _observer;
            var waiter = _expectations.FirstOrDefault(x => x.Match(e));
            if (waiter != null)
            {
                _expectations.Remove(waiter);
                matched = waiter;
                expectedObserver = OnExpectedReply;
                expectedMs = waiter.ElapsedMilliseconds;
                if (_capacity == int.MaxValue)
                {
                    _events.Add(e);
                    _consumed.Add(e);
                }
            }
            else if (_capacity == int.MaxValue)
            {
                _events.Add(e);
            }
            else Interlocked.Increment(ref _dropped);
        }
        try { expectedObserver?.Invoke(e, expectedMs); }
        finally { matched?.Complete(e); }
        observer?.Invoke(e);
        if (_capacity == int.MaxValue) _signal.Release();
    }

    public void Fail(Exception e)
    {
        lock (_events)
        {
            _failure = e;
            foreach (var waiter in _expectations) waiter.Fail(e);
            _expectations.Clear();
        }
        if (_capacity == int.MaxValue) _signal.Release();
    }

    /// <summary>Register before sending so a fast reply cannot race the waiter. A timeout names the filter.</summary>
    public BotExpectation Expect(Func<BotEvent, bool> match, [CallerArgumentExpression(nameof(match))] string? what = null)
    {
        lock (_events)
        {
            if (_failure != null) throw new InvalidOperationException("bot connection failed", _failure);
            if (_capacity != int.MaxValue && _expectations.Count >= _capacity)
                throw new InvalidOperationException("bot typed awaiter capacity exceeded");
            var waiter = new BotExpectation(match, RemoveExpectation, what);
            _expectations.Add(waiter);
            return waiter;
        }
    }

    private void RemoveExpectation(BotExpectation waiter)
    {
        lock (_events) _expectations.Remove(waiter);
    }

    internal static string TimedOut(TimeSpan timeout, string? what) => string.Create(CultureInfo.InvariantCulture,
        $"bot: expected server message did not arrive in {timeout.TotalSeconds:0.#} s: {what}");

    public IReadOnlyList<BotEvent> Snapshot()
    {
        lock (_events) return [.. _events];
    }

    public async Task<BotEvent> WaitAsync(Func<BotEvent, bool> match, TimeSpan timeout, [CallerArgumentExpression(nameof(match))] string? what = null)
    {
        if (_capacity != int.MaxValue) throw new InvalidOperationException("load mode requires Expect before send");
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            lock (_events)
            {
                var hit = _events.FirstOrDefault(e => !_consumed.Contains(e) && match(e));
                if (hit != null)
                {
                    _consumed.Add(hit);
                    return hit;
                }
                // Checked under the same lock as the scan: a connection's end adds its Disconnect event and then fails,
                // so a failure seen here comes after any event it was meant to deliver.
                if (_failure != null) throw new InvalidOperationException("bot connection failed", _failure);
            }
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !await _signal.WaitAsync(left)) throw new TimeoutException(TimedOut(timeout, what));
        }
    }
}

public sealed class BotExpectation(Func<BotEvent, bool> match, Action<BotExpectation> remove, string? what = null) : IDisposable
{
    private readonly TaskCompletionSource<BotEvent> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long _registered = Stopwatch.GetTimestamp();
    public bool Match(BotEvent e) => match(e);
    public bool IsCompleted => _source.Task.IsCompletedSuccessfully;
    internal double ElapsedMilliseconds => Stopwatch.GetElapsedTime(_registered).TotalMilliseconds;
    internal void Complete(BotEvent e) => _source.TrySetResult(e);
    internal void Fail(Exception e) => _source.TrySetException(e);
    public async Task<BotEvent> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try { return await _source.Task.WaitAsync(timeout, cancellationToken); }
        catch (TimeoutException e) { throw new TimeoutException(BotInbox.TimedOut(timeout, what), e); }
    }
    public void Dispose() => remove(this);
}
