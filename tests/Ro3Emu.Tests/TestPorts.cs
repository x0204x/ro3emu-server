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
    // One full run takes ~3 ports per host for ~1100 hosts (16.10); half of the default range leaves headroom.
    private const int Partitions = 2;
    private static readonly object Gate = new();
    private static readonly HashSet<int> Selected = new();
    private static FileStream? _partitionLock; // held until the process exits
    private static readonly (int First, int End) Range = ClaimPartition(ReadPortRange());

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

    internal static int FreePort()
    {
        lock (Gate)
        {
            // Every port of the partition is tried once from a random start: random probes alone stop finding the last
            // free ports of a nearly used-up partition.
            int size = Range.End - Range.First, start = Random.Shared.Next(size);
            for (int i = 0; i < size; i++)
            {
                int port = Range.First + (start + i) % size;
                if (port is 5380 or 20000 or 33100 or 33101) continue; // Never use the application's default ports.
                if (Selected.Contains(port)) continue;
                using var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
                try { listener.Start(); }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
                { continue; }
                Selected.Add(port);
                return port;
            }
            throw new InvalidOperationException($"No free distinct loopback test port in {Range.First}-{Range.End - 1} ({Selected.Count} already selected).");
        }
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
