using System.Net.Http.Headers;
using System.Text.Json;

namespace Ro3Emu.Patcher;

/// <summary>
/// 透過 HTTPS 讀取更新來源；HTTP 僅允許本機測試。通道、簽章與小型已簽署資產不使用快取，
/// TLS 與發行者簽章各自驗證，不能以關閉信任旗標略過遠端 HTTPS 限制。
/// </summary>
public sealed class RepositoryClient : IDisposable
{
    readonly HttpClient _http;
    readonly bool _ownsClient;
    readonly RepositoryTrustOptions _trust;
    readonly string? _refused;

    public RepositoryClient(Uri baseUri, HttpMessageHandler? handler = null)
        : this(baseUri, RepositoryTrustOptions.UnsignedLab, handler)
    {
    }

    public RepositoryClient(Uri baseUri, RepositoryTrustOptions trust, HttpMessageHandler? handler = null,
        string? connectionAddress = null)
    {
        ArgumentNullException.ThrowIfNull(trust);
        RepositoryTransport.Validate(baseUri, connectionAddress);
        _refused = Allowed(baseUri) ? null
            : $"The update server address {baseUri.GetLeftPart(UriPartial.Authority)} does not use https, so its files " +
              "cannot be trusted. Ask the server's owner for its https address.";
        if (!baseUri.AbsoluteUri.EndsWith('/'))
        {
            baseUri = new Uri(baseUri.AbsoluteUri + "/");
        }
        if (trust.RequireHttps && baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new PatcherException("This launcher only accepts an HTTPS update repository.");
        }
        if (trust.RequireSignedChannel && trust.TrustedPublicKeys.Count == 0)
        {
            throw new PatcherException("Signed update channels are required but no trusted publisher key is configured.");
        }
        _trust = trust;
        _http = handler is null
            ? new HttpClient(RepositoryTransport.Create(baseUri, connectionAddress))
            : new HttpClient(handler, disposeHandler: false);
        _ownsClient = true;
        _http.BaseAddress = baseUri;
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Ro3EmuLauncher/1.0");
    }

    public Uri BaseUri => _http.BaseAddress!;

    public bool RequiresSignedChannel => _trust.RequireSignedChannel;

    /// <summary>HTTPS，或僅連到本機的 HTTP（localhost、127.0.0.0/8、::1）。</summary>
    public static bool Allowed(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    void ThrowIfRefused()
    {
        if (_refused != null) throw new PatcherException(_refused);
    }

    public async Task<ChannelInfo> GetChannelAsync(string channel, CancellationToken ct)
    {
        ThrowIfRefused();
        if (string.IsNullOrWhiteSpace(channel) || channel.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '-')))
        {
            throw new PatcherException("The update channel name is invalid.");
        }
        string relative = $"channels/{channel}.json";
        byte[] raw = await GetMetadataAsync(relative, required: true, ct)
            ?? throw new PatcherException("The update server returned an empty channel.");
        if (_trust.RequireSignedChannel)
        {
            byte[] signature = await GetMetadataAsync(relative + ".sig", required: true, ct)
                ?? throw new PatcherException("The update channel has no signature.");
            RepositorySignatures.Verify(raw, signature, _trust);
        }
        ChannelInfo result;
        try
        {
            result = JsonSerializer.Deserialize<ChannelInfo>(raw, Json.Compact)
                ?? throw new PatcherException("The update server returned an empty channel.");
        }
        catch (JsonException e)
        {
            throw new PatcherException("The update channel is invalid JSON.", e);
        }
        if (!string.Equals(result.Channel, channel, StringComparison.Ordinal))
        {
            throw new PatcherException("The signed update channel identity does not match the requested channel.");
        }
        if (result.Sequence < 0 || _trust.RequireSignedChannel && result.Sequence == 0)
        {
            throw new PatcherException("The update channel sequence is invalid.");
        }
        if (_trust.RequireSignedChannel && result.Expires is null)
        {
            throw new PatcherException("The signed update channel has no expiry.");
        }
        if (result.Expires is { } expires && expires <= DateTimeOffset.UtcNow)
        {
            throw new PatcherException("The signed update channel has expired.");
        }
        return result;
    }

    public async Task<(Manifest Manifest, byte[] Raw)> GetManifestAsync(ChannelInfo channel, CancellationToken ct)
    {
        ThrowIfRefused();
        PathRules.ValidateRelative(channel.Manifest);
        using var response = await _http.GetAsync(channel.Manifest, ct);
        EnsureTransport(response);
        response.EnsureSuccessStatusCode();
        byte[] raw = await response.Content.ReadAsByteArrayAsync(ct);
        if (!string.Equals(Hashing.Sha256Hex(raw), channel.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new PatcherException("The release manifest is corrupt (hash mismatch). Try again in a moment.");
        }
        return (Manifest.FromCompressedBytes(raw), raw);
    }

    /// <summary>Downloads one stored chunk; <paramref name="onBytes"/> reports compressed bytes as they arrive.</summary>
    public async Task<byte[]> GetChunkAsync(string hash, int storedSize, Action<int>? onBytes, CancellationToken ct)
    {
        ThrowIfRefused();
        using var response = await _http.GetAsync(PathRules.ChunkRelative(hash), HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureTransport(response);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        byte[] buffer = new byte[storedSize];
        int total = 0;
        while (total < storedSize)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, Math.Min(64 * 1024, storedSize - total)), ct);
            if (read == 0)
            {
                break;
            }
            total += read;
            onBytes?.Invoke(read);
        }
        if (total != storedSize || await stream.ReadAsync(new byte[1], ct) != 0)
        {
            throw new IOException($"Chunk {hash[..12]} has the wrong size.");
        }
        return buffer;
    }

    public async Task<byte[]?> TryGetBytesAsync(string relative, CancellationToken ct)
    {
        if (_refused != null) return null;
        PathRules.ValidateRelative(relative);
        using var request = new HttpRequestMessage(HttpMethod.Get, relative);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await _http.SendAsync(request, ct);
        EnsureTransport(response);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Fetches a channel-authenticated asset and verifies its SHA-256 before returning bytes.</summary>
    public async Task<byte[]?> GetVerifiedBytesAsync(ChannelAsset asset, CancellationToken ct)
    {
        PathRules.ValidateRelative(asset.Url);
        if (!Manifest.IsSha256Hex(asset.Sha256))
            throw new PatcherException($"The signed asset '{asset.Url}' has an invalid SHA-256.");
        byte[]? data = await TryGetBytesAsync(asset.Url, ct);
        if (data is null) return null;
        if (!string.Equals(Hashing.Sha256Hex(data), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new PatcherException($"The signed asset '{asset.Url}' does not match its SHA-256.");
        return data;
    }

    async Task<byte[]?> GetMetadataAsync(string relative, bool required, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relative);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureTransport(response);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            if (required) throw new PatcherException($"The update repository is missing '{relative}'.");
            return null;
        }
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 0 and var length && length > _trust.MaxMetadataBytes)
        {
            throw new PatcherException($"The update metadata '{relative}' is too large.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        byte[] buffer = new byte[32 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (output.Length + read > _trust.MaxMetadataBytes)
            {
                throw new PatcherException($"The update metadata '{relative}' is too large.");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    void EnsureTransport(HttpResponseMessage response)
    {
        if (_trust.RequireHttps && response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            throw new PatcherException("The update request was redirected away from HTTPS.");
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}

/// <summary>An expected failure whose message can be shown to the player as is.</summary>
public sealed class PatcherException(string message, Exception? inner = null) : Exception(message, inner);
