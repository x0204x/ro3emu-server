using System.Net;
using Microsoft.AspNetCore.Http;

namespace Ro3Emu.Panel;

/// <summary>
/// Who sent a request: <paramref name="Address"/> is the connection's peer, or behind a trusted proxy the client it
/// forwards; <paramref name="Forwarded"/> is true when the request carries a forwarding header (X-Forwarded-For,
/// Forwarded, X-Real-IP), trusted or not. A loopback peer with such a header is a proxy on the same machine, not a
/// local tool, so the loopback exemptions of the limits do not apply to it.
/// </summary>
public readonly record struct RequestClient(IPAddress? Address, bool Forwarded);

/// <summary>
/// The client address of a request for the panel and the server's HTTP port alike, with
/// <see cref="PanelOptions.TrustedProxies"/>: only a listed proxy's X-Forwarded-For and X-Forwarded-Proto are believed.
/// </summary>
public sealed class ClientAddresses(IEnumerable<string> trustedProxies)
{
    private readonly IPAddress[] _proxies = [.. trustedProxies.Select(IPAddress.Parse).Select(Normalize)];

    /// <summary>The client of <paramref name="context"/>: the peer, or behind a trusted proxy the last address it forwarded.</summary>
    public RequestClient Of(HttpContext context)
    {
        bool forwarded = IsForwarded(context.Request);
        if (context.Connection.RemoteIpAddress is not { } peer) return new(null, forwarded);
        peer = Normalize(peer);
        if (!_proxies.Contains(peer)) return new(peer, forwarded);
        string list = context.Request.Headers["X-Forwarded-For"].ToString();
        string last = list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        return new(IPAddress.TryParse(last, out var client) ? Normalize(client) : peer, forwarded);
    }

    /// <summary>https to the browser: the connection itself, or a trusted proxy's X-Forwarded-Proto.</summary>
    public bool Https(HttpContext context) =>
        context.Request.IsHttps || context.Connection.RemoteIpAddress is { } peer && _proxies.Contains(Normalize(peer))
            && string.Equals(context.Request.Headers["X-Forwarded-Proto"].ToString().Split(',')[^1].Trim(), "https", StringComparison.OrdinalIgnoreCase);

    /// <summary>The request names a client other than its peer: some proxy forwarded it.</summary>
    public static bool IsForwarded(HttpRequest request) =>
        request.Headers.ContainsKey("X-Forwarded-For") || request.Headers.ContainsKey("Forwarded") || request.Headers.ContainsKey("X-Real-IP");

    /// <summary>The connection comes from a proxy listed in <see cref="PanelOptions.TrustedProxies"/>.</summary>
    public bool FromTrustedProxy(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } peer && _proxies.Contains(Normalize(peer));

    /// <summary>
    /// The request's Host names this machine (127.0.0.1, localhost or [::1], any port). A page served from another name
    /// that resolves to 127.0.0.1 (DNS rebinding) sends its own name, so a loopback service that checks this cannot be
    /// driven from a browser through that page.
    /// </summary>
    public static bool LoopbackHost(HttpRequest request) =>
        request.Host.Host is { Length: > 0 } host && (host == "127.0.0.1" || host == "[::1]" || host == "::1"
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What a per-address budget counts: an IPv4 address itself, an IPv6 address's /64 network, which one subscriber
    /// usually holds whole (one client could otherwise take a fresh budget from every address of it).
    /// </summary>
    public static IPAddress Network(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return address;
        byte[] bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes);
    }

    internal static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
