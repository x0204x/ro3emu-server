namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// G.711 mu-law (ITU-T G.711, the classic Sun g711.c tables): 16-bit linear PCM to one byte per sample and back.
    /// Voice relay codec 1 (16 kHz mono, 20 ms = 320 bytes).
    /// </summary>
    public static class MuLaw
    {
        private const int Bias = 0x84;
        private const int Clip = 32635;

        private static readonly short[] DecodeTable = BuildDecode();
        private static readonly byte[] ExponentTable = BuildExponent();

        public static byte Encode(short pcm)
        {
            int sample = pcm;
            int sign = (sample >> 8) & 0x80;
            if (sign != 0) sample = -sample;
            if (sample > Clip) sample = Clip;
            sample += Bias;
            int exponent = ExponentTable[(sample >> 7) & 0xFF];
            int mantissa = (sample >> (exponent + 3)) & 0x0F;
            return (byte)~(sign | (exponent << 4) | mantissa);
        }

        public static short Decode(byte ulaw) => DecodeTable[ulaw];

        /// <summary>Encodes <paramref name="count"/> samples into <paramref name="output"/> at <paramref name="offset"/>.</summary>
        public static void Encode(short[] pcm, int count, byte[] output, int offset)
        {
            for (int i = 0; i < count; i++) output[offset + i] = Encode(pcm[i]);
        }

        /// <summary>Decodes <paramref name="count"/> bytes of <paramref name="input"/> from <paramref name="offset"/>.</summary>
        public static short[] Decode(byte[] input, int offset, int count)
        {
            var pcm = new short[count];
            for (int i = 0; i < count; i++) pcm[i] = DecodeTable[input[offset + i]];
            return pcm;
        }

        private static short[] BuildDecode()
        {
            var table = new short[256];
            for (int i = 0; i < 256; i++)
            {
                int u = ~i & 0xFF;
                int exponent = (u >> 4) & 0x07;
                int sample = (((u & 0x0F) << 3) + Bias) << exponent;
                sample -= Bias;
                table[i] = (short)((u & 0x80) != 0 ? -sample : sample);
            }
            return table;
        }

        /// <summary>The position of the highest set bit of a byte (0 for 0 and 1).</summary>
        private static byte[] BuildExponent()
        {
            var table = new byte[256];
            for (int i = 2; i < 256; i++) table[i] = (byte)(table[i >> 1] + 1);
            return table;
        }
    }
}
