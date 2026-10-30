using System.Globalization;
using AssetRipper.TextureDecoder.Bc;
using AssetRipper.TextureDecoder.Rgb.Formats;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Ro3Emu.DataExport;

/// <summary>An image as 8-bit RGBA, rows from the top.</summary>
public sealed record RgbaImage(int Width, int Height, byte[] Pixels);

/// <summary>
/// The client's UI images, read from its Unity bundles in the VFS. The game asks its loader for a sprite by file name
/// ("item_1001001.png", "headicon_monster_angeling.png"); the VFS index maps that name to the bundle holding it. Three
/// shapes occur in the PC client (Unity 2022.3):
/// - a bundle with one Texture2D of that name (item icons, map images): the whole texture;
/// - a Sprite of that name over a texture of the same bundle: the sprite's rectangle of it;
/// - a Sprite packed into a sprite atlas (its m_AtlasTags, a "&lt;tag&gt;.spriteatlasv2" bundle that depends on the
///   sprite bundle): the atlas's render data for the sprite's m_RenderDataKey names the atlas texture and the rectangle,
///   placed at its offset in the sprite's own size (the atlas trims transparent borders).
/// Textures are RGBA32/ARGB32/RGB24/Alpha8 or BC1/BC3/BC7 (DXT1/DXT5) on PC. Not thread safe: one instance per thread.
/// </summary>
public sealed class UnityImages(Vfs vfs) : IDisposable
{
    private readonly AssetsManager _manager = new();
    private readonly Dictionary<string, Atlas?> _atlases = new(StringComparer.OrdinalIgnoreCase);
    private Loaded? _last;

    /// <summary>The sprite atlas (its asset name) the last <see cref="Load"/> read, null when none: an image depends on
    /// it as much as on its own bundle.</summary>
    public string? LastAtlas { get; private set; }

    /// <summary>The image the game shows for <paramref name="fileName"/>, or null when the client has none.</summary>
    public RgbaImage? Load(string fileName)
    {
        LastAtlas = null;
        string name = Path.GetFileNameWithoutExtension(fileName);
        if (vfs.SourceFile(fileName) is not { } source || vfs.FileHash(fileName) is not { } hash) return null;
        var bundle = Open(source + "#" + hash.ToString("x", CultureInfo.InvariantCulture), () => vfs.Extract(fileName));
        if (bundle == null) return null;
        // A texture of that name is the image itself; a sprite of that name cuts it from a texture or an atlas.
        if (bundle.Find(AssetClassID.Texture2D, name, _manager) is { } texture) return Decode(bundle, texture);
        if (bundle.Find(AssetClassID.Sprite, name, _manager) is not { } sprite) return null;
        var field = _manager.GetBaseField(bundle.Assets, sprite);
        var spriteRect = Rect(field["m_Rect"]);
        var textureRef = field["m_RD.texture"];
        if (textureRef["m_PathID"].AsLong != 0 && textureRef["m_FileID"].AsInt == 0
            && bundle.Assets.file.GetAssetInfo(textureRef["m_PathID"].AsLong) is { } own)
            return Crop(Decode(bundle, own), Rect(field["m_RD.textureRect"]), spriteRect, Point(field["m_RD.textureRectOffset"]));
        var tags = field["m_AtlasTags.Array"].Children.Select(c => c.AsString).ToList();
        if (tags.Count == 0) return null;
        LastAtlas = tags[0] + ".spriteatlasv2";
        if (AtlasFor(LastAtlas) is not { } atlas || !atlas.Sprites.TryGetValue(RenderKey(field["m_RenderDataKey"]), out var packed)) return null;
        return Crop(atlas.Texture(packed.Texture), packed.Rect, spriteRect, packed.Offset);
    }

