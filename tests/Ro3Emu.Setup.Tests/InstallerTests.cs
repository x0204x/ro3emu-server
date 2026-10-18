using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Ro3Emu.Setup.Install;

namespace Ro3Emu.Setup.Tests;

/// <summary>
/// The official-client installer against a fake publisher: a loopback HTTP origin serving a tiny release signed with a
/// key made for the test (the real publisher key is only used to verify the real manifest file).
/// </summary>
public sealed class InstallerTests : IAsyncLifetime
{
    private const string Remote = "https://update.example.test/";
    private readonly RSA _key = RSA.Create(3072);
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("ro3emu-install-");
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _requests = new();
    private WebApplication _app = null!;
    private string _origin = "";

    /// <summary>Per path: how a request is answered (null = normally).</summary>
    private readonly ConcurrentDictionary<string, Func<HttpContext, Task<bool>>> _faults = new();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.Run(async context =>
        {
            var path = context.Request.Path.Value!.TrimStart('/');
            _requests.Enqueue($"{path} {context.Request.Headers.Range}");
            if (_faults.TryGetValue(path, out var fault) && await fault(context)) return;
            if (!_files.TryGetValue(path, out var body)) { context.Response.StatusCode = 404; return; }
            await Serve(context, body);
        });
        await _app.StartAsync();
        _origin = _app.Urls.First().TrimEnd('/') + "/";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _key.Dispose();
        _dir.Delete(recursive: true);
    }

    private static async Task Serve(HttpContext context, byte[] body)
    {
        long from = 0;
        var range = context.Request.Headers.Range.ToString();
        if (range.StartsWith("bytes=") && long.TryParse(range[6..].TrimEnd('-'), out var start))
        {
            if (start >= body.Length) { context.Response.StatusCode = 416; return; }
            from = start;
            context.Response.StatusCode = 206;
            context.Response.Headers.ContentRange = $"bytes {from}-{body.Length - 1}/{body.Length}";
        }
        context.Response.ContentLength = body.Length - from;
        await context.Response.Body.WriteAsync(body.AsMemory((int)from));
    }

    private PublisherConfig Config(params string[] origins) => new()
    {
        Origins = origins.Length > 0 ? origins : [_origin + "a/", _origin + "b/"],
        AllowedOrigins = [Remote],
        FullManifest = "full/manifest.json",
        FullSignature = "full/manifest.sig",
        DeltaManifest = "delta/manifest.json",
        DeltaSignature = "delta/manifest.sig",
        KeyId = "test-key",
        PublicKeySpki = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()),
        PublicKeySpkiSha256 = Convert.ToHexStringLower(SHA256.HashData(_key.ExportSubjectPublicKeyInfo())),
    };

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] Zip(params (string Path, byte[] Data)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, data) in entries)
            {
                var entry = zip.CreateEntry(path);
                entry.LastWriteTime = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero); // the same bytes every time
                using var s = entry.Open();
                s.Write(data);
            }
        return memory.ToArray();
    }

    /// <summary>Publishes a release on both origins: packages of files, the full manifest and optionally a delta.</summary>
    private Dictionary<string, byte[]> Publish(Dictionary<string, (string Path, byte[] Data)[]> packages, string? deltaPackage = null, bool badSignature = false)
    {
        var content = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, data) in packages.Where(p => p.Key != deltaPackage).SelectMany(p => p.Value)) content[path] = data;
        object Release(bool delta) => new
        {
            release_id = "test-release", release_sequence = 7, expires_utc = "2099-01-01T00:00:00Z", entry_point = "ro3.exe", platform = "windows",
            target_build = new { build_id = "b", content_sha256 = "c" }, required_base_build = delta ? new { build_id = "a", content_sha256 = "d" } : null,
        };
        byte[] Manifest(IEnumerable<string> names, bool delta) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocol = ReleaseManifest.Protocol,
            schema_version = 2,
            release = Release(delta),
            signing = new { algorithm = ReleaseManifest.Algorithm, key_id = "test-key" },
            packages = names.Select(n => new { package_id = Sha(Zip(packages[n])), sha256 = Sha(Zip(packages[n])), byte_length = Zip(packages[n]).Length, immutable_url = $"{Remote}pkg/{Sha(Zip(packages[n]))}.zip" }),
            operations = names.SelectMany(n => packages[n].Select(f => new { action = "add", logical_path = f.Path, package_id = Sha(Zip(packages[n])), sha256 = Sha(f.Data), byte_length = f.Data.Length })),
            target_files = content.Select(f => new { logical_path = f.Key, sha256 = Sha(f.Value), byte_length = f.Value.Length }),
            totals = new { download_bytes = 1, expanded_bytes = 1, required_free_bytes = 1 },
        });
        void Put(string path, byte[] data) { _files["a/" + path] = data; _files["b/" + path] = data; }
        var full = Manifest(packages.Keys.Where(k => k != deltaPackage), delta: false);
        Put("full/manifest.json", full);
        var signature = _key.SignData(full, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (badSignature) signature[10] ^= 1;
        Put("full/manifest.sig", signature);
        if (deltaPackage != null)
        {
            var delta = Manifest([deltaPackage], delta: true);
            Put("delta/manifest.json", delta);
            Put("delta/manifest.sig", _key.SignData(delta, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        foreach (var (_, files) in packages) Put($"pkg/{Sha(Zip(files))}.zip", Zip(files));
        return content;
    }

    private ClientInstaller Installer(PublisherConfig? config = null) =>
        new(config ?? Config(), new HttpClient(), Path.Combine(_dir.FullName, "cache")) { Backoff = _ => TimeSpan.Zero };

    private static byte[] Bytes(string s, int repeat = 1) => Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(s, repeat)));

    private Dictionary<string, (string, byte[])[]> TwoPackages() => new()
    {
        ["p1"] = [("ro3.exe", Bytes("exe", 1000)), ("ro3_Data/Managed/Assembly-CSharp.dll", Bytes("dll", 500))],
        ["p2"] = [("ro3_Data/StreamingAssets/VFS/big.bytes", Bytes("vfs", 20000))],
    };

    private void AssertInstalled(string game, Dictionary<string, byte[]> content)
    {
        foreach (var (path, data) in content) Assert.Equal(data, File.ReadAllBytes(Path.Combine(game, path)));
        Assert.Empty(Directory.GetFiles(game, "*.ro3emu-new", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InstallsIntoAnEmptyFolderThenFindsNothingToDo()
    {
        var content = Publish(TwoPackages());
        var game = Path.Combine(_dir.FullName, "game");
        var installer = Installer();
        var manifests = await installer.FetchAsync();
        Assert.Equal(7, manifests.Full.Release.Sequence);
        var plan = installer.MakePlan(manifests, game);
        Assert.Equal((3, 2), (plan.Needed.Count, plan.Packages.Count));
        await installer.ApplyAsync(plan, game);
        AssertInstalled(game, content);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir.FullName, "cache"))); // packages are deleted once extracted

        var again = installer.MakePlan(await installer.FetchAsync(), game);
        Assert.Empty(again.Needed);
        Assert.Equal(3, again.Present);
    }

    [Fact]
    public async Task RefusesAManifestWithABadSignature()
    {
        Publish(TwoPackages(), badSignature: true);
        var e = await Assert.ThrowsAsync<InstallException>(() => Installer().FetchAsync());
        Assert.Contains("not signed by the publisher", e.Message);
    }

    [Fact]
    public async Task RefusesAManifestSignedByAnotherKey()
    {
        Publish(TwoPackages());
        var other = Config() with { PublicKeySpki = PublisherConfig.Genesis.PublicKeySpki, PublicKeySpkiSha256 = PublisherConfig.Genesis.PublicKeySpkiSha256 };
        await Assert.ThrowsAsync<InstallException>(() => Installer(other).FetchAsync());
    }

    [Fact]
    public async Task OnlyFetchesThePackageOfAChangedFile()
    {
        var content = Publish(TwoPackages());
        var game = Path.Combine(_dir.FullName, "game");
        var installer = Installer();
        await installer.ApplyAsync(installer.MakePlan(await installer.FetchAsync(), game), game);
        File.WriteAllText(Path.Combine(game, "ro3.exe"), "damaged");
        while (_requests.TryDequeue(out _)) { }

        var plan = installer.MakePlan(await installer.FetchAsync(), game);
        Assert.Equal("ro3.exe", Assert.Single(plan.Needed).File.Path);
        await installer.ApplyAsync(plan, game);
        AssertInstalled(game, content);
        Assert.Single(_requests, r => r.Contains("pkg/"));
    }

    [Fact]
    public async Task ResumesAnInterruptedDownloadAndMovesToTheNextOrigin()
    {
        var packages = TwoPackages();
        var content = Publish(packages);
        var big = Zip(packages["p2"]);
        var path = $"pkg/{Sha(big)}.zip";
        int served = 0;
        // Origin a: the first request ends halfway, later ones fail; origin b serves ranges.
        _faults["a/" + path] = async context =>
        {
            if (Interlocked.Increment(ref served) == 1)
            {
                int half = big.Length / 2;
                context.Response.StatusCode = StatusCodes.Status206PartialContent;
                context.Response.ContentLength = half;
                context.Response.Headers.ContentRange = $"bytes 0-{half - 1}/{big.Length}";
                await context.Response.Body.WriteAsync(big.AsMemory(0, half));
                return true;
            }
            context.Response.StatusCode = 503;
            return true;
        };
        var game = Path.Combine(_dir.FullName, "game");
        var installer = Installer();
        await installer.ApplyAsync(installer.MakePlan(await installer.FetchAsync(), game), game);
        AssertInstalled(game, content);
        Assert.Contains(_requests, r => r.StartsWith("b/" + path + " bytes=") && !r.EndsWith("bytes=0-"));
    }

    [Fact]
    public async Task DownloadsADamagedPackageAgain()
    {
        var content = Publish(TwoPackages());
        var small = Zip(TwoPackages()["p1"]);
        var damaged = small.ToArray();
        damaged[^5] ^= 0xFF;
        _files[$"a/pkg/{Sha(small)}.zip"] = damaged;
        var game = Path.Combine(_dir.FullName, "game");
        var installer = Installer();
        await installer.ApplyAsync(installer.MakePlan(await installer.FetchAsync(), game), game);
        AssertInstalled(game, content);
    }

    [Fact]
    public async Task PrefersTheSmallDeltaPackage()
    {
        var packages = TwoPackages();
        var newDll = Bytes("new dll", 10);
        // The full release carries the new DLL inside the big package; the delta has it alone.
        packages["p2"] = [.. packages["p2"], ("ro3_Data/Managed/HUD.dll", newDll)];
        packages["d"] = [("ro3_Data/Managed/HUD.dll", newDll)];
        Publish(packages, deltaPackage: "d");
        var game = Path.Combine(_dir.FullName, "game");
        var installer = Installer();
        var manifests = await installer.FetchAsync();
        Assert.NotNull(manifests.Delta);
        var plan = installer.MakePlan(manifests, game, path => path.EndsWith("HUD.dll"));
        Assert.Equal(Sha(Zip(packages["d"])), Assert.Single(plan.Packages).Id);
        await installer.ApplyAsync(plan, game);
        Assert.Equal(newDll, File.ReadAllBytes(Path.Combine(game, "ro3_Data/Managed/HUD.dll")));
    }

    [Fact]
    public async Task KeepsADllPatchedByRo3EmuAndRefreshesItsBackupOnUpdate()
    {
        var content = Publish(TwoPackages());
        var game = Path.Combine(_dir.FullName, "game");
        var installer = Installer();
        await installer.ApplyAsync(installer.MakePlan(await installer.FetchAsync(), game), game);
        // RO3Emu patched the DLL: the original sits in ro3emu-backup.
        var dll = Path.Combine(game, "ro3_Data/Managed/Assembly-CSharp.dll");
        Directory.CreateDirectory(Path.Combine(game, "ro3emu-backup"));
        File.Copy(dll, Path.Combine(game, "ro3emu-backup", "Assembly-CSharp.dll"));
        File.WriteAllText(dll, "patched");
        var plan = installer.MakePlan(await installer.FetchAsync(), game);
        Assert.Empty(plan.Needed);
        Assert.Equal(1, plan.KeptPatched);

        // A new release changes the DLL: the update writes the new original to Managed and to the backup.
        var packages = TwoPackages();
        packages["p1"] = [packages["p1"][0], ("ro3_Data/Managed/Assembly-CSharp.dll", Bytes("dll v2", 300))];
        content = Publish(packages);
        plan = installer.MakePlan(await installer.FetchAsync(), game);
        Assert.Equal("ro3_Data/Managed/Assembly-CSharp.dll", Assert.Single(plan.Needed).File.Path);
        await installer.ApplyAsync(plan, game);
        AssertInstalled(game, content);
        Assert.Equal(content["ro3_Data/Managed/Assembly-CSharp.dll"], File.ReadAllBytes(Path.Combine(game, "ro3emu-backup", "Assembly-CSharp.dll")));
    }

    [Fact]
    public async Task FallsBackToTheSecondOriginForTheManifest()
    {
        Publish(TwoPackages());
        var installer = Installer(Config(_origin + "missing/", _origin + "b/"));
        var manifests = await installer.FetchAsync();
        Assert.Equal(_origin + "b/", manifests.Origin);
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/x.dll")]
    [InlineData("a/../../b")]
    public void RejectsUnsafePaths(string path) => Assert.False(ReleaseManifest.SafePath(path));

    [Fact]
    public void RefusesPackageUrlsOutsideThePublishersOrigins()
    {
        var config = Config();
        Assert.Equal([_origin + "a/pkg/x.zip", _origin + "b/pkg/x.zip", Remote + "pkg/x.zip"], config.PackageUrls(Remote + "pkg/x.zip"));
        Assert.Throws<InstallException>(() => config.PackageUrls("https://elsewhere.example/pkg/x.zip"));
    }

    /// <summary>
    /// The publisher's real full manifest (16 Oct 2026, release 140061: full.manifest.json + full.manifest.sig in
    /// RO3_REAL_MANIFEST_DIR) verifies with the built-in Genesis key, and a complete client of that release
    /// (RO3_GENESIS_CLIENT, read only) needs nothing from it. Hashes the whole 24 GB client, so it is slow.
    /// </summary>
    [RealManifestFact, Trait("Category", "Slow")]
    public void RealGenesisManifestVerifiesAndAMatchingClientNeedsNothing()
    {
        var dir = Environment.GetEnvironmentVariable("RO3_REAL_MANIFEST_DIR")!;
        var client = Environment.GetEnvironmentVariable("RO3_GENESIS_CLIENT")!;
        var manifestPath = Path.Combine(dir, "full.manifest.json");

        var bytes = File.ReadAllBytes(manifestPath);
        var manifest = ReleaseManifest.Verify(bytes, File.ReadAllBytes(Path.Combine(dir, "full.manifest.sig")), PublisherConfig.Genesis,
            nowUtc: new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(140061, manifest.Release.Sequence);
        Assert.Equal(37813, manifest.TargetFiles.Count);
        Assert.Equal(12, manifest.Packages.Count);
        Assert.All(manifest.Packages, p => Assert.Single(PublisherConfig.Genesis.PackageUrls(p.Url), u => u == p.Url));

        // A flipped byte must fail.
        var tampered = bytes.ToArray();
        tampered[100] ^= 1;
        Assert.Throws<InstallException>(() => ReleaseManifest.Verify(tampered, File.ReadAllBytes(Path.Combine(dir, "full.manifest.sig")), PublisherConfig.Genesis));

        var installer = new ClientInstaller(PublisherConfig.Genesis, new HttpClient(), Path.Combine(_dir.FullName, "cache"));
        var plan = installer.MakePlan(new ClientInstaller.Manifests(manifest, null, "offline"), client);
        Assert.Empty(plan.Needed.Select(n => n.File.Path));
        Assert.Equal(manifest.TargetFiles.Count, plan.Present);
    }
}

/// <summary>Runs only where the real manifest and a matching client are given (RO3_REAL_MANIFEST_DIR, RO3_GENESIS_CLIENT).</summary>
public sealed class RealManifestFactAttribute : FactAttribute
{
    public RealManifestFactAttribute()
    {
        var dir = Environment.GetEnvironmentVariable("RO3_REAL_MANIFEST_DIR");
        var client = Environment.GetEnvironmentVariable("RO3_GENESIS_CLIENT");
        if (dir == null || client == null || !File.Exists(Path.Combine(dir, "full.manifest.json")) || !File.Exists(Path.Combine(client, "ro3.exe")))
            Skip = "Set RO3_REAL_MANIFEST_DIR (full.manifest.json + .sig) and RO3_GENESIS_CLIENT (a client of that release).";
    }
}
