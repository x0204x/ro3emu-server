using System.Net;
using System.Net.Sockets;

namespace Ro3Emu.Tests;

/// <summary>
/// Distinct loopback listener ports for one testhost process. This does not retain an OS socket lease, so two test
/// processes probing the same range could pick the same free port and then both bind it (seen when several worktrees
/// run the suite at once: hosts failing to start in 1 ms). Each process therefore claims one partition of the range
/// for its lifetime with an exclusive lock file; only when every partition is taken does it share the whole range.
/// </summary>
internal static class TestPorts
{
    // The default pool stays below client ephemeral ranges (Windows: 49152+, Linux: 32768+).
    // RO3EMU_TEST_PORT_RANGE overrides it (inclusive, e.g. 40000-48999 on Windows); partitions apply to either.
    // A port-0 probe can otherwise be consumed by an outgoing connection while the test host is starting.
    // This still is not a lease against unrelated processes deliberately binding the same server port.
    // A full run with the client data starts ~2300 hosts of 3 ports each (11.2026), more than one partition holds, so
    // the allocator hands a port out again once it has rested (TestPortAllocator.ReuseAfterMs).
    private const int Partitions = 2;
    private static FileStream? _partitionLock; // held until the process exits
    private static readonly TestPortAllocator Allocator = Create(ClaimPartition(ReadPortRange()));

    private static TestPortAllocator Create((int First, int End) range) =>
        new(range.First, range.End, () => Environment.TickCount64, CanBind, Random.Shared.Next);

    private static (int First, int End) ClaimPartition((int First, int EndExclusive) pool)
    {
        int size = (pool.EndExclusive - pool.First) / Partitions;
        for (int i = 0; i < Partitions; i++)
        {
            try
            {
                _partitionLock = new FileStream(Path.Combine(Path.GetTempPath(), $"ro3emu-testports-{pool.First}-{i}.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return (pool.First + i * size, pool.First + (i + 1) * size);
            }
            catch (IOException) { } // another test process owns this partition
        }
        return (pool.First, pool.EndExclusive);
    }

    internal static (int Http, int Micro, int Game) FreePorts() =>
        (FreePort(), FreePort(), FreePort());

    internal static int FreePort() => Allocator.Next();

    /// <summary>The safety net: a port a host still listens on (or that is otherwise taken) cannot be bound.</summary>
    private static bool CanBind(int port)
    {
        using var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        try { listener.Start(); }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        { return false; }
        return true;
    }

    private static (int First, int EndExclusive) ReadPortRange()
    {
        string? configured = Environment.GetEnvironmentVariable("RO3EMU_TEST_PORT_RANGE");
        if (string.IsNullOrWhiteSpace(configured)) return (20001, 32768);
        string[] parts = configured.Split('-');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int first) ||
            !int.TryParse(parts[1], out int last) || first < 1024 || last > 65535 || first >= last)
            throw new InvalidOperationException("RO3EMU_TEST_PORT_RANGE must be an inclusive unprivileged port range, e.g. 40000-48999.");
        return (first, last + 1);
    }
}

/// <summary>
/// Hands out the ports of [<paramref name="first"/>, <paramref name="end"/>): never-used ports first, then a port
/// handed out at least <see cref="ReuseAfterMs"/> ago, which by then belongs to a host that bound it long since (and
/// is still checked with <paramref name="canBind"/>, so a host that still listens on it keeps it). A port handed out
/// more recently is never repeated: its host may not have bound it yet.
/// </summary>
internal sealed class TestPortAllocator(int first, int end, Func<long> nowMs, Func<int, bool> canBind, Func<int, int> random)
{
    internal const long ReuseAfterMs = 60_000;
    private readonly object _gate = new();
    private readonly Dictionary<int, long> _handedOut = []; // port → when it was last handed out

    internal int Next()
    {
        lock (_gate)
        {
            long now = nowMs();
            if ((Scan(now, reuse: false) ?? Scan(now, reuse: true)) is { } port) return Hand(port, now);
            throw new InvalidOperationException(
                $"No free distinct loopback test port in {first}-{end - 1} ({_handedOut.Count} handed out, none rested for {ReuseAfterMs} ms).");
        }
    }

    private int Hand(int port, long now)
    {
        _handedOut[port] = now;
        return port;
    }

    /// <summary>Every port of the range is tried once from a random start: random probes alone stop finding the last
    /// free ports of a nearly used-up range.</summary>
    private int? Scan(long now, bool reuse)
    {
        int size = end - first, start = random(size);
        for (int i = 0; i < size; i++)
        {
            int port = first + (start + i) % size;
            if (port is 5380 or 20000 or 33100 or 33101) continue; // Never use the application's default ports.
            bool used = _handedOut.TryGetValue(port, out long at);
            if (used != reuse || used && now - at < ReuseAfterMs) continue;
            if (canBind(port)) return port;
        }
        return null;
    }
}
