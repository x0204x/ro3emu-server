using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using Ro3Emu.Server;
using Ro3Emu.Server.Updates;

namespace Ro3Emu.Tests;

/// <summary>
/// The update manager's parts without a server: version order, the releases list and GitHub's rate limit, the package
/// per platform, the checked download, the unpacking and staging checks, the hosting modes and the file swap with its
/// rollback. Every HTTP answer is a fake and every release invented.
/// </summary>
public sealed class UpdateTests
{
    private static readonly UpdatePlatform Here = UpdatePlatform.Of(UpdatePlatform.Current()) ?? UpdatePlatform.Linux;
    /// <summary>The files that make a folder a release's, on both platforms.</summary>
    private static readonly string[] LayoutFiles = ["server/Ro3Emu.Server", "RO3Emu", "server/Ro3Emu.Server.exe", "RO3Emu.exe"];

    // ---------------------------------------------------------------- versions

    [Fact]
    public void VersionsCompareAsTheReleasesNumberThem()
    {
        Assert.True(ReleaseVersion.TryParse("v1.0.24", out var tag));
        Assert.True(ReleaseVersion.TryParse("1.0.24+7cf31d446d957c93", out var build));
        Assert.Equal(tag, build);
        Assert.Equal("1.0.24", build.ToString());
        Assert.True(V("1.0.10") > V("1.0.9"));
        Assert.True(V("2.0.0") > V("1.99.99"));
        Assert.True(V("1.1.0") > V("1.1.0-rc.1"));
        Assert.True(V("1.1.0-rc.2") > V("1.1.0-rc.1"));
        Assert.Equal(0, V("1.1.0").CompareTo(V("v1.1.0")));
        foreach (string bad in new[] { "", "1.0", "latest", "v1.0.x", "1.0.0.0", "1.0.0-", "../1.0.0" })
            Assert.False(ReleaseVersion.TryParse(bad, out _), bad);
    }

    private static ReleaseVersion V(string text) => ReleaseVersion.TryParse(text, out var v) ? v : throw new ArgumentException(text);

    // ---------------------------------------------------------------- the releases list

    private const string Sha = "d84b3518b56c2c5ec40a1b0c782c273ee91b7ba792cdd69bf2a55a9712da2d0a";

    private static string SampleReleases() => UpdateFixtures.ReleasesJson(
        ("v1.0.23", false, false, [("RO3Emu-win-x64.zip", "https://example.invalid/a.zip", 10, null)]),
        ("v1.0.25", false, false, [("RO3Emu-server-linux-x64.tar.gz", "https://example.invalid/l.tgz", 20, "sha256:" + Sha.ToUpperInvariant()),
            ("RO3Emu-win-x64.zip", "https://example.invalid/w.zip", 30, "md5:abc")]),
        ("v1.0.26", true, false, []), ("v1.1.0-rc.1", false, true, []), ("nightly", false, false, []));

    [Fact]
    public void TheListKeepsPublishedVersionsNewestFirstWithTheirDigests()
    {
        var releases = ReleaseFeed.Parse(SampleReleases());
        Assert.Equal(["v1.0.25", "v1.0.23"], releases.Select(r => r.Tag));
        var linux = UpdatePlatform.Linux.Pick(releases[0])!;
        Assert.Equal((20L, Sha), (linux.Size, linux.Sha256));
        Assert.Null(UpdatePlatform.Windows.Pick(releases[0])!.Sha256); // not a sha256 digest
        Assert.Null(UpdatePlatform.Windows.Pick(releases[1])!.Sha256);
        Assert.Null(UpdatePlatform.Linux.Pick(releases[1]));
        Assert.Contains("<script>", releases[0].Notes); // kept as text; the page never reads it as HTML
    }

