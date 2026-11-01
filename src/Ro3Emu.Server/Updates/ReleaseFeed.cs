using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ro3Emu.Server.Updates;

/// <param name="Sha256">The digest GitHub lists for the asset ("sha256:&lt;hex&gt;"), lower case hex; null when it lists none.</param>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

/// <param name="Notes">The release's body as written (Markdown), cut to <see cref="ReleaseFeed.NotesMax"/> characters.</param>
public sealed record ReleaseInfo(string Tag, ReleaseVersion Version, string Name, DateTimeOffset Published, string Notes, string Page,
    IReadOnlyList<ReleaseAsset> Assets);

/// <summary>What one request for the releases list brought.</summary>
/// <param name="Releases">The published releases, newest version first; null unless the list came (200).</param>
/// <param name="NotModified">GitHub answered 304 to the last list's ETag: the cached list is current.</param>
/// <param name="RetryAt">The rate limit is spent until then.</param>
/// <param name="Error">Why there is no list, in a sentence for the page.</param>
public sealed record FeedResponse(IReadOnlyList<ReleaseInfo>? Releases, string? ETag, bool NotModified, DateTimeOffset? RetryAt, string? Error);

/// <summary>
/// The releases list of the GitHub REST API (GET /repos/{owner}/{repo}/releases), asked without a token: 60 requests an
/// hour per address, so the caller asks rarely and sends the last answer's ETag (a 304 does not count against the
/// limit). Drafts, pre-releases and tags that are not versions are left out. Nothing here retries: a failure is
/// reported and the caller keeps its last list.
/// </summary>
public sealed partial class ReleaseFeed(HttpClient http, UpdateOptions options, TimeProvider clock)
{
    public const int NotesMax = 20_000;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,100}/[A-Za-z0-9_.\-]{1,100}$")]
    private static partial Regex RepositoryName();

    [GeneratedRegex(@"^sha256:([0-9a-fA-F]{64})$")]
    private static partial Regex Sha256Digest();

    /// <summary>The configured API address, or null when it is not https (plain http only on loopback, for a test feed).</summary>
    public Uri? ApiBase => Uri.TryCreate(options.ApiUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ? uri : null;

    public bool ValidRepository => RepositoryName().IsMatch(options.Repository);

    /// <summary>
    /// A download address the feed may hand out: https (GitHub's assets redirect to its own https hosts), or the test
    /// feed's own scheme, host and port.
    /// </summary>
    public bool AllowedDownload(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || ApiBase is { } api && Uri.Compare(uri, api, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0);

    public async Task<FeedResponse> FetchAsync(string? etag, string userAgent, CancellationToken ct)
    {
        if (ApiBase is not { } api) return Failed("Ro3Emu:Updates:ApiUrl is not an https address.");
        if (!ValidRepository) return Failed("Ro3Emu:Updates:Repository is not owner/name.");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(api, "repos/" + options.Repository + "/releases?per_page=30"));
        request.Headers.UserAgent.ParseAdd(userAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (etag != null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotModified) return new FeedResponse(null, etag, true, null, null);
            if (RetryAt(response) is { } retry)
                return new FeedResponse(null, null, false, retry, "GitHub's rate limit for this address is spent until "
                    + retry.ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture) + ".");
            if (!response.IsSuccessStatusCode)
                return Failed(string.Create(CultureInfo.InvariantCulture, $"GitHub answered HTTP {(int)response.StatusCode}."));
            string json = await response.Content.ReadAsStringAsync(timeout.Token);
            return new FeedResponse(Parse(json), response.Headers.ETag?.ToString(), false, null, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Failed("GitHub did not answer within 20 seconds."); }
        catch (HttpRequestException e) { return Failed("GitHub could not be reached (" + e.Message + ")."); }
        catch (JsonException) { return Failed("GitHub's answer was not a releases list."); }
    }

    private static FeedResponse Failed(string error) => new(null, null, false, null, error);

    /// <summary>
    /// A spent rate limit: 403 or 429 with x-ratelimit-remaining 0 (until x-ratelimit-reset) or with Retry-After
    /// (GitHub's secondary limits). Null for any other answer.
    /// </summary>
    private DateTimeOffset? RetryAt(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)) return null;
        if (response.Headers.RetryAfter is { } after)
            return after.Date ?? clock.GetUtcNow() + (after.Delta ?? TimeSpan.FromMinutes(1));
        if (Header(response, "x-ratelimit-remaining") == "0")
            return long.TryParse(Header(response, "x-ratelimit-reset"), NumberStyles.None, CultureInfo.InvariantCulture, out long reset)
                ? DateTimeOffset.FromUnixTimeSeconds(reset) : clock.GetUtcNow().AddHours(1);
        return response.StatusCode == HttpStatusCode.TooManyRequests ? clock.GetUtcNow().AddMinutes(1) : null;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>The published releases of a releases list, newest version first.</summary>
    public static List<ReleaseInfo> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("not an array");
        var list = new List<ReleaseInfo>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object || Bool(r, "draft") || Bool(r, "prerelease")) continue;
            string tag = Text(r, "tag_name");
            if (!ReleaseVersion.TryParse(tag, out var version)) continue;
            var published = DateTimeOffset.TryParse(Text(r, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at : default;
            string notes = Text(r, "body");
            string name = Text(r, "name");
            list.Add(new ReleaseInfo(tag, version, name.Length > 0 ? name : tag, published, notes.Length > NotesMax ? notes[..NotesMax] : notes,
                Text(r, "html_url"), Assets(r)));
        }
        return [.. list.OrderByDescending(r => r.Version)];
    }

    private static List<ReleaseAsset> Assets(JsonElement release)
    {
        var assets = new List<ReleaseAsset>();
        if (!release.TryGetProperty("assets", out var all) || all.ValueKind != JsonValueKind.Array) return assets;
        foreach (var a in all.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("size", out var size) || !size.TryGetInt64(out long bytes)) continue;
            string? sha = Sha256Digest().Match(Text(a, "digest")) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;
            assets.Add(new ReleaseAsset(Text(a, "name"), Text(a, "browser_download_url"), bytes, sha));
        }
        return assets;
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
