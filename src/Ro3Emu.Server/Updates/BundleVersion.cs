using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Ro3Emu.Server.Updates;

/// <summary>
/// The version a downloaded server says it is, read without running it: the AssemblyInformationalVersion of
/// Ro3Emu.Server.dll ("1.0.24+&lt;commit&gt;", pack.ps1's -Version). A release ships the server as a .NET single-file
/// bundle: the apphost holds a 64-bit offset of the bundle manifest right before a fixed 32-byte signature
/// (Microsoft.NET.HostModel, Bundle/HostWriter and Bundle/Manifest), and the manifest lists each embedded file's offset,
/// size, compressed size (manifest version 6 and later) and path. A plain assembly is read as it is.
/// </summary>
public static class BundleVersion
{
    /// <summary>The bundle signature the SDK writes after the manifest offset (HostWriter.bundleSignature).</summary>
    internal static readonly byte[] Signature =
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
    ];

    /// <summary>The largest assembly read out of a bundle (the server's own is about 35 MB).</summary>
    private const long AssemblyMax = 256L << 20;

    /// <summary>The informational version of <paramref name="assembly"/> inside the bundle <paramref name="file"/> (or of
    /// the file itself when it is that assembly); null when it has none or is neither.</summary>
    public static string? Read(string file, string assembly = "Ro3Emu.Server.dll")
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (FindManifest(stream) is not { } manifest) return InformationalVersion(stream);
            using var entry = Entry(stream, manifest, assembly);
            return entry == null ? null : InformationalVersion(entry);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or FormatException) { return null; } // not a bundle the SDK wrote
    }

    /// <summary>The manifest's offset: the 8 bytes before the signature, when they point inside the file.</summary>
    private static long? FindManifest(FileStream stream)
    {
        long at = Find(stream, Signature);
        if (at < 8) return null;
        Span<byte> offset = stackalloc byte[8];
        stream.Position = at - 8;
        stream.ReadExactly(offset);
        long manifest = BitConverter.ToInt64(offset);
        return manifest > 0 && manifest < stream.Length ? manifest : null;
    }

    /// <summary>The first position of <paramref name="pattern"/> in the stream, or -1 (read in 1 MB blocks that overlap).</summary>
    private static long Find(Stream stream, byte[] pattern)
    {
        byte[] buffer = new byte[1 << 20];
        long start = 0;
        stream.Position = 0;
        while (true)
        {
            int filled = 0, read;
            while (filled < buffer.Length && (read = stream.Read(buffer, filled, buffer.Length - filled)) > 0) filled += read;
            int hit = buffer.AsSpan(0, filled).IndexOf(pattern);
            if (hit >= 0) return start + hit;
            if (filled < buffer.Length) return -1;
            start += filled - (pattern.Length - 1);
            stream.Position = start;
        }
    }

    private static MemoryStream? Entry(FileStream stream, long manifest, string name)
    {
        stream.Position = manifest;
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        uint major = reader.ReadUInt32();
        reader.ReadUInt32(); // minor
        int count = reader.ReadInt32();
        reader.ReadString(); // bundle id
        if (major >= 2) for (int i = 0; i < 4; i++) reader.ReadInt64(); // deps.json and runtimeconfig.json: offset, size
        if (major >= 2) reader.ReadUInt64(); // flags
        for (int i = 0; i < count && i < 10_000; i++)
        {
            long offset = reader.ReadInt64(), size = reader.ReadInt64(), compressed = major >= 6 ? reader.ReadInt64() : 0;
            reader.ReadByte(); // file type
            string path = reader.ReadString();
            if (!string.Equals(path, name, StringComparison.Ordinal)) continue;
            long stored = compressed > 0 ? compressed : size;
            if (offset < 0 || size <= 0 || size > AssemblyMax || stored > AssemblyMax || offset + stored > stream.Length) return null;
            byte[] bytes = new byte[stored];
            stream.Position = offset;
            stream.ReadExactly(bytes);
            if (compressed == 0) return new MemoryStream(bytes, writable: false);
            // A compressed bundle (EnableCompressionInSingleFile) stores deflate data.
            var inflated = new MemoryStream();
            using (var deflate = new System.IO.Compression.DeflateStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress))
                deflate.CopyTo(inflated);
            inflated.Position = 0;
            return inflated;
        }
        return null;
    }

    /// <summary>The assembly's AssemblyInformationalVersionAttribute value, or null.</summary>
    private static string? InformationalVersion(Stream assembly)
    {
        assembly.Position = 0;
        try
        {
            using var pe = new PEReader(assembly, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) return null;
            foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = md.GetCustomAttribute(handle);
                if (AttributeName(md, attribute) != "AssemblyInformationalVersionAttribute") continue;
                var blob = md.GetBlobReader(attribute.Value);
                if (blob.ReadUInt16() != 1) return null; // the custom attribute prolog
                return blob.ReadSerializedString();
            }
            return null;
        }
        catch (BadImageFormatException) { return null; }
    }

    private static string? AttributeName(MetadataReader md, CustomAttribute attribute)
    {
        var parent = attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
            HandleKind.MethodDefinition => md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
            _ => default(EntityHandle),
        };
        return parent.Kind switch
        {
            HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name),
            HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
            _ => null,
        };
    }
}