    [Fact]
    public void TheAssetIsTheRunningPlatformsPackage()
    {
        Assert.Equal("RO3Emu-server-linux-x64.tar.gz", UpdatePlatform.Of("linux-x64")!.Asset);
        Assert.Equal("RO3Emu-win-x64.zip", UpdatePlatform.Of("win-x64")!.Asset);
        Assert.Null(UpdatePlatform.Of("linux-arm64"));
        Assert.Null(UpdatePlatform.Of("osx-arm64"));
        Assert.Equal(("server/Ro3Emu.Server.exe", "RO3Emu.exe", "RO3Emu"), (UpdatePlatform.Windows.ServerFile, UpdatePlatform.Windows.ToolFile, UpdatePlatform.Windows.Folder));
        Assert.Equal(("server/Ro3Emu.Server", "RO3Emu", "RO3Emu-server"), (UpdatePlatform.Linux.ServerFile, UpdatePlatform.Linux.ToolFile, UpdatePlatform.Linux.Folder));
    }

    [Fact]
    public async Task TheFeedSendsItsETagAndStopsAtTheRateLimit()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-10-31T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var answers = new Queue<HttpResponseMessage>();
        answers.Enqueue(FakeHttp.Json(SampleReleases(), "\"v1\""));
        answers.Enqueue(new HttpResponseMessage(HttpStatusCode.NotModified));
        var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
        limited.Headers.Add("x-ratelimit-remaining", "0");
        limited.Headers.Add("x-ratelimit-reset", clock.GetUtcNow().AddMinutes(40).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        answers.Enqueue(limited);
        answers.Enqueue(new HttpResponseMessage(HttpStatusCode.BadGateway));
        using var handler = new FakeHttp(_ => answers.Dequeue());
        using var http = new HttpClient(handler);
        var feed = new ReleaseFeed(http, new UpdateOptions { Repository = "aethai/ro3emu-server" }, clock);

        var first = await feed.FetchAsync(null, "test", default);
        Assert.Equal((2, "\"v1\""), (first.Releases!.Count, first.ETag));
        Assert.Equal("https://api.github.com/repos/aethai/ro3emu-server/releases?per_page=30", handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("test", handler.Requests[0].Headers.UserAgent.ToString());
        Assert.True((await feed.FetchAsync(first.ETag, "test", default)).NotModified);
        Assert.Equal("\"v1\"", handler.Requests[1].Headers.GetValues("If-None-Match").Single());
        var spent = await feed.FetchAsync(first.ETag, "test", default);
        Assert.Equal(clock.GetUtcNow().AddMinutes(40).ToUnixTimeSeconds(), spent.RetryAt!.Value.ToUnixTimeSeconds());
        Assert.Contains("rate limit", spent.Error);
        var failed = await feed.FetchAsync(null, "test", default);
        Assert.Equal((null, "GitHub answered HTTP 502."), (failed.Releases, failed.Error));

        // A bad setting asks nothing at all.
        var wrong = new ReleaseFeed(http, new UpdateOptions { Repository = "not a repository" }, clock);
        Assert.NotNull((await wrong.FetchAsync(null, "test", default)).Error);
        var plain = new ReleaseFeed(http, new UpdateOptions { ApiUrl = "http://example.com" }, clock);
        Assert.NotNull((await plain.FetchAsync(null, "test", default)).Error);
        Assert.Equal(4, handler.Requests.Count);
        Assert.True(plain.AllowedDownload("https://github.com/aethai/ro3emu-server/releases/download/v1.0.24/x.zip"));
        Assert.False(plain.AllowedDownload("http://github.com/x.zip"));
        Assert.True(new ReleaseFeed(http, new UpdateOptions { ApiUrl = "http://127.0.0.1:15880" }, clock).AllowedDownload("http://127.0.0.1:15880/dl/x"));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ---------------------------------------------------------------- download and staging

    [Fact]
    public async Task ADownloadMustMatchItsDigestAndHaveOne()
    {
        string dir = UpdateFixtures.TempDir("download");
        try
        {
            byte[] body = Encoding.UTF8.GetBytes("a release package");
            using var handler = new FakeHttp(_ => FakeHttp.Bytes(body));
            using var http = new HttpClient(handler);
            string target = Path.Combine(dir, "pkg.zip");
            var progress = new List<long>();
            var good = new ReleaseAsset("pkg.zip", "https://example.invalid/pkg.zip", body.Length, UpdateFixtures.Sha256(body));
            await UpdatePackage.DownloadAsync(http, good, target, "test", new Progress(progress), default);
            Assert.Equal(body, await File.ReadAllBytesAsync(target));
            Assert.Equal(body.Length, progress[^1]);
            File.Delete(target);

            var bad = good with { Sha256 = new string('0', 64) };
            var refused = await Assert.ThrowsAsync<UpdateException>(() => UpdatePackage.DownloadAsync(http, bad, target, "test", new Progress([]), default));
            Assert.Contains("SHA-256", refused.Message);
            Assert.Empty(Directory.GetFiles(dir));

            await Assert.ThrowsAsync<UpdateException>(() => UpdatePackage.DownloadAsync(http, good with { Size = body.Length + 1 }, target, "test", new Progress([]), default));
            Assert.Empty(Directory.GetFiles(dir));

            int asked = handler.Requests.Count;
            var unsigned = await Assert.ThrowsAsync<UpdateException>(() => UpdatePackage.DownloadAsync(http, good with { Sha256 = null }, target, "test", new Progress([]), default));
            Assert.Contains("no SHA-256", unsigned.Message);
            Assert.Equal(asked, handler.Requests.Count); // refused before asking
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class Progress(List<long> seen) : IProgress<long>
    {
        public void Report(long value) => seen.Add(value);
    }

    [Theory]
    [InlineData(UpdatePlatform.LinuxX64)]
    [InlineData(UpdatePlatform.WindowsX64)]
    public void APackageIsUnpackedAndItsServerVersionChecked(string name)
    {
        var platform = UpdatePlatform.Of(name)!;
        string dir = UpdateFixtures.TempDir("stage");
        try
        {
            string archive = Path.Combine(dir, platform.Asset);
            File.WriteAllBytes(archive, UpdateFixtures.Package(platform, UpdateFixtures.ReleaseFiles(platform, "9.9.9+abcdef0")));
            string root = UpdatePackage.Extract(archive, platform, Path.Combine(dir, "staging"));
            Assert.Equal(Path.Combine(dir, "staging", platform.Folder), root);
            Assert.Equal("9.9.9+abcdef0", UpdatePackage.Check(root, platform, V("v9.9.9")));
            Assert.Equal("9.9.9+abcdef0", BundleVersion.Read(Path.Combine(root, platform.ServerFile)));
            if (platform.IsTarball && !OperatingSystem.IsWindows())
                Assert.True(File.GetUnixFileMode(Path.Combine(root, platform.ServerFile)).HasFlag(UnixFileMode.UserExecute));

            var other = Assert.Throws<UpdateException>(() => UpdatePackage.Check(root, platform, V("v9.9.8")));
            Assert.Contains("9.9.9", other.Message);
            File.Delete(Path.Combine(root, platform.ToolFile));
            Assert.Throws<UpdateException>(() => UpdatePackage.Check(root, platform, V("v9.9.9")));
            File.WriteAllBytes(Path.Combine(root, platform.ServerFile), Encoding.UTF8.GetBytes("not a server"));
            File.WriteAllBytes(Path.Combine(root, platform.ToolFile), [1]);
            Assert.Throws<UpdateException>(() => UpdatePackage.Check(root, platform, V("v9.9.9")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AnEntryOutsideThePackageFolderOrALinkIsRefused()
    {
        string dir = UpdateFixtures.TempDir("evil");
        try
        {
            foreach (string name in new[] { "RO3Emu-server/../evil.sh", "elsewhere/server.sh", "/etc/passwd", "RO3Emu-server/a/../../b" })
            {
                string archive = Path.Combine(dir, "evil.tar.gz");
                File.WriteAllBytes(archive, Tar(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream([1]) }));
                Assert.Throws<UpdateException>(() => UpdatePackage.Extract(archive, UpdatePlatform.Linux, Path.Combine(dir, "staging")));
                Assert.False(File.Exists(Path.Combine(dir, "evil.sh")));
            }
            string link = Path.Combine(dir, "link.tar.gz");
            File.WriteAllBytes(link, Tar(new PaxTarEntry(TarEntryType.SymbolicLink, "RO3Emu-server/server/Ro3Emu.Server") { LinkName = "/bin/sh" }));
            Assert.Contains("not a plain file", Assert.Throws<UpdateException>(() => UpdatePackage.Extract(link, UpdatePlatform.Linux, Path.Combine(dir, "staging"))).Message);

            string zip = Path.Combine(dir, "evil.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntry("RO3Emu/../../evil.exe").Open().Dispose();
            Assert.Throws<UpdateException>(() => UpdatePackage.Extract(zip, UpdatePlatform.Windows, Path.Combine(dir, "staging")));
            Assert.Throws<UpdateException>(() => UpdatePackage.Extract(Path.Combine(dir, "evil.tar.gz"), UpdatePlatform.Windows, Path.Combine(dir, "staging")));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static byte[] Tar(TarEntry entry)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
            tar.WriteEntry(entry);
        return output.ToArray();
    }

    // ---------------------------------------------------------------- hosting modes

    [Fact]
    public void TheHostingModeIsFoundFromHowTheServerRuns()
    {
        string release = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ro3-release"));
        var files = LayoutFiles.Select(f => Path.GetFullPath(Path.Combine(release, f))).ToHashSet(StringComparer.Ordinal);
        UpdateHosting Detect(bool windows, Dictionary<string, string> env, bool interactive = true, string? exe = null, string hosting = "auto",
            bool docker = false, string? installDir = null) =>
            UpdateHosting.Detect(new UpdateOptions { Hosting = hosting, InstallDir = installDir },
                new UpdateHosting.Probe(windows, !windows, exe ?? Path.Combine(release, "server", windows ? "Ro3Emu.Server.exe" : "Ro3Emu.Server"), interactive,
                    name => env.GetValueOrDefault(name), path => files.Contains(Path.GetFullPath(path)) || docker && path == "/.dockerenv"));

        var systemd = Detect(false, new() { ["INVOCATION_ID"] = "abc" });
        Assert.Equal((UpdateHostingMode.Systemd, release, true), (systemd.Mode, systemd.InstallDir, systemd.CanInstall));
        Assert.Equal((UpdateHostingMode.Manual, false, true), (Detect(false, []).Mode, Detect(false, []).CanInstall, Detect(false, []).CanDownload));
        Assert.Equal((UpdateHostingMode.Docker, false), (Detect(false, new() { ["INVOCATION_ID"] = "abc" }, docker: true).Mode, Detect(false, [], docker: true).CanInstall));
        Assert.Equal(UpdateHostingMode.Docker, Detect(false, new() { ["DOTNET_RUNNING_IN_CONTAINER"] = "true" }).Mode);
        Assert.Equal((UpdateHostingMode.App, false), (Detect(true, new() { ["RO3EMU_STARTER"] = "RO3Emu" }).Mode, Detect(true, new() { ["RO3EMU_STARTER"] = "RO3Emu" }).CanInstall));
        Assert.Equal((UpdateHostingMode.Service, true), (Detect(true, [], interactive: false).Mode, Detect(true, [], interactive: false).CanInstall));
        Assert.Equal(UpdateHostingMode.Manual, Detect(true, []).Mode);
        Assert.Equal(UpdateHostingMode.Service, Detect(true, [], hosting: "service").Mode);

        // Outside a release folder: a source build, whatever the setting, and nothing to download.
        var source = Detect(false, new() { ["INVOCATION_ID"] = "abc" }, exe: Path.Combine(Path.GetTempPath(), "bin", "Release", "Ro3Emu.Server"), hosting: "systemd");
        Assert.Equal((UpdateHostingMode.Source, false, false), (source.Mode, source.CanInstall, source.CanDownload));
        var elsewhere = Detect(false, [], exe: "/usr/bin/x", installDir: release, hosting: "systemd");
        Assert.Equal((UpdateHostingMode.Systemd, release), (elsewhere.Mode, elsewhere.InstallDir));
        Assert.NotNull(Detect(false, [], installDir: Path.GetTempPath()).Problem);
    }

    // ---------------------------------------------------------------- the swap

    [Fact]
    public void AnInstallKeepsThePreviousVersionAndARollbackPutsItBack()
    {
        string dir = UpdateFixtures.TempDir("swap");
        try
        {
            string install = Path.Combine(dir, "install"), staged = Path.Combine(dir, "staged");
            var paths = new UpdatePaths(Path.Combine(dir, "data"));
            var old = UpdateFixtures.ReleaseFiles(Here, "1.0.0");
            old["server/appsettings.json"] = Encoding.UTF8.GetBytes("{ \"edited\": true }");
            UpdateFixtures.WriteFolder(install, old);
            var fresh = UpdateFixtures.ReleaseFiles(Here, "1.0.1");
            fresh["server/new-file.txt"] = [7];
            UpdateFixtures.WriteFolder(staged, fresh);

            var swap = UpdateInstaller.Swap(staged, install, paths, "1.0.0", "ro3emu-before-1.0.1.db");
            Assert.Equal(fresh[Here.ServerFile], File.ReadAllBytes(Path.Combine(install, Here.ServerFile)));
            Assert.Equal(old["server/appsettings.json"], File.ReadAllBytes(Path.Combine(install, "server/appsettings.json")));
            Assert.Equal(fresh["server/appsettings.json"], File.ReadAllBytes(Path.Combine(install, "server/appsettings.json.new")));
            Assert.Equal(["server/appsettings.json"], swap.Kept);
            Assert.True(File.Exists(Path.Combine(install, Here.ServerFile + ".old")));
            var previous = UpdateInstaller.ReadPrevious(paths)!;
            Assert.Equal(("1.0.0", "ro3emu-before-1.0.1.db"), (previous.Version, previous.Backup));
            Assert.Equal(["server/new-file.txt"], previous.Added);
            Assert.Equal(old[Here.ServerFile], File.ReadAllBytes(Path.Combine(paths.Previous, Here.ServerFile)));

            UpdateInstaller.DeleteLeftovers(install);
            Assert.Empty(Directory.GetFiles(install, "*.old", SearchOption.AllDirectories));

            UpdateInstaller.Restore(install, paths, "1.0.1");
            Assert.Equal(old[Here.ServerFile], File.ReadAllBytes(Path.Combine(install, Here.ServerFile)));
            Assert.Equal(old[Here.ToolFile], File.ReadAllBytes(Path.Combine(install, Here.ToolFile)));
            Assert.False(File.Exists(Path.Combine(install, "server/new-file.txt")));
            var now = UpdateInstaller.ReadPrevious(paths)!;
            Assert.Equal(("1.0.1", null), (now.Version, now.Backup));
            Assert.Equal(fresh[Here.ServerFile], File.ReadAllBytes(Path.Combine(paths.Previous, Here.ServerFile)));
            Assert.Equal([7], File.ReadAllBytes(Path.Combine(paths.Previous, "server/new-file.txt")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ASwapThatCannotReplaceAFilePutsEverythingBack()
    {
        string dir = UpdateFixtures.TempDir("swapfail");
        try
        {
            string install = Path.Combine(dir, "install"), staged = Path.Combine(dir, "staged");
            var paths = new UpdatePaths(Path.Combine(dir, "data"));
            var old = UpdateFixtures.ReleaseFiles(Here, "1.0.0");
            UpdateFixtures.WriteFolder(install, old);
            var fresh = UpdateFixtures.ReleaseFiles(Here, "1.0.1");
            fresh["server/zz-blocked"] = [1];
            UpdateFixtures.WriteFolder(staged, fresh);
            Directory.CreateDirectory(Path.Combine(install, "server", "zz-blocked")); // a folder where the release has a file

            var refused = Assert.Throws<UpdateException>(() => UpdateInstaller.Swap(staged, install, paths, "1.0.0", null));
            Assert.Contains("back as it was", refused.Message);
            foreach (var (name, bytes) in old) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(install, name)));
            foreach (var (name, bytes) in fresh) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(staged, name)));
            Assert.Empty(Directory.GetFiles(install, "*.old", SearchOption.AllDirectories));
            Assert.Throws<UpdateException>(() => UpdateInstaller.Restore(Path.Combine(dir, "nowhere"), new UpdatePaths(Path.Combine(dir, "empty")), "1.0.0"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ARollbackWithoutAReleaseFolderIsRefusedOffline()
    {
        var output = new StringWriter();
        Assert.Equal(1, UpdateCli.Rollback(new ServerOptions { DataDir = Path.Combine(Path.GetTempPath(), "ro3emu-none") }, output));
        Assert.Contains("not run from a release folder", output.ToString());
    }
}
