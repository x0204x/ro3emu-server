namespace Ro3Emu.DataExport;

/// <summary>
/// Rewrites the client's obfuscated Lua 5.4 chunks (LuaPayload/*.lua.bytes) into standard Lua 5.4 bytecode.
/// Obfuscation, as read from xlua.dll lundump (FUN_180032420 and callees):
///   - signature byte 0x1E instead of ESC (0x1B);
///   - every instruction of a function is XORed with (sizecode &lt;&lt; 16 | sizecode);
///   - every string payload s[0..n) is chained: s[k] ^= enc[k-1] for k = n-1..1, then s[0] ^= (byte)n.
/// Everything else (opcodes, constant tags, layout) is stock 5.4.
/// </summary>
public static class LuaChunkDecoder
{
    /// <summary>Decodes every *.lua.bytes under <paramref name="inDir"/> into <paramref name="outDir"/> (same tree, .luac).</summary>
    public static (int Ok, int Failed) DecodeTree(string inDir, string outDir, Action<string>? error = null, Action<int, int>? progress = null)
    {
        int ok = 0, failed = 0;
        var files = Directory.GetFiles(inDir, "*.bytes", SearchOption.AllDirectories);
        foreach (var path in files)
        {
            var rel = Path.GetRelativePath(inDir, path);
            var outPath = Path.Combine(outDir, rel[..^".lua.bytes".Length] + ".luac");
            try
            {
                var data = File.ReadAllBytes(path);
                Decode(data);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                File.WriteAllBytes(outPath, data);
                ok++;
            }
            catch (Exception e)
            {
                failed++;
                error?.Invoke($"{rel}: {e.Message}");
            }
            progress?.Invoke(ok + failed, files.Length);
        }
        return (ok, failed);
    }

    /// <summary>Decodes one chunk in place.</summary>
    public static void Decode(byte[] data) => new Chunk(data).Decode();

    private sealed class Chunk(byte[] d)
    {
        // Client opcode -> stock Lua 5.4 opcode, read case by case from luaV_execute (xlua.dll FUN_180033f40)
        // and the permuted luaP_opmodes at file offset 0xaf4b0. Opcodes from CLOSE (54) up are unchanged.
        private static readonly byte[] OpMap = BuildOpMap();

        private static byte[] BuildOpMap()
        {
            var m = new byte[128];
            for (int i = 0; i < 128; i++) m[i] = (byte)i;
            m[0] = 0;                                              // MOVE
            for (int i = 1; i <= 12; i++) m[i] = (byte)(33 + i);   // ADD..SHR      -> 34..45
            for (int i = 13; i <= 15; i++) m[i] = (byte)(33 + i);  // MMBIN..MMBINK -> 46..48
            for (int i = 16; i <= 20; i++) m[i] = (byte)(33 + i);  // UNM..CONCAT   -> 49..53
            for (int i = 21; i <= 39; i++) m[i] = (byte)(i - 20);  // LOADI..NEWTABLE -> 1..19
            for (int i = 40; i <= 53; i++) m[i] = (byte)(i - 20);  // SELF, ADDI..SHLI -> 20..33
            m[83] = 12;                                            // extra case 0x53: a second GETTABLE
            return m;
        }

        private int _p;

        public void Decode()
        {
            if (d[0] != 0x1E && d[0] != 0x1B) throw new InvalidDataException($"unexpected signature 0x{d[0]:X2}");
            d[0] = 0x1B;
            if (d[4] != 0x54) throw new InvalidDataException("not Lua 5.4");
            _p = 31;     // header: sig4 ver fmt data6 sizes3 int8 num8
            _p++;        // main closure sizeupvalues
            Function();
            if (_p != d.Length) throw new InvalidDataException($"trailing bytes: parsed {_p} of {d.Length}");
        }

        private ulong Varint()
        {
            ulong x = 0;
            byte b;
            do { b = d[_p++]; x = (x << 7) | (uint)(b & 0x7F); } while ((b & 0x80) == 0);
            return x;
        }

        private void Str()
        {
            var size = (int)Varint();
            if (size == 0) return;
            int n = size - 1;
            for (int k = n - 1; k >= 1; k--) d[_p + k] ^= d[_p + k - 1];
            if (n > 0) d[_p] ^= (byte)n;
            _p += n;
        }

        private void Function()
        {
            Str();          // source
            Varint();       // linedefined
            Varint();       // lastlinedefined
            _p += 3;         // numparams, is_vararg, maxstacksize
            var sizecode = (uint)Varint();
            uint key = (sizecode << 16) | sizecode;
            for (uint i = 0; i < sizecode; i++, _p += 4)
            {
                uint w = BitConverter.ToUInt32(d, _p) ^ key;
                if ((w & 0x7F) > 83) throw new InvalidDataException($"unknown opcode {w & 0x7F} at {_p}");
                w = (w & ~0x7Fu) | OpMap[w & 0x7F];
                BitConverter.TryWriteBytes(d.AsSpan(_p, 4), w);
            }
            var sizek = (int)Varint();
            for (int i = 0; i < sizek; i++)
            {
                byte tag = d[_p++];
                switch (tag)
                {
                    case 0x00: case 0x01: case 0x11: break;
                    case 0x03: case 0x13: _p += 8; break;
                    case 0x04: case 0x14: Str(); break;
                    default: throw new InvalidDataException($"constant tag 0x{tag:X2} at {_p - 1}");
                }
            }
            var sizeup = (int)Varint();
            _p += 3 * sizeup;
            var sizep = (int)Varint();
            for (int i = 0; i < sizep; i++) Function();
            var lineinfo = (int)Varint();
            _p += lineinfo;
            var abslines = (int)Varint();
            for (int i = 0; i < abslines; i++) { Varint(); Varint(); }
            var locvars = (int)Varint();
            for (int i = 0; i < locvars; i++) { Str(); Varint(); Varint(); }
            var upnames = (int)Varint();
            for (int i = 0; i < upnames; i++) Str();
        }
    }
}
