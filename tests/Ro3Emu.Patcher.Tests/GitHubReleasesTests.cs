using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Ro3Emu.Launcher;

namespace Ro3Emu.Patcher.Tests;

/// <summary>RO3Emu.exe installs only a download it can check: a release without a SHA-256 digest or an https address is refused unfetched.</summary>
public sealed class GitHubReleasesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-releases-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class Counting(byte[] body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private sealed class NoProgress : IProgress<(long Done, long Total)>
    {
        public void Report((long Done, long Total) value) { }
    }

    private const string Https = "https://github.com/aethai/ro3emu-server/releases/download/v1/RO3Emu-win-x64.zip";

    [Theory]
    [InlineData(Https, null)]
    [InlineData(Https, "")]
    [InlineData(Https, "abc123")]
    [InlineData("http://github.com/aethai/ro3emu-server/RO3Emu-win-x64.zip", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("ftp://github.com/RO3Emu-win-x64.zip", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("not a url", "0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task ADownloadItCannotCheckIsRefusedUnfetched(string url, string? digest)
    {
        var handler = new Counting([1, 2, 3]);
        using var http = new HttpClient(handler);
        var releases = new GitHubReleases(http, Path.Combine(_dir, "cache.json"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            releases.DownloadAsync(new GitHubReleases.Asset(url, 3, digest), _dir, new NoProgress(), CancellationToken.None));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task AWrongDigestIsRefusedAndTheRightOneInstalls()
    {
        byte[] body = [1, 2, 3];
        var handler = new Counting(body);
        using var http = new HttpClient(handler);
        var releases = new GitHubReleases(http, Path.Combine(_dir, "cache.json"));
        await Assert.ThrowsAsync<InvalidDataException>(() => releases.DownloadAsync(
            new GitHubReleases.Asset(Https, 3, new string('0', 64)), _dir, new NoProgress(), CancellationToken.None));
        string path = await releases.DownloadAsync(
            new GitHubReleases.Asset(Https, 3, Convert.ToHexStringLower(SHA256.HashData(body))), _dir, new NoProgress(), CancellationToken.None);
        Assert.Equal(body, await File.ReadAllBytesAsync(path));
    }
}
