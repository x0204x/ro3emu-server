using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ro3Emu.Launcher;

/// <summary>
/// RO3Emu's releases on GitHub (api.github.com, no token: 60 requests an hour per address, the app asks once per start):
/// the news column, "What's new" after an update, and the app's own update from RO3Emu-win-x64.zip.
/// </summary>
public sealed partial class GitHubReleases(HttpClient http, string cacheFile)
{
    public const string Repository = "aethai/ro3emu-server";
    public const string WindowsAsset = "RO3Emu-win-x64.zip";

    /// <summary>The releases list; RO3EMU_RELEASES_URL replaces it, to try the app update against a local copy.</summary>
    static string ReleasesUrl => Environment.GetEnvironmentVariable("RO3EMU_RELEASES_URL") is { Length: > 0 } url
        ? url : $"https://api.github.com/repos/{Repository}/releases?per_page=8";

    public sealed record Release(string Tag, string Name, DateTimeOffset Published, string Body, string Url, Asset? Windows)
    {
        /// <summary>v1.0.19 → 1.0.19; null for a tag that is not a version.</summary>
        public Version? Version => System.Version.TryParse(Tag.TrimStart('v', 'V'), out var v) ? v : null;
    }

    public sealed record Asset(string Url, long Size, string? Sha256);

    /// <summary>The newest releases (drafts and pre-releases left out), from GitHub, else from the last start's copy.</summary>
    public async Task<List<Release>> LoadAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
            request.Headers.UserAgent.ParseAdd("RO3Emu-Launcher");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct);
            var releases = Parse(json);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                await File.WriteAllTextAsync(cacheFile, json, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return releases;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            try { return File.Exists(cacheFile) ? Parse(await File.ReadAllTextAsync(cacheFile, ct)) : []; }
            catch (Exception inner) when (inner is IOException or JsonException) { return []; }
        }
    }

    internal static List<Release> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<Release>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (Bool(r, "draft") || Bool(r, "prerelease")) continue;
            string tag = Text(r, "tag_name");
            if (tag.Length == 0) continue;
            Asset? windows = null;
            if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                    if (Text(a, "name") == WindowsAsset && a.TryGetProperty("size", out var size) && size.TryGetInt64(out long bytes))
                    {
                        string digest = Text(a, "digest");
                        windows = new Asset(Text(a, "browser_download_url"), bytes,
                            digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null);
                    }
            string name = Text(r, "name");
            var published = DateTimeOffset.TryParse(Text(r, "published_at"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : default;
            list.Add(new Release(tag, name.Length > 0 ? name : tag, published, Text(r, "body"), Text(r, "html_url"), windows));
        }
        return list;
    }

    static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>The first paragraph of a release body as plain text (headings, links and emphasis removed).</summary>
    public static string Summary(string body)
    {
        foreach (string block in Regex.Split(body.Replace("\r\n", "\n"), @"\n\s*\n"))
        {
            var lines = block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("<!--", StringComparison.Ordinal)).ToList();
            if (lines.Count == 0) continue;
            // a list: its first point is the headline; a paragraph: all of it
            if (Bullet().IsMatch(lines[0])) lines = [.. lines.Take(1)];
            string text = Plain(string.Join(' ', lines.Select(l => Bullet().Replace(l, ""))));
            if (text.Length > 0) return text.Length > 400 ? text[..397].TrimEnd() + "..." : text;
        }
        return "";
    }

    /// <summary>The body's bullet points (or its paragraphs when it has none) for "What's new".</summary>
    public static List<string> Points(string body)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();
        var bullets = lines.Where(l => Bullet().IsMatch(l)).Select(l => Plain(Bullet().Replace(l, ""))).Where(l => l.Length > 0).ToList();
        if (bullets.Count > 0) return [.. bullets.Take(12)];
        return [.. Regex.Split(body.Replace("\r\n", "\n"), @"\n\s*\n").Select(b => Plain(string.Join(' ', b.Split('\n').Select(l => l.Trim()))))
            .Where(b => b.Length > 0 && !b.StartsWith('#')).Take(6)];
    }

    static string Plain(string text)
    {
        text = Regex.Replace(text, @"!\[[^\]]*\]\([^)]*\)", "");
        text = Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1");
        text = Regex.Replace(text, @"[*_`]+", "");
        text = Regex.Replace(text, @"^#+\s*", "");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    [GeneratedRegex(@"^([-*+]|\d+[.)])\s+")]
    private static partial Regex Bullet();

    /// <summary>
    /// Downloads the release's zip and checks it: the size GitHub lists and its SHA-256 digest. An asset without a digest
    /// or with a download address that is not https is refused before anything is fetched, so the launcher never
    /// installs files it cannot check. Returns the zip's path in <paramref name="downloadDir"/>.
    /// </summary>
    public async Task<string> DownloadAsync(Asset asset, string downloadDir, IProgress<(long Done, long Total)> progress, CancellationToken ct)
    {
        if (asset.Sha256 is not { Length: 64 } expected)
            throw new InvalidDataException("The release lists no SHA-256 checksum for its download, so it cannot be checked.");
        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var url) || !Ro3Emu.Patcher.RepositoryClient.Allowed(url))
            throw new InvalidDataException("The release's download address does not use https.");
        Directory.CreateDirectory(downloadDir);
        string path = Path.Combine(downloadDir, WindowsAsset);
        string partial = path + ".part";
        using (var request = new HttpRequestMessage(HttpMethod.Get, asset.Url))
        {
            request.Headers.UserAgent.ParseAdd("RO3Emu-Launcher");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(partial);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress.Report((done, asset.Size));
            }
        }
        long length = new FileInfo(partial).Length;
        if (length != asset.Size) throw new InvalidDataException($"The download has {length} bytes, the release lists {asset.Size}.");
        await using (var stream = File.OpenRead(partial))
        {
            string actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The download does not match the release's checksum.");
        }
        File.Move(partial, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Puts the zip's RO3Emu\ folder over <paramref name="appDir"/>: each file the release ships is swapped in, the one
    /// it replaces (even the running launcher: Windows lets a running exe be renamed) moved aside to *.old, which the
    /// next start deletes. Files the release does not ship (data, logs, settings) are not touched.
    /// </summary>
    public static void Apply(string zipPath, string appDir)
    {
        string staging = Path.Combine(appDir, ".update");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, staging);
        string root = Directory.Exists(Path.Combine(staging, "RO3Emu")) ? Path.Combine(staging, "RO3Emu") : staging;
        var moved = new List<(string Target, string Old)>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, file);
                string target = Path.GetFullPath(Path.Combine(appDir, relative));
                if (!target.StartsWith(Path.GetFullPath(appDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string old = target + ".old";
                if (File.Exists(target))
                {
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(target, old);
                    moved.Add((target, old));
                }
                File.Move(file, target);
            }
        }
        catch
        {
            // Put the old files back: a half applied update must not break a working install.
            foreach (var (target, old) in moved)
            {
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(old, target);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Deletes the *.old files a previous update left (best effort: one still in use stays until the next start).</summary>
    public static void DeleteLeftovers(string appDir)
    {
        foreach (string dir in new[] { appDir, Path.Combine(appDir, "server"), Path.Combine(appDir, "client") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string old in Directory.EnumerateFiles(dir, "*.old"))
            {
                try { File.Delete(old); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
