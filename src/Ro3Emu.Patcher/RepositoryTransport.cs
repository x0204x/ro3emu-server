using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Ro3Emu.Patcher.Tests")]

namespace Ro3Emu.Patcher;

/// <summary>只替核准的私有 HTTPS 來源指定 TCP 位址；TLS 身份仍由原始 URI 與平台驗證。</summary>
internal static class RepositoryTransport
{
    internal const string ApprovedHost = "hiroshi-pc3.taila2669f.ts.net";
    internal const string ApprovedAddress = "100.126.246.106";

    internal static void Validate(Uri origin, string? connectionAddress)
    {
        if (connectionAddress is null) return;
        if (!Approved(origin) || !string.Equals(connectionAddress, ApprovedAddress, StringComparison.Ordinal))
            throw new PatcherException("更新來源的指定連線位址不符合核准的 HTTPS 主機與連接埠。");
    }

    // connect 只供同組件測試注入隔離的 loopback TCP stream；不從設定或通道接受替代位址。
    internal static SocketsHttpHandler Create(Uri origin, string? connectionAddress,
        Func<IPEndPoint, CancellationToken, ValueTask<Stream>>? connect = null)
    {
        Validate(origin, connectionAddress);
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (connectionAddress is null) return handler;

        // 私有來源直連；不能把代理伺服器的端點或重導向目的地誤接至這個 IP。
        handler.UseProxy = false;
        handler.AllowAutoRedirect = false;
        handler.ConnectCallback = (context, ct) =>
        {
            if (context.InitialRequestMessage.RequestUri is not { } requestUri || !Approved(requestUri)
                || !string.Equals(context.DnsEndPoint.Host, ApprovedHost, StringComparison.OrdinalIgnoreCase)
                || context.DnsEndPoint.Port != 443 || context.InitialRequestMessage.Headers.Host is not null)
                throw new PatcherException("指定連線位址只適用於原始核准的 HTTPS 更新來源。");
            ct.ThrowIfCancellationRequested();
            var endpoint = new IPEndPoint(IPAddress.Parse(ApprovedAddress), 443);
            return connect is null ? ConnectAsync(endpoint, ct) : connect(endpoint, ct);
        };
        // 不改 SslOptions：SNI、憑證鏈與 hostname 仍由 SocketsHttpHandler 依原 URI 驗證。
        return handler;
    }

    private static bool Approved(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.IdnHost, ApprovedHost, StringComparison.OrdinalIgnoreCase)
        && uri.Port == 443 && uri.UserInfo.Length == 0;

    private static async ValueTask<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
