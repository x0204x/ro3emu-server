using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Ro3Emu.Patcher;

namespace Ro3Emu.Patcher.Tests;

/// <summary>隔離 TCP/TLS fixture：不使用系統 DNS、不安裝根憑證，也不略過 hostname 驗證。</summary>
public sealed class RepositoryTransportTests
{
    private static readonly Uri Origin = new("https://hiroshi-pc3.taila2669f.ts.net/ro3/");
    private static readonly RepositoryTrustOptions Trust = new() { RequireHttps = true };

    [Fact]
    public void WithoutAnAddressTheExistingDnsAndProxyPathIsUnchanged()
    {
        using var handler = RepositoryTransport.Create(new Uri("https://unrelated.example/"), null);
        Assert.Null(handler.ConnectCallback);
        Assert.True(handler.UseProxy);
        Assert.True(handler.AllowAutoRedirect);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Null(handler.SslOptions.CertificateChainPolicy);
    }

    [Theory]
    [InlineData("http://hiroshi-pc3.taila2669f.ts.net/ro3/", "100.126.246.106")]
    [InlineData("https://other.taila2669f.ts.net/ro3/", "100.126.246.106")]
    [InlineData("https://hiroshi-pc3.taila2669f.ts.net.evil.example/", "100.126.246.106")]
    [InlineData("https://hiroshi-pc3.taila2669f.ts.net:444/", "100.126.246.106")]
    [InlineData("https://user@hiroshi-pc3.taila2669f.ts.net/", "100.126.246.106")]
    [InlineData("https://100.126.246.106/ro3/", "100.126.246.106")]
    [InlineData("https://hiroshi-pc3.taila2669f.ts.net/", "127.0.0.1")]
    [InlineData("https://hiroshi-pc3.taila2669f.ts.net/", "100.126.246.106:443")]
    [InlineData("https://hiroshi-pc3.taila2669f.ts.net/", "")]
    public void AConnectionAddressCannotAuthorizeAnotherOriginOrAddress(string uri, string address)
    {
        Assert.Throws<PatcherException>(() => new RepositoryClient(new Uri(uri), Trust, connectionAddress: address));
    }

    [Fact]
    public async Task ApprovedTcpAddressKeepsTheOriginalUriSniHostAndNativeTlsValidation()
    {
        await using var server = new TlsOrigin(RepositoryTransport.ApprovedHost);
        using var handler = server.CreateHandler(trustFixtureRoot: true);
        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.True(string.IsNullOrEmpty(handler.SslOptions.TargetHost));
        using var client = new RepositoryClient(Origin, Trust, handler, RepositoryTransport.ApprovedAddress);
        byte[]? content = null;
        Exception? error = await Record.ExceptionAsync(async () => content = await client.TryGetBytesAsync("probe", CancellationToken.None));
        await server.Completion;
        Assert.Null(server.Failure);
        Assert.Null(error);
        Assert.Equal("verified payload", Encoding.UTF8.GetString(Assert.IsType<byte[]>(content)));
        Assert.Equal(Origin, client.BaseUri);
        Assert.Equal(RepositoryTransport.ApprovedHost, server.Sni);
        Assert.Equal(RepositoryTransport.ApprovedHost, server.Host);
        Assert.Equal("GET /ro3/probe HTTP/1.1", server.RequestLine);
        Assert.Equal(new IPEndPoint(IPAddress.Parse(RepositoryTransport.ApprovedAddress), 443), server.RequestedEndpoint);
        Assert.Equal(1, server.Connections);
    }

