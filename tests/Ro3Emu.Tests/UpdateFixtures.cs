using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using Ro3Emu.Server.Updates;

namespace Ro3Emu.Tests;

/// <summary>
/// Invented release packages for the update tests: a server "executable" that is a .NET single-file bundle in the
/// layout the SDK writes (an apphost stand-in, the manifest offset before the bundle signature, the manifest at the
/// end) holding a generated Ro3Emu.Server.dll with the version a test asks for, packed as the release packs it.
/// </summary>
internal static class UpdateFixtures
{
    /// <summary>An assembly named Ro3Emu.Server whose AssemblyInformationalVersion is <paramref name="version"/>.</summary>
    public static byte[] ServerAssembly(string version)
    {
        var assembly = new PersistedAssemblyBuilder(new AssemblyName("Ro3Emu.Server"), typeof(object).Assembly);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, [version]));
        assembly.DefineDynamicModule("Ro3Emu.Server").DefineType("Stub", TypeAttributes.Public).CreateType();
        using var output = new MemoryStream();
        assembly.Save(output);
        return output.ToArray();
    }

    /// <summary>A single-file bundle (manifest version 6) holding <paramref name="dll"/> as Ro3Emu.Server.dll.</summary>
    public static byte[] Bundle(byte[] dll)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write(Encoding.ASCII.GetBytes("MZ fake apphost ".PadRight(64, '.')));
        long offsetAt = output.Position;
        writer.Write(0L);
        writer.Write(BundleVersion.Signature);
        long dllAt = output.Position;
        writer.Write(dll);
        long manifest = output.Position;
        writer.Write(6u);
        writer.Write(0u);
        writer.Write(1);
        writer.Write("fake-bundle");
        for (int i = 0; i < 4; i++) writer.Write(0L);
        writer.Write(0UL);
        writer.Write(dllAt);
        writer.Write((long)dll.Length);
        writer.Write(0L); // not compressed
        writer.Write((byte)1); // assembly
        writer.Write("Ro3Emu.Server.dll");
        output.Position = offsetAt;
        writer.Write(manifest);
        writer.Flush();
        return output.ToArray();
    }

    /// <summary>The files of a release folder of <paramref name="platform"/> whose server says <paramref name="version"/>.</summary>
    public static Dictionary<string, byte[]> ReleaseFiles(UpdatePlatform platform, string version) => new(StringComparer.Ordinal)
    {
        [platform.ServerFile] = Bundle(ServerAssembly(version)),
        [platform.ToolFile] = Encoding.UTF8.GetBytes("tool " + version),
        ["server/appsettings.json"] = Encoding.UTF8.GetBytes("{ \"Ro3Emu\": { } }"),
        ["README.txt"] = Encoding.UTF8.GetBytes("readme " + version),
    };

    /// <summary>The release package of <paramref name="platform"/>: its folder name, then the files (a tarball keeps 0755 on executables).</summary>
    public static byte[] Package(UpdatePlatform platform, IReadOnlyDictionary<string, byte[]> files)
    {
        using var output = new MemoryStream();
        if (platform.IsTarball)
        {
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
                foreach (var (name, bytes) in files)
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, platform.Folder + "/" + name)
                    {
                        Mode = name == platform.ServerFile || name == platform.ToolFile ? (UnixFileMode)0b111_101_101 : (UnixFileMode)0b110_100_100,
                        DataStream = new MemoryStream(bytes),
                    });
        }
        else
        {
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var (name, bytes) in files)
            {
                using var entry = zip.CreateEntry(platform.Folder + "/" + name).Open();
                entry.Write(bytes);
            }
        }
        return output.ToArray();
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Writes a release folder's files under <paramref name="root"/>.</summary>
    public static void WriteFolder(string root, IReadOnlyDictionary<string, byte[]> files)
    {
        foreach (var (name, bytes) in files)
        {
            string path = Path.Combine(root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
    }

    /// <summary>A releases list as the GitHub API returns it.</summary>
    public static string ReleasesJson(params (string Tag, bool Draft, bool Prerelease, (string Name, string Url, long Size, string? Digest)[] Assets)[] releases) =>
        System.Text.Json.JsonSerializer.Serialize(releases.Select(r => new Dictionary<string, object?>
        {
            ["tag_name"] = r.Tag, ["name"] = "RO3Emu " + r.Tag, ["draft"] = r.Draft, ["prerelease"] = r.Prerelease,
            ["published_at"] = "2026-10-30T22:30:03Z", ["html_url"] = "https://github.com/aethai/ro3emu-server/releases/tag/" + r.Tag,
            ["body"] = "## Notes\n<script>alert(1)</script> fixed things",
            ["assets"] = r.Assets.Select(a => new Dictionary<string, object?>
            {
                ["name"] = a.Name, ["browser_download_url"] = a.Url, ["size"] = a.Size, ["digest"] = a.Digest,
            }).ToList(),
        }).ToList());

    public static string TempDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ro3emu-update-" + label + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}

/// <summary>An HTTP stack that answers from a function and counts the requests: no network.</summary>
internal sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(answer(request));
    }

    public static HttpResponseMessage Bytes(byte[] body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new ByteArrayContent(body) };

    public static HttpResponseMessage Json(string body, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag != null) response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return response;
    }
}
