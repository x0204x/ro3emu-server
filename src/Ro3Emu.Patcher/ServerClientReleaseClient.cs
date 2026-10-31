using System.Net.Http.Headers;
using System.Text.Json;
using Ro3Emu.ClientRelease;

namespace Ro3Emu.Patcher;

/// <summary>只使用登入器已設定的同一遊戲 API 來源；每次請求皆略過快取。</summary>
public sealed class ServerClientReleaseClient : IDisposable
{
    readonly HttpClient _http;
    readonly Uri _serverBaseUri;

    public ServerClientReleaseClient(Uri serverBaseUri, HttpMessageHandler? handler = null)
    {
        _serverBaseUri = serverBaseUri;
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<ClientReleaseRequirement> GetAsync(CancellationToken ct)
    {
        if (!_serverBaseUri.IsAbsoluteUri || _serverBaseUri.Scheme is not ("http" or "https")
            || _serverBaseUri.Query.Length != 0 || _serverBaseUri.Fragment.Length != 0)
            throw new PatcherException("正式機 API 網址無效，無法確認必要客戶端。");
        var endpoint = new Uri(_serverBaseUri.AbsoluteUri.TrimEnd('/') + ClientReleaseRequirement.Endpoint);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        CancellationToken token = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } actual
            && (actual.Scheme != endpoint.Scheme || actual.Authority != endpoint.Authority || actual.AbsolutePath != endpoint.AbsolutePath))
            throw new PatcherException("必要客戶端請求離開已設定的正式機 API，已停止啟動。");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            if (bytes.Length + read > 64 * 1024) throw new PatcherException("正式機必要客戶端宣告過大。");
            bytes.Write(buffer, 0, read);
        }
        ClientReleaseRequirement required = JsonSerializer.Deserialize<ClientReleaseRequirement>(bytes.ToArray(), Json.Compact)
            ?? throw new PatcherException("正式機未提供有效的必要客戶端宣告。");
        try { required.Validate(); }
        catch (InvalidDataException e) { throw new PatcherException(e.Message, e); }
        return required;
    }

    public void Dispose() => _http.Dispose();
}
