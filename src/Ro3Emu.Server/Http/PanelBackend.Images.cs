using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The panel's pictures: PNG files the data export decoded from the operator's own client (DataExport IconExport, the
/// icons/ folder of the game data), never shipped with RO3Emu. Their index.json lists every picture with the hash of the
/// client file it came from; a page gets "/img/&lt;kind&gt;/&lt;file&gt;?v=&lt;hash&gt;" for a picture that is there, and an empty
/// address for one that is not, so it draws its letter instead of asking. The index is read again when it changes (an
/// export while the server runs).
/// </summary>
public sealed partial class PanelBackend
{
    private const string IconIndex = "index.json";

    private readonly object _imageGate = new();
    private Dictionary<string, string> _images = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _imagesStamp;

    public string? ImageDir => Directory.Exists(ImageFolder) ? ImageFolder : null;

    public string? BrandLogoFile
    {
        get
        {
            if (options.Value.Panel.Branding.LogoPath is not { Length: > 0 } logo) return null;
            string root = Path.GetFullPath(options.Value.DataDir), file = Path.GetFullPath(Path.Combine(root, logo));
            return file.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? file : null;
        }
    }

    private string ImageFolder => options.Value.Panel.ImageDir is { Length: > 0 } dir ? dir
        : Path.Combine(options.Value.GameDataDir ?? options.Value.DataDir, "icons");

    /// <summary>The address of a picture of <paramref name="kind"/> (items, monsters, classes, maps), or "".</summary>
    private string Image(string kind, string? file)
    {
        if (string.IsNullOrEmpty(file)) return "";
        return Images().TryGetValue(kind + "/" + file, out string? version) ? "/img/" + kind + "/" + file + "?v=" + version : "";
    }

    private string ItemIcon(ItemConfig item) => Image("items", item.Icon);

    /// <summary>An item's bag icon by id, for the admin tools' item search.</summary>
    internal string ItemIconOf(long item) => ItemConfig.Get(data, item) is { } row ? ItemIcon(row) : "";

    private string ClassIcon(long career) => JobConfig.Get(data, career) is { } job ? Image("classes", job.Icon) : "";

    private string MapImage(long map) => MapConfig.Get(data, map) is { BgImage: [var first, ..] } ? Image("maps", first) : "";

    /// <summary>Picture path to version (the first characters of its source hash), from index.json.</summary>
    private Dictionary<string, string> Images()
    {
        var index = Path.Combine(ImageFolder, IconIndex);
        DateTime stamp = File.Exists(index) ? File.GetLastWriteTimeUtc(index) : default;
        lock (_imageGate)
        {
            if (stamp == _imagesStamp) return _images;
            var images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (stamp != default)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(index));
                    foreach (var entry in doc.RootElement.EnumerateObject().Where(e => e.Value.ValueKind == JsonValueKind.Object))
                    {
                        string hash = entry.Value.TryGetProperty("Hash", out var h) ? h.GetString() ?? "" : "";
                        string atlas = entry.Value.TryGetProperty("AtlasHash", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()! : "";
                        string version = (hash.Length > 8 ? hash[..8] : hash) + (atlas.Length > 4 ? atlas[..4] : atlas);
                        images[entry.Name] = version.Length > 0 ? version : "1";
                    }
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                images.Clear(); // a half-written index: the next request reads it again
                stamp = default;
            }
            _images = images;
            _imagesStamp = stamp;
            return images;
        }
    }
}
