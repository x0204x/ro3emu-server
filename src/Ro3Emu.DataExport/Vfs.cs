using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Ro3Emu.DataExport;

/// <summary>
/// Read-only access to the client's asset VFS (ro3_Data/StreamingAssets/VFS): assetmeta.data maps
/// the MurmurHash3 x64 of a lower-cased asset name to either a loose bundle (VFS/&lt;2 hex&gt;/&lt;hash&gt;.bundle) or an
/// entry of a packed "RO3V" container (VFS/&lt;hash ^ dyn&gt;.bytes). A port of tools/WorldData/vfsget.js.
/// </summary>
public sealed class Vfs : IDisposable
{
    public readonly record struct Entry(ulong Asset, byte LoadType, ushort Dyn, ulong FileHash, ulong VfsHash);

    private readonly string _dir;
    private readonly Dictionary<ulong, List<Entry>> _bundles = new();
    private readonly Dictionary<ulong, List<Entry>> _bytes = new();
    private readonly Dictionary<string, Container> _containers = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Extract may run on several threads (the icon export): the container cache and FilesRead are shared.</summary>
    private readonly Lock _gate = new();

    /// <summary>The container and bundle files read so far (to copy only what an export needs).</summary>
    public HashSet<string> FilesRead { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Vfs(string vfsDir)
    {
        _dir = vfsDir;
        var meta = Path.Combine(vfsDir, "assetmeta.data");
        FilesRead.Add(meta);
        var b = File.ReadAllBytes(meta);
        int o = 0;
        void Read(Dictionary<ulong, List<Entry>> map)
        {
            uint n = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
            o += 4;
            for (uint i = 0; i < n; i++, o += 27)
            {
                var s = b.AsSpan(o);
                var e = new Entry(BinaryPrimitives.ReadUInt64LittleEndian(s), s[8], BinaryPrimitives.ReadUInt16LittleEndian(s[9..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(s[11..]), BinaryPrimitives.ReadUInt64LittleEndian(s[19..]));
                if (!map.TryGetValue(e.Asset, out var list)) map[e.Asset] = list = new List<Entry>(1);
                list.Add(e);
            }
        }
        Read(_bundles);
        Read(_bytes);
    }

    /// <summary>The asset's bytes, or null when the VFS has no asset of that name.</summary>
    public byte[]? Extract(string name)
    {
        if (Locate(name) is not var (e, bundle)) return null;
        if (e.FileHash == e.VfsHash)
        {
            // vfsget.js: a two-digit folder from the zero-padded hash, the file name from the unpadded one.
            var file = Path.Combine(_dir, LooseFile(e));
            lock (_gate) FilesRead.Add(file);
            return File.ReadAllBytes(file);
        }
        var container = ContainerFor(Path.Combine(_dir, ContainerFile(e, bundle)));
        if (!container.Index.TryGetValue(e.FileHash, out var at)) return null;
        var data = new byte[at.Length];
        RandomAccess.Read(container.Handle, data, at.Offset);
        return data;
    }

    /// <summary>The VFS file (bundle or container) that holds the asset, relative to the VFS folder, or null.</summary>
    public string? SourceFile(string name) =>
        Locate(name) is var (e, bundle) ? e.FileHash == e.VfsHash ? LooseFile(e) : ContainerFile(e, bundle) : null;

    /// <summary>The asset's own file hash (changes with its content), or null when the VFS has no asset of that name.</summary>
    public ulong? FileHash(string name) => Locate(name)?.Entry.FileHash;

    /// <summary>A "bytes" asset first, as the game's loader asks for one, else a Unity bundle.</summary>
    private (Entry Entry, bool Bundle)? Locate(string name)
    {
        ulong h = LowerHash(name);
        if (_bytes.TryGetValue(h, out var list)) return (list[0], false);
        if (_bundles.TryGetValue(h, out list)) return (list[0], true);
        return null;
    }

    private static string LooseFile(Entry e) => e.FileHash.ToString("x16", CultureInfo.InvariantCulture)[..2] + "/" + e.FileHash.ToString("x", CultureInfo.InvariantCulture) + ".bundle";

    /// <summary>A packed entry's container: bytes assets in &lt;hash ^ dyn&gt;.bytes, bundles in &lt;hash ^ dyn&gt;.bundle
    /// (tools/Loc/bundle.js).</summary>
    private static string ContainerFile(Entry e, bool bundle) => (e.VfsHash ^ e.Dyn).ToString("x", CultureInfo.InvariantCulture) + (bundle ? ".bundle" : ".bytes");

    public void Dispose()
    {
        foreach (var c in _containers.Values) c.Handle.Dispose();
        _containers.Clear();
    }

    private Container ContainerFor(string file)
    {
        lock (_gate) return _containers.TryGetValue(file, out var cached) ? cached : LoadContainer(file);
    }

    private Container LoadContainer(string file)
    {
        FilesRead.Add(file);
        var handle = File.OpenHandle(file);
        var header = new byte[24];
        RandomAccess.Read(handle, header, 0);
        if (Encoding.ASCII.GetString(header, 0, 4) != "RO3V") throw new InvalidDataException("bad magic " + file);
        long offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
        int size = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16)));
        var m = new byte[size];
        RandomAccess.Read(handle, m, offset);
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(m);
        var entries = new (ulong Key, ulong Offset)[n];
        for (int i = 0; i < n; i++)
            entries[i] = (BinaryPrimitives.ReadUInt64LittleEndian(m.AsSpan(4 + i * 16)), BinaryPrimitives.ReadUInt64LittleEndian(m.AsSpan(12 + i * 16)));
        ulong total = BinaryPrimitives.ReadUInt64LittleEndian(m.AsSpan(4 + (int)n * 16));
        // Entries are contiguous: each one ends where the next (by offset) starts, the last one at the total.
        var sorted = entries.OrderBy(e => e.Offset).ToArray();
        var index = new Dictionary<ulong, (long Offset, int Length)>(sorted.Length);
        for (int i = 0; i < sorted.Length; i++)
        {
            ulong end = i + 1 < sorted.Length ? sorted[i + 1].Offset : total;
            index[sorted[i].Key] = ((long)sorted[i].Offset, checked((int)(end - sorted[i].Offset)));
        }
        var container = new Container(handle, index);
        _containers[file] = container;
        return container;
    }