    /// <summary>The sprite atlas (asset name) a picture is packed into, without decoding anything; null when none.</summary>
    public string? AtlasOf(string fileName)
    {
        if (vfs.SourceFile(fileName) is not { } source || vfs.FileHash(fileName) is not { } hash) return null;
        string name = Path.GetFileNameWithoutExtension(fileName);
        if (Open(source + "#" + hash.ToString("x", CultureInfo.InvariantCulture), () => vfs.Extract(fileName)) is not { } bundle
            || bundle.Find(AssetClassID.Texture2D, name, _manager) != null || bundle.Find(AssetClassID.Sprite, name, _manager) is not { } sprite)
            return null;
        var field = _manager.GetBaseField(bundle.Assets, sprite);
        if (field["m_RD.texture.m_PathID"].AsLong != 0) return null;
        return field["m_AtlasTags.Array"].Children.Select(c => c.AsString).FirstOrDefault() is { Length: > 0 } tag ? tag + ".spriteatlasv2" : null;
    }

    public void Dispose()
    {
        _manager.UnloadAll(true);
        _atlases.Clear();
        _last = null;
    }

    // ---------------------------------------------------------------- bundles

    /// <summary>One loaded bundle; the last one stays open, as consecutive names often share it.</summary>
    private sealed class Loaded(BundleFileInstance bundle, AssetsFileInstance assets)
    {
        public BundleFileInstance Bundle { get; } = bundle;
        public AssetsFileInstance Assets { get; } = assets;
        public string Key { get; init; } = "";
        /// <summary>Decoded textures by path id: many sprites share one.</summary>
        public Dictionary<long, RgbaImage> Textures { get; } = [];
        private Dictionary<(AssetClassID, string), AssetFileInfo>? _byName;

        public AssetFileInfo? Find(AssetClassID type, string name, AssetsManager manager)
        {
            if (_byName == null)
            {
                _byName = [];
                foreach (var t in new[] { AssetClassID.Texture2D, AssetClassID.Sprite })
                    foreach (var info in Assets.file.GetAssetsOfType(t))
                        _byName.TryAdd((t, manager.GetBaseField(Assets, info)["m_Name"].AsString.ToLowerInvariant()), info);
            }
            return _byName.GetValueOrDefault((type, name.ToLowerInvariant()));
        }
    }

    private Loaded? Open(string key, Func<byte[]?> read)
    {
        if (_last?.Key == key) return _last;
        if (_last != null) Close(_last);
        _last = null;
        if (read() is not { } bytes) return null;
        var bundle = _manager.LoadBundleFile(new MemoryStream(bytes), key, true);
        int index = Enumerable.Range(0, bundle.file.BlockAndDirInfo.DirectoryInfos.Count).FirstOrDefault(bundle.file.IsAssetsFile, -1);
        if (index < 0)
        {
            _manager.UnloadBundleFile(bundle);
            return null;
        }
        return _last = new Loaded(bundle, _manager.LoadAssetsFileFromBundle(bundle, index, false)) { Key = key };
    }

    private void Close(Loaded loaded)
    {
        _manager.UnloadAssetsFile(loaded.Assets);
        _manager.UnloadBundleFile(loaded.Bundle);
    }

    /// <summary>A sprite atlas by its asset name, its textures decoded once; null when the client has none.</summary>
    private Atlas? AtlasFor(string name)
    {
        if (_atlases.TryGetValue(name, out var cached)) return cached;
        Atlas? atlas = null;
        if (vfs.SourceFile(name) is { } source && vfs.Extract(name) is { } bytes)
        {
            var bundle = _manager.LoadBundleFile(new MemoryStream(bytes), "atlas:" + source, true);
            int index = Enumerable.Range(0, bundle.file.BlockAndDirInfo.DirectoryInfos.Count).FirstOrDefault(bundle.file.IsAssetsFile, -1);
            if (index >= 0)
            {
                var loaded = new Loaded(bundle, _manager.LoadAssetsFileFromBundle(bundle, index, false)) { Key = "atlas:" + source };
                atlas = new Atlas(this, loaded);
            }
        }
        _atlases[name] = atlas;
        return atlas;
    }

