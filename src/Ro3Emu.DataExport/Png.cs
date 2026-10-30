using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Ro3Emu.DataExport;

/// <summary>A small PNG writer (8-bit RGB or RGBA, adaptive row filters) and the halving the large pictures get.</summary>
public static class Png
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = MakeCrcTable();

    /// <summary>The image as PNG; an image without any transparency is written without its alpha channel.</summary>
    public static byte[] Encode(RgbaImage image)
    {
        bool opaque = true;
        for (int i = 3; i < image.Pixels.Length && opaque; i += 4) opaque = image.Pixels[i] == 255;
        int channels = opaque ? 3 : 4;
        using var output = new MemoryStream();
        output.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8; // bit depth
        header[9] = (byte)(opaque ? 2 : 6); // RGB or RGBA
        Chunk(output, "IHDR", header);
        Chunk(output, "IDAT", Compress(Filter(image, channels)));
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>Halved (2x2 averages, alpha weighted) until neither side is above <paramref name="maxSide"/>.</summary>
    public static RgbaImage Shrink(RgbaImage image, int maxSide)
    {
        while (Math.Max(image.Width, image.Height) > maxSide && image.Width > 1 && image.Height > 1)
        {
            int w = image.Width / 2, h = image.Height / 2;
            var pixels = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int a = 0, r = 0, g = 0, b = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int i = ((y * 2 + dy) * image.Width + x * 2 + dx) * 4, alpha = image.Pixels[i + 3];
                            r += image.Pixels[i] * alpha; g += image.Pixels[i + 1] * alpha; b += image.Pixels[i + 2] * alpha; a += alpha;
                        }
                    int o = (y * w + x) * 4;
                    if (a > 0) { pixels[o] = (byte)(r / a); pixels[o + 1] = (byte)(g / a); pixels[o + 2] = (byte)(b / a); }
                    pixels[o + 3] = (byte)((a + 2) / 4);
                }
            image = new RgbaImage(w, h, pixels);
        }
        return image;
    }

    /// <summary>Each row with the filter whose bytes sum smallest (the usual heuristic).</summary>
    private static byte[] Filter(RgbaImage image, int channels)
    {
        int stride = image.Width * channels;
        byte[] pixels = image.Pixels;
        if (channels == 3)
        {
            pixels = new byte[stride * image.Height];
            for (int i = 0, o = 0; i < image.Pixels.Length; i += 4, o += 3) image.Pixels.AsSpan(i, 3).CopyTo(pixels.AsSpan(o));
        }
        var output = new byte[(stride + 1) * image.Height];
        var candidate = new byte[stride];
        var best = new byte[stride];
        for (int y = 0; y < image.Height; y++)
        {
            var row = pixels.AsSpan(y * stride, stride);
            var above = y > 0 ? pixels.AsSpan((y - 1) * stride, stride) : [];
            long bestSum = long.MaxValue;
            byte bestType = 0;
            for (byte type = 0; type <= 4; type++)
            {
                long sum = 0;
                for (int i = 0; i < stride; i++)
                {
                    int left = i >= channels ? row[i - channels] : 0, up = above.Length > 0 ? above[i] : 0;
                    int corner = i >= channels && above.Length > 0 ? above[i - channels] : 0;
                    int predicted = type switch { 1 => left, 2 => up, 3 => (left + up) / 2, 4 => Paeth(left, up, corner), _ => 0 };
                    byte value = (byte)(row[i] - predicted);
                    candidate[i] = value;
                    sum += value < 128 ? value : 256 - value;
                }
                if (sum < bestSum)
                {
                    bestSum = sum;
                    bestType = type;
                    candidate.CopyTo(best, 0);
                }
            }
            output[y * (stride + 1)] = bestType;
            best.CopyTo(output, y * (stride + 1) + 1);
        }
        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(data);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] body)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, body.Length);
        output.Write(number);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(body);
        uint crc = Crc(Crc(0xFFFFFFFF, typeBytes), body) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
