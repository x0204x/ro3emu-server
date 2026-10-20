using System.Net;
using System.Net.Sockets;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Net;

/// <summary>Accepts TCP connections on one port and runs a <see cref="PhonestChannel"/> per client.</summary>
public abstract class GateListener(IPAddress address, int port, ILogger log, Diagnostics.ServerMetrics? metrics = null) : BackgroundService
{
    private const int MaxServeTasks = 4096;
    private readonly object _servesGate = new();
    private readonly Dictionary<long, (Socket Socket, Task Task)> _serves = new();
    private readonly Dictionary<IPAddress, int> _perAddress = new();

    /// <summary>
    /// Open connections one remote address may hold on this gate. A player needs one per gate (two for a moment while
    /// reconnecting), so this leaves room for a household or a LAN party behind one NAT while a single host can no
    /// longer take every slot. Loopback is exempt: local play and the load tests run hundreds of bots from it.
    /// </summary>
    internal int MaxPerAddress { get; init; } = 64;
    internal bool ExemptLoopback { get; init; } = true;
    /// <summary>The client opens its encrypted session right after connecting.</summary>
    internal TimeSpan EncryptDeadline { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>...and signs in right after that; a connection that has done neither by then is dropped.</summary>
    internal TimeSpan SignInDeadline { get; init; } = TimeSpan.FromMinutes(2);
    private readonly AsyncLocal<GameLoop.CompletionReservation?> _serveCompletion = new();
    private long _nextServeId;
    private bool _stopping;
    public bool DrainSucceeded { get; private set; } = true;
    public int OutstandingServeTasks { get { lock (_servesGate) return _serves.Count; } }
    protected ILogger Log { get; } = log;
    protected Diagnostics.ServerMetrics? Metrics { get; } = metrics;

    protected abstract string Name { get; }

    protected abstract ValueTask OnFrameAsync(PhonestChannel channel, ushort index, ReadOnlyMemory<byte> body);

    protected virtual ValueTask OnDisconnectedAsync(PhonestChannel channel) => ValueTask.CompletedTask;
    /// <summary>True once the connection belongs to an account (see <see cref="SignInDeadline"/>).</summary>
    protected virtual bool IsSignedIn(PhonestChannel channel) => true;
    protected virtual GameLoop.CompletionReservation? ReserveServeCompletion() => null;
    protected virtual void OnCleanupFailed(PhonestChannel channel, Exception error) { }
    protected GameLoop.CompletionReservation? CurrentServeCompletion => _serveCompletion.Value;

    private Socket? _listener;

    /// <summary>
    /// Binds before the host reports started. ExecuteAsync runs in the background, so listening there would leave a
    /// moment after startup where connections are refused.
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var listener = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            listener.Bind(new IPEndPoint(address, port));
            listener.Listen(512); // a realm start is a burst of connects (Kestrel's default backlog too)
        }
        catch
        {
            listener.Dispose();
            throw;
        }
        _listener = listener;
        Log.LogInformation("{Name} listening on {Address}:{Port}", Name, address, port);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var listener = _listener!;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var socket = await listener.AcceptAsync(ct);
                socket.NoDelay = true;
                var remote = (socket.RemoteEndPoint as IPEndPoint)?.Address is { } ip && !(ExemptLoopback && IPAddress.IsLoopback(ip))
                    ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip) : null;
                lock (_servesGate)
                {
                    if (_stopping || _serves.Count >= MaxServeTasks)
                    {
                        socket.Dispose();
                        continue;
                    }
                    if (remote != null && _perAddress.GetValueOrDefault(remote) >= MaxPerAddress)
                    {
                        Log.LogWarning("{Name}: {Remote} refused, it already holds {Count} connections", Name, remote, MaxPerAddress);
                        socket.Dispose();
                        continue;
                    }
                    long id = ++_nextServeId;
                    GameLoop.CompletionReservation? completion;
                    try { completion = ReserveServeCompletion(); }
                    catch (InvalidOperationException)
                    {
                        socket.Dispose();
                        continue;
                    }
                    // No cancellation token on Task.Run: a canceled dispatch must still dispose its socket.
                    var task = Task.Run(() => ServeAsync(socket, ct, completion), CancellationToken.None);
                    _serves.Add(id, (socket, task));
                    if (remote != null) _perAddress[remote] = _perAddress.GetValueOrDefault(remote) + 1;
                    _ = task.ContinueWith(_ =>
                        {
                            lock (_servesGate)
                            {
                                _serves.Remove(id);
                                if (remote != null && --_perAddress[remote] == 0) _perAddress.Remove(remote);
                            }
                        },
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_servesGate) _stopping = true;
        await base.StopAsync(cancellationToken);
        Task[] outstanding;
        lock (_servesGate)
        {
            // RunAsync observes the canceled receive token; each channel's existing DisposeAsync policy
            // must retain its chance to flush queued output before the socket closes.
            outstanding = _serves.Values.Select(s => s.Task).ToArray();
        }
        try { await Task.WhenAll(outstanding).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
        catch (Exception e) when (e is TimeoutException || (e is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            DrainSucceeded = false;
            Log.LogError("{Name} serve drain incomplete: {Count} connection tasks remain", Name, OutstandingServeTasks);
        }
        catch (Exception e)
        {
            DrainSucceeded = false;
            Log.LogError(e, "{Name} serve drain failed", Name);
        }
    }

    private async Task ServeAsync(Socket socket, CancellationToken ct, GameLoop.CompletionReservation? completion)
    {
        _serveCompletion.Value = completion;
        try
        {
            await using var channel = new PhonestChannel(socket, Log) { Metrics = Metrics?.Connect() };
            Log.LogInformation("{Name}: {Remote} connected", Name, channel.Remote);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var watch = WatchHandshakeAsync(channel, deadline);
            try
            {
                await channel.RunAsync(OnFrameAsync, deadline.Token);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException or IOException)
            {
            }
            catch (Exception e)
            {
                Log.LogError(e, "{Name}: {Remote} failed", Name, channel.Remote);
            }
            finally
            {
                try { await OnDisconnectedAsync(channel); }
                catch (Exception e)
                {
                    DrainSucceeded = false;
                    OnCleanupFailed(channel, e);
                    Log.LogError(e, "{Name}: {Remote} disconnect cleanup failed", Name, channel.Remote);
                }
                Log.LogInformation("{Name}: {Remote} disconnected", Name, channel.Remote);
                deadline.Cancel();
                await watch;
            }
        }
        finally { _serveCompletion.Value = null; completion?.Dispose(); }
    }

    /// <summary>Ends a connection that has not encrypted, or not signed in, in time (it would hold a slot forever).</summary>
    private async Task WatchHandshakeAsync(PhonestChannel channel, CancellationTokenSource deadline)
    {
        try
        {
            await Task.Delay(EncryptDeadline, deadline.Token);
            if (!channel.Encrypted)
            {
                Log.LogWarning("{Name}: {Remote} did not open an encrypted session in time; closing", Name, channel.Remote);
                deadline.Cancel();
                return;
            }
            if (SignInDeadline > EncryptDeadline) await Task.Delay(SignInDeadline - EncryptDeadline, deadline.Token);
            if (!IsSignedIn(channel))
            {
                Log.LogWarning("{Name}: {Remote} did not sign in in time; closing", Name, channel.Remote);
                deadline.Cancel();
            }
        }
        catch (OperationCanceledException) { }
    }
}