    /// <summary>A sprite's m_RenderDataKey: the GUID's four words and the local id.</summary>
    private static (uint, uint, uint, uint, long) RenderKey(AssetTypeValueField key)
    {
        var guid = key["first"];
        return (guid["data[0]"].AsUInt, guid["data[1]"].AsUInt, guid["data[2]"].AsUInt, guid["data[3]"].AsUInt, key["second"].AsLong);
    }

    private readonly record struct Packed(long Texture, RectF Rect, (float X, float Y) Offset);

    /// <summary>A SpriteAtlas: where each packed sprite sits (m_RenderDataMap) and its textures.</summary>
    private sealed class Atlas
    {
        private readonly UnityImages _owner;
        public Loaded Bundle { get; }
        public Dictionary<(uint, uint, uint, uint, long), Packed> Sprites { get; } = [];

        public Atlas(UnityImages owner, Loaded bundle)
        {
            _owner = owner;
            Bundle = bundle;
            foreach (var info in bundle.Assets.file.GetAssetsOfType(AssetClassID.SpriteAtlas))
            {
                var field = owner._manager.GetBaseField(bundle.Assets, info);
                foreach (var pair in field["m_RenderDataMap.Array"].Children)
                {
                    var data = pair["second"];
                    if (data["texture.m_FileID"].AsInt != 0) continue;
                    Sprites.TryAdd(RenderKey(pair["first"]), new Packed(data["texture.m_PathID"].AsLong, Rect(data["textureRect"]),
                        Point(data["textureRectOffset"])));
                }
            }
        }

        public RgbaImage Texture(long pathId)
        {
            var info = Bundle.Assets.file.GetAssetInfo(pathId) ?? throw new InvalidDataException("The sprite atlas has no texture " + pathId);
            return _owner.Decode(Bundle, info);
        }
    }

    // ---------------------------------------------------------------- textures

    private RgbaImage Decode(Loaded bundle, AssetFileInfo info)
    {
        if (bundle.Textures.TryGetValue(info.PathId, out var cached)) return cached;
        var field = _manager.GetBaseField(bundle.Assets, info);
        int width = field["m_Width"].AsInt, height = field["m_Height"].AsInt, format = field["m_TextureFormat"].AsInt;
        byte[] raw = TextureBytes(bundle, field);
        return bundle.Textures[info.PathId] = new RgbaImage(width, height, FlipRows(ToRgba(raw, width, height, format), width, height));
    }

    /// <summary>The first mip level: inline image data, or the stream in the bundle's .resS file.</summary>
    private static byte[] TextureBytes(Loaded bundle, AssetTypeValueField texture)
    {
        var inline = texture["image data"];
        if (!inline.IsDummy && inline.AsByteArray is { Length: > 0 } bytes) return bytes;
        string path = texture["m_StreamData.path"].AsString;
        long offset = (long)texture["m_StreamData.offset"].AsULong, size = texture["m_StreamData.size"].AsUInt;
        string file = path[(path.LastIndexOf('/') + 1)..];
        int index = bundle.Bundle.file.GetFileIndex(file);
        if (index < 0 || size <= 0) throw new InvalidDataException("A texture's data is missing from its bundle: " + path);
        bundle.Bundle.file.GetFileRange(index, out long start, out long length);
        if (offset + size > length) throw new InvalidDataException("A texture's data runs past its bundle file: " + path);
        var reader = bundle.Bundle.file.DataReader;
        reader.Position = start + offset;
        return reader.ReadBytes((int)size);
    }

