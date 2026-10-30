using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Ro3Emu.Setup.Install;

/// <summary>
/// Installs or updates the official client from the publisher's update service, the way its launcher does: fetch the
/// signed manifests, compare every target file with the disk (size, then SHA-256), download only the packages that hold
/// what is missing (preferring the small delta package when it has the file), with resume and retries, check each
/// package's SHA-256, extract only the needed entries and check every written file's SHA-256. Packages are deleted
/// once extracted. Nothing of ours is written into the game folder; the one exception is the backup of DLLs RO3Emu
/// patched before (ro3emu-backup), which an update refreshes with the new originals.
/// </summary>
public sealed class ClientInstaller(PublisherConfig config, HttpClient http, string cacheDir)
{
    /// <summary>(stage, done, total) in bytes, for the progress line.</summary>
    public Action<string, long, long>? Progress { get; set; }

    public Action<string>? Log { get; set; }

    public int MaxAttempts { get; set; } = 8;

    public Func<int, TimeSpan> Backoff { get; set; } = attempt => TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(attempt, 5)));

    public sealed record Manifests(ReleaseManifest Full, ReleaseManifest? Delta, string Origin);

    /// <summary>A target file and the package to take it from.</summary>
    public sealed record Need(ReleaseManifest.TargetFile File, ReleaseManifest.Package Package);

    public sealed record Plan(IReadOnlyList<Need> Needed, int Present, int KeptPatched, long NeededBytes,
        IReadOnlyList<ReleaseManifest.Package> Packages)
    {
        public long DownloadBytes => Packages.Sum(p => p.Length);
        public long LargestPackage => Packages.Count == 0 ? 0 : Packages.Max(p => p.Length);
    }

    /// <summary>The full manifest (and the delta one when it verifies) from the first origin that serves a valid one.</summary>
    public async Task<Manifests> FetchAsync(CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var origin in config.Origins)
        {
            try
            {
                var full = await GetBytesAsync(origin + config.FullManifest, ct);
                var sig = await GetBytesAsync(origin + config.FullSignature, ct);
                var manifest = ReleaseManifest.Verify(full, sig, config);
                Log?.Invoke($"manifest from {origin}: {manifest.Release.Id} sequence {manifest.Release.Sequence}, {manifest.TargetFiles.Count} files");
                ReleaseManifest? delta = null;
                if (config.DeltaManifest is { Length: > 0 } dm && config.DeltaSignature is { Length: > 0 } ds)
                {
                    try
                    {
                        delta = ReleaseManifest.Verify(await GetBytesAsync(origin + dm, ct), await GetBytesAsync(origin + ds, ct), config);
                        if (delta.Release.TargetBuild.ContentSha256 != manifest.Release.TargetBuild.ContentSha256) delta = null;
                    }
                    catch (Exception e) when (e is HttpRequestException or InstallException or TaskCanceledException)
                    {
                        Log?.Invoke($"delta manifest from {origin} skipped: {e.Message}");
                    }
                }
                return new Manifests(manifest, delta, origin);
            }
            catch (InstallException e) { last = e; Log?.Invoke($"{origin}: {e.Message}"); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                last = e;
                Log?.Invoke($"{origin}: {e.Message}");
            }
        }
        if (last is InstallException install) throw install;
        throw new InstallException("Genesis' download servers could not be reached.",
            "Check the internet connection and try again. Some internet providers block Genesis' mirror: connecting through "
            + "Cloudflare WARP (or another VPN) helps, or install the game with the Genesis launcher and pick its folder instead. "
            + "If the servers moved, a newer RO3Emu (or RO3Emu.download.json) has the new addresses.");
    }

    private async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>
    /// What the folder lacks for the release. A DLL RO3Emu patched counts as present when its original in
    /// ro3emu-backup matches. <paramref name="include"/> limits the release to some files (null = all).
    /// </summary>
    public Plan MakePlan(Manifests manifests, string folder, Func<string, bool>? include = null)
    {
        var targets = manifests.Full.TargetFiles.Where(t => include == null || include(t.Path)).ToList();
        long toHash = targets.Sum(t => t.Length);
        long hashed = 0;
        var missing = new List<ReleaseManifest.TargetFile>();
        int keptPatched = 0;
        var gate = new object();
        var watch = Stopwatch.StartNew();
        Parallel.ForEach(targets, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 6) }, t =>
        {
            var state = FileState(folder, t);
            lock (gate)
            {
                if (state == Present.No) missing.Add(t);
                else if (state == Present.Patched) keptPatched++;
                hashed += t.Length;
                if (watch.ElapsedMilliseconds > 250) { watch.Restart(); Progress?.Invoke("Checking the game files", hashed, toHash); }
            }
        });
        Progress?.Invoke("Checking the game files", toHash, toHash);

        // Every operation that yields a missing file's exact content is a source; the full manifest has one for each.
        var packages = manifests.Full.Packages.Concat(manifests.Delta?.Packages ?? []).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var sources = manifests.Full.Operations.Concat(manifests.Delta?.Operations ?? [])
            .Where(o => o.Action is "add" or "replace" && packages.ContainsKey(o.PackageId))
            .GroupBy(o => (o.Path, o.Sha256))
            .ToDictionary(g => g.Key, g => g.Select(o => packages[o.PackageId]).DistinctBy(p => p.Id).OrderBy(p => p.Length).ToList());
        var chosen = new Dictionary<string, ReleaseManifest.Package>();
        var needs = new List<Need>();
        // Files with one source first, so the others can join a package that is downloaded anyway.
        foreach (var t in missing.OrderBy(t => sources.TryGetValue((t.Path, t.Sha256), out var s) ? s.Count : 0).ThenBy(t => t.Path, StringComparer.Ordinal))
        {
            if (!sources.TryGetValue((t.Path, t.Sha256), out var candidates) || candidates.Count == 0)
                throw new InstallException($"Genesis' release has no package for {t.Path}.", "Try again later.");
            var package = candidates.FirstOrDefault(c => chosen.ContainsKey(c.Id)) ?? candidates[0];
            chosen.TryAdd(package.Id, package);
            needs.Add(new Need(t, package));
        }
        var order = manifests.Full.Packages.Concat(manifests.Delta?.Packages ?? []).Select(p => p.Id).Distinct().ToList();
        return new Plan(needs, targets.Count - missing.Count, keptPatched, missing.Sum(t => t.Length),
            chosen.Values.OrderBy(p => order.IndexOf(p.Id)).ToList());
    }

    private enum Present { No, Yes, Patched }

    private static Present FileState(string folder, ReleaseManifest.TargetFile t)
    {
        var path = Path.Combine(folder, t.Path);
        if (Matches(path, t)) return Present.Yes;
        // ro3_Data/Managed/X.dll patched by RO3Emu: its original lives in ro3emu-backup/X.dll.
        if (t.Path.StartsWith("ro3_Data/Managed/", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
            && Matches(Path.Combine(folder, "ro3emu-backup", Path.GetFileName(t.Path)), t))
            return Present.Patched;
        return Present.No;
    }

    private static bool Matches(string path, ReleaseManifest.TargetFile t)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != t.Length) return false;
        return string.Equals(Sha256(path), t.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public static string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Downloads, checks and extracts what the plan needs, one package at a time.</summary>
    public async Task ApplyAsync(Plan plan, string folder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(cacheDir);
        long total = plan.DownloadBytes, before = 0;
        foreach (var package in plan.Packages)
        {
            var files = plan.Needed.Where(n => n.Package.Id == package.Id).Select(n => n.File).ToList();
            long offset = before;
            var zip = await DownloadAsync(package, (done, _) => Progress?.Invoke("Downloading", offset + done, total), ct);
            before += package.Length;
            Extract(zip, files, folder);
            File.Delete(zip);
            Log?.Invoke($"package {package.Id[..12]}: {files.Count} files written");
        }
    }

    /// <summary>One package into the cache, resuming a partial download, retrying across origins, SHA-256 checked.</summary>
    public async Task<string> DownloadAsync(ReleaseManifest.Package package, Action<long, long>? progress = null, CancellationToken ct = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(package.Id, "^[0-9a-f]{64}$"))
            throw new InstallException($"The manifest has a malformed package id ({package.Id}).", "Do not use this download source.");
        Directory.CreateDirectory(cacheDir);
        var final = Path.Combine(cacheDir, package.Id + ".zip");
        var part = final + ".part";
        if (File.Exists(final))
        {
            if (new FileInfo(final).Length == package.Length && Sha256(final) == package.Sha256.ToLowerInvariant()) return final;
            File.Delete(final);
        }
        var urls = config.PackageUrls(package.Url);
        Exception? last = null;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var url = urls[attempt % urls.Count];
            try
            {
                long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                if (have > package.Length) { File.Delete(part); have = 0; }
                if (have < package.Length) await FetchRangeAsync(url, part, have, package.Length, progress, ct);
                progress?.Invoke(package.Length, package.Length);
                Progress?.Invoke("Checking the download", 0, 0);
                if (Sha256(part) == package.Sha256.ToLowerInvariant())
                {
                    File.Move(part, final, overwrite: true);
                    return final;
                }
                Log?.Invoke($"package {package.Id[..12]} from {url}: wrong SHA-256, downloading it again");
                File.Delete(part);
                last = new InstallException("A downloaded package was damaged.", "Try again.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or InvalidDataException)
            {
                last = e;
                Log?.Invoke($"package {package.Id[..12]} from {url}: {e.Message} (attempt {attempt + 1})");
            }
            await Task.Delay(Backoff(attempt), ct);
        }
        throw new InstallException($"A game package could not be downloaded ({last?.Message}).",
            "Check the internet connection and run the same command again: the download resumes where it stopped.");
    }

    private async Task FetchRangeAsync(string url, string part, long have, long length, Action<long, long>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(part); throw new InvalidDataException("range not satisfiable"); }
        response.EnsureSuccessStatusCode();
        bool resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (have > 0 && !resumed) have = 0; // the server sent the whole file
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var buffer = new byte[1 << 20];
        long done = have;
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            if (done + read > length) throw new InvalidDataException("the server sent more than the package size");
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            progress?.Invoke(done, length);
        }
        if (done != length) throw new IOException($"the download stopped at {done} of {length} bytes");
    }

    /// <summary>Writes the files from the package, each checked against its SHA-256 before it replaces anything.</summary>
    public static void Extract(string zipPath, IEnumerable<ReleaseManifest.TargetFile> files, string folder)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var e in zip.Entries) entries.TryAdd(e.FullName.Replace('\\', '/'), e);
        foreach (var f in files)
        {
            if (!ReleaseManifest.SafePath(f.Path)) throw new InstallException($"Unsafe path {f.Path}.", "Do not use this download source.");
            if (!entries.TryGetValue(f.Path, out var entry)) throw new InstallException($"The package lacks {f.Path}.", "Try again later.");
            var target = Path.GetFullPath(Path.Combine(folder, f.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temp = target + ".ro3emu-new";
            using (var input = entry.Open())
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 20];
                long length = 0;
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                    length += read;
                }
                if (length != f.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != f.Sha256.ToLowerInvariant())
                {
                    output.Dispose();
                    File.Delete(temp);
                    throw new InstallException($"{f.Path} in the package does not match the manifest.", "Try again later.");
                }
            }
            File.Move(temp, target, overwrite: true);
            // A DLL RO3Emu patched earlier: the new original is also the new backup, so the next patch starts from it.
            if (f.Path.StartsWith("ro3_Data/Managed/", StringComparison.OrdinalIgnoreCase))
            {
                var backup = Path.Combine(folder, "ro3emu-backup", Path.GetFileName(f.Path));
                if (File.Exists(backup)) File.Copy(target, backup, overwrite: true);
            }
        }
    }
}
