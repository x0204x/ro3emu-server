#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Encryption of every voice frame after Auth (and the Auth's proof of the secret), one copy for the in-game client (compiled against the client's own
    /// mscorlib/System.Core: it has AES and HMAC-SHA256 but no AES-GCM), the relay and the bot (both link this file
    /// through Ro3Emu.Server). The session secret rides in the 77016 ticket over the encrypted game gate.
    ///
    /// Sealed payload: counter u64 LE | AES-128-CTR ciphertext | first 16 bytes of HMAC-SHA256(direction | frame type |
    /// counter | ciphertext). Encrypt-then-MAC; the CTR block is direction | 3 zero bytes | counter u64 LE | block u32
    /// LE, so the two directions never share a keystream. The counter starts at 0 and must be exactly the next one:
    /// TCP keeps order, so a replayed, dropped, reordered or reflected frame fails, and a failed frame leaves the
    /// counter where it was. One instance per connection; Seal and Open each keep their own state, so one writer and
    /// one reader thread may use them at once (not two writers).
    /// </summary>
    public sealed class VoiceCipher : IDisposable
    {
        public const int SecretSize = 32;
        private const int CounterSize = 8, TagSize = 16, Block = 16;

        /// <summary>Bytes a sealed payload adds: counter + tag.</summary>
        public const int Overhead = CounterSize + TagSize;

        /// <summary>Length of <see cref="AuthProof"/>.</summary>
        public const int AuthProofSize = 16;

        private const byte ClientToServer = 1, ServerToClient = 2;

        private readonly Direction _send, _receive;

        private VoiceCipher(byte[] secret, byte sendDirection, byte receiveDirection)
        {
            if (secret == null || secret.Length != SecretSize) throw new ArgumentException("the voice secret must be " + SecretSize + " bytes", nameof(secret));
            byte[] encKey = new byte[16];
            Array.Copy(Derive(secret, "ro3emu voice enc"), encKey, encKey.Length);
            byte[] macKey = Derive(secret, "ro3emu voice mac");
            _send = new Direction(sendDirection, encKey, macKey);
            _receive = new Direction(receiveDirection, encKey, macKey);
        }

        public static VoiceCipher ForClient(byte[] secret) => new VoiceCipher(secret, ClientToServer, ServerToClient);

        public static VoiceCipher ForServer(byte[] secret) => new VoiceCipher(secret, ServerToClient, ClientToServer);

        /// <summary>A fresh session secret (the relay issues one per ticket).</summary>
        public static byte[] NewSecret()
        {
            var secret = new byte[SecretSize];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(secret);
            return secret;
        }

        /// <summary>
        /// What the Auth frame carries next to the token: HMAC-SHA256(secret, "ro3emu voice auth" | role id u64 LE |
        /// token), first 16 bytes. Auth itself crosses the wire in the clear, so the token alone could be read off it
        /// and raced in first; the proof shows the sender also holds the secret, which never leaves the encrypted game
        /// gate, so a copied token can neither use the ticket up nor replace the player's live connection.
        /// </summary>
        public static byte[] AuthProof(byte[] secret, long roleId, byte[] token)
        {
            byte[] label = Encoding.ASCII.GetBytes("ro3emu voice auth");
            var input = new byte[label.Length + 8 + token.Length];
            Array.Copy(label, input, label.Length);
            WriteUInt64(input, label.Length, (ulong)roleId);
            Array.Copy(token, 0, input, label.Length + 8, token.Length);
            var proof = new byte[AuthProofSize];
            using (var hmac = new HMACSHA256(secret)) Array.Copy(hmac.ComputeHash(input), proof, AuthProofSize);
            return proof;
        }

        /// <summary>Constant-time comparison of two Auth proofs (false for a wrong length).</summary>
        public static bool ProofEquals(byte[] expected, byte[] actual)
        {
            if (expected == null || actual == null || expected.Length != AuthProofSize || actual.Length != AuthProofSize) return false;
            return FixedTimeEquals(expected, actual, 0);
        }

        /// <summary>The next outgoing frame's sealed payload.</summary>
        public byte[] Seal(byte type, byte[] payload)
        {
            var d = _send;
            int length = payload == null ? 0 : payload.Length;
            var sealedPayload = new byte[CounterSize + length + TagSize];
            WriteUInt64(sealedPayload, 0, d.Counter);
            if (length > 0) Array.Copy(payload!, 0, sealedPayload, CounterSize, length);
            d.Crypt(d.Counter, sealedPayload, CounterSize, length);
            byte[] tag = d.Tag(type, sealedPayload, length);
            Array.Copy(tag, 0, sealedPayload, CounterSize + length, TagSize);
            d.Counter++;
            return sealedPayload;
        }

        /// <summary>The plaintext of the next incoming frame, or null when it is not exactly that frame (wrong key,
        /// changed byte or type, replay, gap, reflection, too short): the caller closes the connection.</summary>
        public byte[]? Open(byte type, byte[]? sealedPayload)
        {
            var d = _receive;
            if (sealedPayload == null || sealedPayload.Length < Overhead) return null;
            if (ReadUInt64(sealedPayload, 0) != d.Counter) return null;
            int length = sealedPayload.Length - Overhead;
            if (!FixedTimeEquals(d.Tag(type, sealedPayload, length), sealedPayload, CounterSize + length)) return null;
            var plain = new byte[length];
            Array.Copy(sealedPayload, CounterSize, plain, 0, length);
            d.Crypt(d.Counter, plain, 0, length);
            d.Counter++;
            return plain;
        }

        public void Dispose()
        {
            _send.Dispose();
            _receive.Dispose();
        }

        private static byte[] Derive(byte[] secret, string label)
        {
            using (var hmac = new HMACSHA256(secret)) return hmac.ComputeHash(Encoding.ASCII.GetBytes(label));
        }

        private static bool FixedTimeEquals(byte[] tag, byte[] data, int offset)
        {
            int diff = 0;
            for (int i = 0; i < TagSize; i++) diff |= tag[i] ^ data[offset + i];
            return diff == 0;
        }

        private static void WriteUInt64(byte[] buffer, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++) buffer[offset + i] = (byte)(value >> (8 * i));
        }

        private static ulong ReadUInt64(byte[] buffer, int offset)
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++) value |= (ulong)buffer[offset + i] << (8 * i);
            return value;
        }

        /// <summary>One direction's counter, keystream and MAC (own AES/HMAC objects: not thread-safe to share).</summary>
        private sealed class Direction : IDisposable
        {
            public ulong Counter;
            private readonly byte _id;
            private readonly Aes _aes;
            private readonly ICryptoTransform _ecb;
            private readonly HMACSHA256 _mac;
            private readonly byte[] _ctrBlock = new byte[Block], _keystream = new byte[Block];

            public Direction(byte id, byte[] encKey, byte[] macKey)
            {
                _id = id;
                _aes = Aes.Create();
                _aes.Mode = CipherMode.ECB;
                _aes.Padding = PaddingMode.None;
                _aes.Key = encKey;
                _ecb = _aes.CreateEncryptor();
                _mac = new HMACSHA256(macKey);
            }

            /// <summary>XORs the keystream for frame <paramref name="counter"/> over data[offset..offset+count).</summary>
            public void Crypt(ulong counter, byte[] data, int offset, int count)
            {
                Array.Clear(_ctrBlock, 0, Block);
                _ctrBlock[0] = _id;
                WriteUInt64(_ctrBlock, 4, counter);
                for (int done = 0, block = 0; done < count; block++)
                {
                    _ctrBlock[12] = (byte)block;
                    _ctrBlock[13] = (byte)(block >> 8);
                    _ctrBlock[14] = (byte)(block >> 16);
                    _ctrBlock[15] = (byte)(block >> 24);
                    _ecb.TransformBlock(_ctrBlock, 0, Block, _keystream, 0);
                    int n = Math.Min(Block, count - done);
                    for (int i = 0; i < n; i++) data[offset + done + i] ^= _keystream[i];
                    done += n;
                }
            }

            /// <summary>HMAC over direction | type | counter + ciphertext (the sealed payload's first 8 + length bytes).</summary>
            public byte[] Tag(byte type, byte[] sealedPayload, int length)
            {
                var input = new byte[2 + CounterSize + length];
                input[0] = _id;
                input[1] = type;
                Array.Copy(sealedPayload, 0, input, 2, CounterSize + length);
                return _mac.ComputeHash(input);
            }

            public void Dispose()
            {
                _ecb.Dispose();
                _aes.Dispose();
                _mac.Dispose();
            }
        }
    }
}
