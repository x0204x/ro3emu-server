using System.Security.Cryptography;

namespace Ro3Emu.Panel;

/// <summary>The site's files, embedded in this assembly as "www/&lt;path&gt;" and read once.</summary>
internal sealed class PanelAssets
{
    private const string Prefix = "www/";

    public sealed record Asset(byte[] Body, string ContentType, string ETag);

    private readonly Dictionary<string, Asset> _byPath = new(StringComparer.Ordinal);

    public PanelAssets()
    {
        var assembly = typeof(PanelAssets).Assembly;
        foreach (string name in assembly.GetManifestResourceNames())
        {
            // RecursiveDir keeps the build machine's separator.
            string path = name.Replace('\\', '/');
            if (!path.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            byte[] body = copy.ToArray();
            string etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(body).AsSpan(0, 8)) + "\"";
            _byPath["/" + path[Prefix.Length..]] = new Asset(body, ContentType(path), etag);
        }
    }

    /// <summary>The file served at <paramref name="path"/> ("/app.css"), or null.</summary>
    public Asset? Find(string path) => _byPath.GetValueOrDefault(path);

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".woff2" => "font/woff2",
        ".json" => "application/json; charset=utf-8",
        _ => "text/plain; charset=utf-8",
    };
}