    [Theory]
    [InlineData("wrong.example", true, false)]
    [InlineData(RepositoryTransport.ApprovedHost, false, false)]
    [InlineData(RepositoryTransport.ApprovedHost, true, true)]
    public async Task WrongHostnameUntrustedChainAndExpiredCertificateAreRejected(string certificateHost,
        bool trustFixtureRoot, bool expired)
    {
        await using var server = new TlsOrigin(certificateHost, expired);
        using var handler = server.CreateHandler(trustFixtureRoot);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        using var client = new RepositoryClient(Origin, Trust, handler, RepositoryTransport.ApprovedAddress);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.TryGetBytesAsync("probe", CancellationToken.None));
        await server.Completion;
        Assert.True(error.InnerException?.GetType() == typeof(AuthenticationException),
            $"憑證案例必須由原生 TLS 驗證拒絕，不能以握手 EOF 代替。Client: {error}; Fixture: {server.Failure}");
        Assert.Equal(1, server.Connections);
        Assert.Null(server.RequestLine); // TLS 拒絕時未送出 HTTP 要求，也未改走明文。
    }

    [Theory]
    [InlineData("https://other.example/probe", false)]
    [InlineData("http://hiroshi-pc3.taila2669f.ts.net/probe", false)]
    [InlineData("https://hiroshi-pc3.taila2669f.ts.net:444/probe", false)]
    [InlineData("https://hiroshi-pc3.taila2669f.ts.net/probe", true)]
    public async Task AConfiguredHandlerCannotRouteAnotherOriginOrHostOverride(string destination, bool overrideHost)
    {
        int connections = 0;
        using var handler = RepositoryTransport.Create(Origin, RepositoryTransport.ApprovedAddress, (_, _) =>
        {
            connections++;
            return ValueTask.FromResult<Stream>(Stream.Null);
        });
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, destination);
        if (overrideHost) request.Headers.Host = "other.example";
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
        Assert.IsType<PatcherException>(error.InnerException);
        Assert.Equal(0, connections);
    }

    [Theory]
    [InlineData("https://other.example/probe")]
    [InlineData("http://hiroshi-pc3.taila2669f.ts.net/probe")]
    public async Task RedirectCannotChangeTheOriginOrDowngradeTls(string location)
    {
        await using var server = new TlsOrigin(RepositoryTransport.ApprovedHost, redirect: location);
        using var handler = server.CreateHandler(trustFixtureRoot: true);
        using var client = new RepositoryClient(Origin, Trust, handler, RepositoryTransport.ApprovedAddress);
        byte[]? content = null;
        Exception? error = await Record.ExceptionAsync(async () => content = await client.TryGetBytesAsync("probe", CancellationToken.None));
        await server.Completion;
        Assert.Null(server.Failure);
        Assert.Null(error);
        Assert.Null(content);
        Assert.Equal(1, server.Connections);
        Assert.Equal("GET /ro3/probe HTTP/1.1", server.RequestLine);
    }

    [Fact]
    public async Task CancellationReachesTheTcpConnectorWithoutStartingTlsOrRetrying()
    {
        int connections = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = RepositoryTransport.Create(Origin, RepositoryTransport.ApprovedAddress, async (_, ct) =>
        {
            connections++;
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Stream.Null;
        });
        using var client = new HttpClient(handler);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<HttpResponseMessage> pending = client.GetAsync(Origin, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, connections);
    }

    private sealed class TlsOrigin : IAsyncDisposable
    {
        private readonly RSA _rootKey = RSA.Create(2048);
        private readonly RSA _leafKey = RSA.Create(2048);
        private readonly X509Certificate2 _root;
        private readonly X509Certificate2 _leaf;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(10));
        private readonly string? _redirect;
        internal Task Completion { get; }
        internal int Connections { get; private set; }
        internal IPEndPoint? RequestedEndpoint { get; private set; }
        internal string? Sni { get; private set; }
        internal string? Host { get; private set; }
        internal string? RequestLine { get; private set; }
        internal Exception? Failure { get; private set; }

        internal TlsOrigin(string host, bool expired = false, string? redirect = null)
        {
            _redirect = redirect;
            var root = new CertificateRequest("CN=RO3 isolated TLS fixture", _rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            root.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            root.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            _root = root.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(10));
            var leaf = new CertificateRequest("CN=" + host, _leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            leaf.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            leaf.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            leaf.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new("1.3.6.1.5.5.7.3.1")], true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(host);
            leaf.CertificateExtensions.Add(names.Build());
            using var signed = leaf.Create(_root, DateTimeOffset.UtcNow.AddDays(-2),
                DateTimeOffset.UtcNow.AddDays(expired ? -1 : 1), RandomNumberGenerator.GetBytes(16));
            using var keyed = signed.CopyWithPrivateKey(_leafKey);
            byte[] pfx = keyed.Export(X509ContentType.Pkcs12);
            try
            {
                // Windows Schannel 不能使用上述 ephemeral key。只重載隔離測試憑證的暫存金鑰；
                // 不使用 PersistKeySet、不匯入信任庫，Dispose 釋放原生 key context。
                _leaf = X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.DefaultKeySet);
            }
            finally { CryptographicOperations.ZeroMemory(pfx); }
            _listener.Start();
            Completion = ServeAsync();
        }

        internal SocketsHttpHandler CreateHandler(bool trustFixtureRoot)
        {
            var handler = RepositoryTransport.Create(Origin, RepositoryTransport.ApprovedAddress, async (endpoint, ct) =>
            {
                RequestedEndpoint = endpoint;
                Connections++;
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(_listener.LocalEndpoint, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            });
            if (trustFixtureRoot)
            {
                // 只限 fixture 的記憶體根；原生鏈、有效期與主機名稱驗證仍執行，不寫全域信任庫。
                handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    RevocationMode = X509RevocationMode.NoCheck,
                    DisableCertificateDownloads = true,
                };
                handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(_root);
            }
            return handler;
        }

        private async Task ServeAsync()
        {
            try
            {
                using TcpClient socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var tls = new SslStream(socket.GetStream(), leaveInnerStreamOpen: false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateSelectionCallback = (_, name) => { Sni = name; return _leaf; },
                }, _stop.Token);
                using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
                RequestLine = await reader.ReadLineAsync(_stop.Token);
                while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
                    if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) Host = line[5..].Trim();
                string response = _redirect is { } location
                    ? "HTTP/1.1 302 Found\r\nLocation: " + location + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : "HTTP/1.1 200 OK\r\nContent-Length: 16\r\nConnection: close\r\n\r\nverified payload";
                await tls.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
            }
            catch (Exception e) when (e is AuthenticationException or IOException or OperationCanceledException or SocketException)
            {
                // 拒絕憑證時 client 會結束 TLS；保留 fixture 的真正錯誤，不能讓 EOF 冒充驗證拒絕。
                Failure = e;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await Completion;
            _stop.Dispose();
            _leaf.Dispose();
            _root.Dispose();
            _leafKey.Dispose();
            _rootKey.Dispose();
        }
    }
}
