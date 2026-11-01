using System.Net;

namespace Ro3Emu.Panel;

/// <summary>
/// The sign-up budgets (<see cref="PanelOptions.SignUpsPerAddressPerDay"/>, <see cref="PanelOptions.SignUpsPerHour"/>),
/// both rolling windows and off at 0. An attempt reserves its slot before the account is written and gives it back
/// unless the account was created, so concurrent attempts cannot pass a limit and a taken name costs nothing. An IPv6
/// client counts by its /64 network, which one subscriber usually holds whole. In memory: a restart starts afresh.
/// </summary>
internal sealed class PanelSignUps(PanelOptions options, TimeProvider clock)
{
    public enum Refusal { None, Address, Busy }

    /// <summary>A reserved slot: the network it counts against (null: none) and its time.</summary>
    public readonly record struct Slot(IPAddress? Network, DateTimeOffset At, bool Hourly);

    /// <summary>Past this many networks a reservation first drops the ones with nothing left in the window.</summary>
    private const int PruneAt = 4096;
    private static readonly TimeSpan Day = TimeSpan.FromDays(1), Hour = TimeSpan.FromHours(1);

    private readonly object _sync = new();
    private readonly Dictionary<IPAddress, List<DateTimeOffset>> _byNetwork = [];
    private readonly List<DateTimeOffset> _lastHour = [];

    /// <summary>Reserves one sign-up for <paramref name="client"/>; a refusal says which budget is used up.</summary>
    public Refusal TryReserve(IPAddress? client, out Slot slot)
    {
        var now = clock.GetUtcNow();
        int perAddress = options.SignUpsPerAddressPerDay, perHour = options.SignUpsPerHour;
        var network = perAddress > 0 && client != null ? Network(client) : null;
        slot = new(network, now, perHour > 0);
        lock (_sync)
        {
            _lastHour.RemoveAll(t => t <= now - Hour);
            if (perHour > 0 && _lastHour.Count >= perHour) return Refusal.Busy;
            if (network != null)
            {
                if (_byNetwork.Count >= PruneAt)
                    foreach (var stale in _byNetwork.Where(e => e.Value.All(t => t <= now - Day)).Select(e => e.Key).ToList()) _byNetwork.Remove(stale);
                if (!_byNetwork.TryGetValue(network, out var times)) _byNetwork[network] = times = [];
                times.RemoveAll(t => t <= now - Day);
                if (times.Count >= perAddress) return Refusal.Address;
                times.Add(now);
            }
            if (slot.Hourly) _lastHour.Add(now);
            return Refusal.None;
        }
    }

    /// <summary>Gives a slot back: the attempt did not create an account.</summary>
    public void Release(Slot slot)
    {
        lock (_sync)
        {
            if (slot.Network != null && _byNetwork.TryGetValue(slot.Network, out var times)) times.Remove(slot.At);
            if (slot.Hourly) _lastHour.Remove(slot.At);
        }
    }

    /// <summary>An IPv4 address itself; an IPv6 address's /64 network.</summary>
    internal static IPAddress Network(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return address;
        byte[] bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes);
    }
}
