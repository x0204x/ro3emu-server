using System;
using System.Text;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The voice relay's wire format (the server's VoiceRelay): every frame is 'V' 'T' | type u8 | length u16 LE | payload,
    /// numbers little endian. Up 0x01..0x08, down 0x81..0x88. The ticket comes over the game connection (77015 up with
    /// an empty body, 77016 down).
    /// </summary>
    public static class VoiceProtocol
    {
        public const uint TicketRequestMsgId = 77015;
        public const uint TicketMsgId = 77016;

        public const int HeaderSize = 5;
        public const int MaxPayload = 0xFFFF;
        public const byte Version = 1;
        public const int TokenSize = 16;

        // Up.
        public const byte Auth = 0x01, Join = 0x02, Leave = 0x03, Mic = 0x04, Audio = 0x05, Ping = 0x06, Speaker = 0x08;
        // Down.
        public const byte AuthOk = 0x81, JoinResult = 0x82, MemberChange = 0x83, AudioDown = 0x84, MicState = 0x85, Vad = 0x86,
            Pong = 0x87, Kicked = 0x88;

        /// <summary>Audio payload codec ids (first payload byte; the server does not look inside).</summary>
        public const byte CodecMuLaw = 1;

        /// <summary>The 77016 body: where the relay is and the one-time token for it. Port 0 = voice off on this server.</summary>
        public sealed class Ticket
        {
            public int Port;
            public string Host;
            public long RoleId;
            public byte[] Token;
            public uint TtlSeconds;
        }

        /// <summary>u16 port | u8 host length | host ASCII | i64 role id | 16-byte token | u32 ttl seconds; null if malformed.</summary>
        public static Ticket ParseTicket(byte[] body)
        {
            if (body == null || body.Length < 3) return null;
            int port = body[0] | (body[1] << 8);
            int hostLength = body[2];
            int at = 3 + hostLength;
            if (body.Length < at + 8 + TokenSize + 4) return null;
            var token = new byte[TokenSize];
            Buffer.BlockCopy(body, at + 8, token, 0, TokenSize);
            return new Ticket
            {
                Port = port,
                Host = Encoding.ASCII.GetString(body, 3, hostLength),
                RoleId = BitConverter.ToInt64(body, at),
                Token = token,
                TtlSeconds = BitConverter.ToUInt32(body, at + 8 + TokenSize),
            };
        }

        /// <summary>One frame around <paramref name="payload"/>.</summary>
        public static byte[] Frame(byte type, byte[] payload)
        {
            payload = payload ?? new byte[0];
            if (payload.Length > MaxPayload) throw new ArgumentException("voice frame payload too long");
            var frame = new byte[HeaderSize + payload.Length];
            frame[0] = (byte)'V';
            frame[1] = (byte)'T';
            frame[2] = type;
            frame[3] = (byte)payload.Length;
            frame[4] = (byte)(payload.Length >> 8);
            Buffer.BlockCopy(payload, 0, frame, HeaderSize, payload.Length);
            return frame;
        }

        /// <summary>The payload length of a frame header, or -1 when the magic is wrong.</summary>
        public static int PayloadLength(byte[] header)
        {
            if (header.Length < HeaderSize || header[0] != 'V' || header[1] != 'T') return -1;
            return header[3] | (header[4] << 8);
        }

        public static byte[] AuthFrame(long roleId, byte[] token)
        {
            if (token == null || token.Length != TokenSize) throw new ArgumentException("voice token must be 16 bytes");
            var payload = new byte[1 + 8 + TokenSize];
            payload[0] = Version;
            BitConverter.GetBytes(roleId).CopyTo(payload, 1);
            Buffer.BlockCopy(token, 0, payload, 9, TokenSize);
            return Frame(Auth, payload);
        }

        /// <summary>Join / Leave: u8 length | ASCII channel.</summary>
        public static byte[] ChannelFrame(byte type, string channel)
        {
            var name = Encoding.ASCII.GetBytes(channel ?? "");
            if (name.Length > 255) throw new ArgumentException("voice channel name too long");
            var payload = new byte[1 + name.Length];
            payload[0] = (byte)name.Length;
            Buffer.BlockCopy(name, 0, payload, 1, name.Length);
            return Frame(type, payload);
        }

        /// <summary>Mic / Speaker: u8 on.</summary>
        public static byte[] SwitchFrame(byte type, bool on) => Frame(type, new[] { on ? (byte)1 : (byte)0 });

        /// <summary>Audio: u16 seq | codec u8 | data.</summary>
        public static byte[] AudioFrame(ushort seq, byte codec, byte[] data, int count)
        {
            var payload = new byte[2 + 1 + count];
            payload[0] = (byte)seq;
            payload[1] = (byte)(seq >> 8);
            payload[2] = codec;
            Buffer.BlockCopy(data, 0, payload, 3, count);
            return Frame(Audio, payload);
        }

        /// <summary>JoinResult: code u8 | u8 length | channel.</summary>
        public static bool ParseJoinResult(byte[] payload, out int code, out string channel)
        {
            code = -1;
            channel = null;
            if (payload.Length < 2 || payload.Length < 2 + payload[1]) return false;
            code = payload[0];
            channel = Encoding.ASCII.GetString(payload, 2, payload[1]);
            return true;
        }

        /// <summary>MemberChange / MicState / Vad: i64 role id | u8 flag.</summary>
        public static bool ParseRoleFlag(byte[] payload, out long roleId, out bool flag)
        {
            roleId = 0;
            flag = false;
            if (payload.Length < 9) return false;
            roleId = BitConverter.ToInt64(payload, 0);
            flag = payload[8] != 0;
            return true;
        }

        /// <summary>Audio down: i64 speaker | u16 seq | payload (codec u8 | data); data starts at <paramref name="dataOffset"/>.</summary>
        public static bool ParseAudio(byte[] payload, out long speaker, out ushort seq, out byte codec, out int dataOffset)
        {
            speaker = 0;
            seq = 0;
            codec = 0;
            dataOffset = 11;
            if (payload.Length < 11) return false;
            speaker = BitConverter.ToInt64(payload, 0);
            seq = (ushort)(payload[8] | (payload[9] << 8));
            codec = payload[10];
            return true;
        }
    }
}