    /// <summary>Unity's TextureFormat ids the PC client uses, to RGBA (still bottom-up, as Unity stores rows).</summary>
    internal static byte[] ToRgba(byte[] raw, int width, int height, int format)
    {
        int pixels = width * height;
        byte[] rgba;
        switch (format)
        {
            case 4: // RGBA32
                rgba = raw.AsSpan(0, pixels * 4).ToArray();
                break;
            case 5: // ARGB32
                rgba = new byte[pixels * 4];
                for (int i = 0; i < pixels; i++)
                {
                    rgba[i * 4] = raw[i * 4 + 1]; rgba[i * 4 + 1] = raw[i * 4 + 2]; rgba[i * 4 + 2] = raw[i * 4 + 3]; rgba[i * 4 + 3] = raw[i * 4];
                }
                break;
            case 14: // BGRA32
                rgba = new byte[pixels * 4];
                for (int i = 0; i < pixels; i++)
                {
                    rgba[i * 4] = raw[i * 4 + 2]; rgba[i * 4 + 1] = raw[i * 4 + 1]; rgba[i * 4 + 2] = raw[i * 4]; rgba[i * 4 + 3] = raw[i * 4 + 3];
                }
                break;
            case 3: // RGB24
                rgba = new byte[pixels * 4];
                for (int i = 0; i < pixels; i++)
                {
                    rgba[i * 4] = raw[i * 3]; rgba[i * 4 + 1] = raw[i * 3 + 1]; rgba[i * 4 + 2] = raw[i * 3 + 2]; rgba[i * 4 + 3] = 255;
                }
                break;
            case 1: // Alpha8
                rgba = new byte[pixels * 4];
                for (int i = 0; i < pixels; i++)
                {
                    rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 255; rgba[i * 4 + 3] = raw[i];
                }
                break;
            case 10: Bc1.Decompress<ColorRGBA<byte>, byte>(raw, width, height, out rgba); break; // DXT1
            case 12: Bc3.Decompress<ColorRGBA<byte>, byte>(raw, width, height, out rgba); break; // DXT5
            case 25: Bc7.Decompress<ColorRGBA<byte>, byte>(raw, width, height, out rgba); break; // BC7
            default: throw new NotSupportedException("Texture format " + format + " is not one the PC client uses.");
        }
        return rgba;
    }

    private static byte[] FlipRows(byte[] rgba, int width, int height)
    {
        var flipped = new byte[width * height * 4];
        int stride = width * 4;
        for (int y = 0; y < height; y++) rgba.AsSpan((height - 1 - y) * stride, stride).CopyTo(flipped.AsSpan(y * stride));
        return flipped;
    }

    // ---------------------------------------------------------------- sprite rectangles

    private readonly record struct RectF(float X, float Y, float Width, float Height);

    private static RectF Rect(AssetTypeValueField rect) => new(rect["x"].AsFloat, rect["y"].AsFloat, rect["width"].AsFloat, rect["height"].AsFloat);

    private static (float X, float Y) Point(AssetTypeValueField point) => (point["x"].AsFloat, point["y"].AsFloat);

    /// <summary>
    /// The sprite in its own size (<paramref name="sprite"/>): the texture's <paramref name="source"/> rectangle placed
    /// at <paramref name="offset"/>. Unity's rectangles count from the bottom; the images here are top-down.
    /// </summary>
    private static RgbaImage Crop(RgbaImage texture, RectF source, RectF sprite, (float X, float Y) offset)
    {
        int width = Math.Max(1, (int)MathF.Round(sprite.Width)), height = Math.Max(1, (int)MathF.Round(sprite.Height));
        var pixels = new byte[width * height * 4];
        int x0 = (int)MathF.Round(source.X), y0 = (int)MathF.Round(source.Y);
        int w = (int)MathF.Round(source.Width), h = (int)MathF.Round(source.Height);
        int ox = (int)MathF.Round(offset.X), oy = (int)MathF.Round(offset.Y);
        for (int y = 0; y < h; y++)
        {
            int sy = y0 + y, dy = oy + y; // from the bottom
            if (sy < 0 || sy >= texture.Height || dy < 0 || dy >= height) continue;
            for (int x = 0; x < w; x++)
            {
                int sx = x0 + x, dx = ox + x;
                if (sx < 0 || sx >= texture.Width || dx < 0 || dx >= width) continue;
                int from = ((texture.Height - 1 - sy) * texture.Width + sx) * 4, to = ((height - 1 - dy) * width + dx) * 4;
                texture.Pixels.AsSpan(from, 4).CopyTo(pixels.AsSpan(to));
            }
        }
        return new RgbaImage(width, height, pixels);
    }
}
