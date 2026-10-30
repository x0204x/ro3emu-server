using System.Buffers.Binary;
using System.IO.Compression;
using Ro3Emu.DataExport;

namespace Ro3Emu.Setup.Tests;

/// <summary>The panel's pictures: which ones the tables ask for, the PNG writer, and (with a client) the decode itself.</summary>
public class IconExportTests
{
    [Fact]
    public void RequestsComeFromTheTablesOncePerFileAndOnlyPlainNames()
    {
        var dir = Directory.CreateTempSubdirectory("ro3emu-icons-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "ItemConfig.json"), """
                {"1":{"_kIcon":"item_1.png","_kPic":"item_null_01.png"},"2":{"_kIcon":"item_1.png","_kPic":"card_2.png"},
                 "3":{"_kIcon":"../evil.png"},"4":{"_kIcon":""}}
                """);
            File.WriteAllText(Path.Combine(dir.FullName, "NPCConfig.json"), """
                {"10":{"_iNPCType":1,"_kHeadIcon":"headicon_monster_a.png"},"11":{"_iNPCType":2,"_kHeadIcon":"headicon_npc_b.png"}}
                """);
            File.WriteAllText(Path.Combine(dir.FullName, "JobConfig.json"), """{"1000":{"_kIcon":"icon_job_novice.png","_kBigIcon":"iconbig_job_novice.png"}}""");
            File.WriteAllText(Path.Combine(dir.FullName, "MapConfig.json"), """{"7":{"_kBgImage":["sc_a_pathfind.png","sc_a_deco.png"]},"8":{"_kBgImage":[]}}""");

            var requests = IconExport.Requests(dir.FullName);
            Assert.Equal(["items/item_1.png", "items/card_2.png", "monsters/headicon_monster_a.png", "classes/icon_job_novice.png",
                "classes/iconbig_job_novice.png", "maps/sc_a_pathfind.png"], requests.Select(r => r.Path));
            Assert.Equal(512, requests.Single(r => r.Kind == "maps").MaxSide);
            Assert.Equal(400, requests.Single(r => r.File == "card_2.png").MaxSide);
        }
        finally { dir.Delete(true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PngHoldsTheExactPixels(bool opaque)
    {
        var pixels = new byte[5 * 3 * 4];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 37 + 11);
        if (opaque) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var png = Png.Encode(new RgbaImage(5, 3, pixels));

        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
        Assert.Equal((5, 3), (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20))));
        int channels = png[25] == 2 ? 3 : 4;
        Assert.Equal(opaque ? 3 : 4, channels);
        Assert.Equal(pixels, Decode(png, 5, 3, channels));
    }

    [Fact]
    public void ShrinkHalvesUntilItFits()
    {
        var image = Png.Shrink(new RgbaImage(1024, 600, new byte[1024 * 600 * 4]), 512);
        Assert.Equal((512, 300), (image.Width, image.Height));
        var white = Enumerable.Repeat((byte)255, 4 * 4 * 4).ToArray();
        Assert.All(Png.Shrink(new RgbaImage(4, 4, white), 2).Pixels, b => Assert.Equal(255, b));
    }

    [Theory]
    [InlineData(4, new byte[] { 1, 2, 3, 4 })] // RGBA32
    [InlineData(5, new byte[] { 4, 1, 2, 3 })] // ARGB32
    [InlineData(14, new byte[] { 3, 2, 1, 4 })] // BGRA32
    public void UncompressedUnityFormatsBecomeRgba(int format, byte[] raw) =>
        Assert.Equal([1, 2, 3, 4], UnityImages.ToRgba(raw, 1, 1, format));

    /// <summary>The IDAT stream inflated and unfiltered: a small PNG reader for these tests.</summary>
    private static byte[] Decode(byte[] png, int width, int height, int channels)
    {
        using var idat = new MemoryStream();
        for (int at = 8; at < png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            if (System.Text.Encoding.ASCII.GetString(png, at + 4, 4) == "IDAT") idat.Write(png, at + 8, length);
            at += 12 + length;
        }
        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        byte[] data = raw.ToArray(), rgba = new byte[width * height * 4];
        int stride = width * channels;
        var prior = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            byte type = data[y * (stride + 1)];
            var row = data.AsSpan(y * (stride + 1) + 1, stride).ToArray();
            for (int i = 0; i < stride; i++)
            {
                int left = i >= channels ? row[i - channels] : 0, up = prior[i], corner = i >= channels ? prior[i - channels] : 0;
                int p = left + up - corner, pa = Math.Abs(p - left), pb = Math.Abs(p - up), pc = Math.Abs(p - corner);
                int paeth = pa <= pb && pa <= pc ? left : pb <= pc ? up : corner;
                row[i] = (byte)(row[i] + type switch { 1 => left, 2 => up, 3 => (left + up) / 2, 4 => paeth, _ => 0 });
            }
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < channels; c++) rgba[(y * width + x) * 4 + c] = row[x * channels + c];
                if (channels == 3) rgba[(y * width + x) * 4 + 3] = 255;
            }
            prior = row;
        }
        return rgba;
    }
}

/// <summary>Pictures decoded from the installed client: each of the three shapes the client stores them in.</summary>
public sealed class RealClientIconTests
{
    private static readonly string Client = Environment.GetEnvironmentVariable("RO3_CLIENT_DIR") is { Length: > 0 } dir ? dir : @"D:\RO3\ROO_PC";

    [RealClientExportTests.ClientFact]
    public void TheClientsIconsDecodeAndASecondExportKeepsThem()
    {
        IconExport.Request[] requests =
        [
            new("items", "item_1001001.png"), // a texture of its own, RGBA32
            new("monsters", "headicon_monster_angeling.png"), // a sprite in the CommonHeadMonster_Icon atlas, DXT5
            new("classes", "icon_job_crusader.png"), // a sprite in the Job atlas
            new("maps", "sc_prontera_001_pathfind.png", 512), // a 1024 DXT5 texture, halved
            new("items", "no_such_icon_anywhere.png"),
        ];
        var output = Directory.CreateTempSubdirectory("ro3emu-realicons-");
        try
        {
            using (var vfs = new Vfs(GameDataExport.VfsDir(Client)))
            {
                var first = IconExport.Export(vfs, requests, output.FullName);
                Assert.Equal((4, 0, 1, 0), (first.Written, first.Unchanged, first.Missing, first.Failed));
                var second = IconExport.Export(vfs, requests, output.FullName);
                Assert.Equal((0, 4), (second.Written, second.Unchanged));
            }
            Assert.Equal((128, 128), Size(Path.Combine(output.FullName, "items", "item_1001001.png")));
            Assert.Equal((128, 128), Size(Path.Combine(output.FullName, "monsters", "headicon_monster_angeling.png")));
            Assert.Equal((56, 56), Size(Path.Combine(output.FullName, "classes", "icon_job_crusader.png")));
            Assert.Equal((512, 512), Size(Path.Combine(output.FullName, "maps", "sc_prontera_001_pathfind.png")));
            // A host without the whole client fetches the atlases too.
            var sources = IconExport.SourceFiles(Client, requests);
            using var index = new Vfs(GameDataExport.VfsDir(Client));
            Assert.Contains("ro3_Data/StreamingAssets/VFS/" + index.SourceFile("CommonHeadMonster_Icon.spriteatlasv2"), sources);
            Assert.Contains("ro3_Data/StreamingAssets/VFS/" + index.SourceFile("Job.spriteatlasv2"), sources);
        }
        finally { output.Delete(true); }
    }

    private static (int, int) Size(string png)
    {
        var bytes = File.ReadAllBytes(png);
        return (BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20)));
    }
}