    private sealed record Container(Microsoft.Win32.SafeHandles.SafeFileHandle Handle, Dictionary<ulong, (long Offset, int Length)> Index);

    /// <summary>The VFS key of an asset name: MurmurHash3 x64 128 (seed 0) of the name with A-Z lower-cased, h1 + h2.</summary>
    public static ulong LowerHash(string name)
    {
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++) if (chars[i] is >= 'A' and <= 'Z') chars[i] = (char)(chars[i] + 32);
        return Murmur64(Encoding.UTF8.GetBytes(chars));
    }

    public static ulong Murmur64(ReadOnlySpan<byte> buf, ulong seed = 0)
    {
        const ulong c1 = 9782798678568883157UL, c2 = 5545529020109919103UL;
        ulong h1 = seed, h2 = seed;
        int len = buf.Length, nb = len / 16;
        for (int i = 0; i < nb; i++)
        {
            ulong k1 = BinaryPrimitives.ReadUInt64LittleEndian(buf[(i * 16)..]);
            ulong k2 = BinaryPrimitives.ReadUInt64LittleEndian(buf[(i * 16 + 8)..]);
            k1 *= c1; k1 = ulong.RotateLeft(k1, 31); k1 *= c2; h1 ^= k1;
            h1 = ulong.RotateLeft(h1, 27); h1 += h2; h1 = h1 * 5 + 1390208809;
            k2 *= c2; k2 = ulong.RotateLeft(k2, 33); k2 *= c1; h2 ^= k2;
            h2 = ulong.RotateLeft(h2, 31); h2 += h1; h2 = h2 * 5 + 944331445;
        }
        var t = buf[(nb * 16)..];
        int r = len & 15;
        ulong t1 = 0, t2 = 0;
        for (int i = Math.Min(r, 15) - 1; i >= 8; i--) t2 ^= (ulong)t[i] << ((i - 8) * 8);
        if (r > 8) { t2 *= c2; t2 = ulong.RotateLeft(t2, 33); t2 *= c1; h2 ^= t2; }
        for (int i = Math.Min(r, 8) - 1; i >= 0; i--) t1 ^= (ulong)t[i] << (i * 8);
        if (r > 0) { t1 *= c1; t1 = ulong.RotateLeft(t1, 31); t1 *= c2; h1 ^= t1; }
        h1 ^= (ulong)len; h2 ^= (ulong)len;
        h1 += h2; h2 += h1;
        h1 = Fmix(h1); h2 = Fmix(h2);
        return h1 + h2;
    }

    private static ulong Fmix(ulong k)
    {
        k ^= k >> 33; k *= 18397679294719823053UL; k ^= k >> 33; k *= 14181476777654086739UL; k ^= k >> 33;
        return k;
    }
}
