using System;
using System.Text;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Bounded wire format for opt-in skill-presentation telemetry. The hook never sends it until this connection has
    /// received a grant from the server; neither side sends raw game packets, chat, credentials, file paths or stacks.
    /// </summary>
    public static class SkillTraceProtocol
    {
        public const uint ReportMsgId = 77017;
        public const uint GrantMsgId = 77018;
        public const byte Version = 1;
        public const int TokenSize = 16;
        public const uint MaxGrantSeconds = 86400;
        public const int GrantSize = 25;
        public const int ReportHeaderSize = 88;
        public const int MaxReasonBytes = 64;
        public const int MaxReportSize = ReportHeaderSize + MaxReasonBytes;

        internal static bool IsSkillMessage(uint msgId) => msgId == 31100 || msgId == 31106
            || msgId == 31121 || msgId == 31107 || msgId == 31101 || msgId == 31119
            || msgId == 31109 || msgId == 31120 || msgId == 31108;

        public enum Stage : byte
        {
            RouteReceived = 1,
            HandlerCompleted = 2,
            HandlerFailed = 3,
            TimelineBuilt = 4,
            TimelineFailed = 5,
        }

        public readonly struct Grant
        {
            public Grant(byte[] token, uint ttlSeconds, ushort eventsPerSecond, ushort burst)
            {
                Token = token;
                TtlSeconds = ttlSeconds;
                EventsPerSecond = eventsPerSecond;
                Burst = burst;
            }

            public byte[] Token { get; }
            public uint TtlSeconds { get; }
            public ushort EventsPerSecond { get; }
            public ushort Burst { get; }
        }

        public readonly struct Report
        {
            public Report(byte[] token, uint sequence, long clientMonotonicMs, uint msgId, int skillId, long sourceId,
                long targetId, long sessionId, long correlationId, int showPoint, int effectId, long startTimestampMs,
                Stage stage, bool success, string reason)
            {
                Token = token;
                Sequence = sequence;
                ClientMonotonicMs = clientMonotonicMs;
                MsgId = msgId;
                SkillId = skillId;
                SourceId = sourceId;
                TargetId = targetId;
                SessionId = sessionId;
                CorrelationId = correlationId;
                ShowPoint = showPoint;
                EffectId = effectId;
                StartTimestampMs = startTimestampMs;
                EventStage = stage;
                Success = success;
                Reason = CleanReason(reason);
            }

            public byte[] Token { get; }
            public uint Sequence { get; }
            public long ClientMonotonicMs { get; }
            public uint MsgId { get; }
            public int SkillId { get; }
            public long SourceId { get; }
            public long TargetId { get; }
            public long SessionId { get; }
            public long CorrelationId { get; }
            public int ShowPoint { get; }
            public int EffectId { get; }
            public long StartTimestampMs { get; }
            public Stage EventStage { get; }
            public bool Success { get; }
            public string Reason { get; }
        }

        public static byte[] EncodeGrant(byte[] token, uint ttlSeconds, ushort eventsPerSecond, ushort burst)
        {
            RequireToken(token);
            var bytes = new byte[GrantSize];
            bytes[0] = Version;
            Buffer.BlockCopy(token, 0, bytes, 1, TokenSize);
            WriteUInt32(bytes, 17, ttlSeconds);
            WriteUInt16(bytes, 21, eventsPerSecond);
            WriteUInt16(bytes, 23, burst);
            return bytes;
        }

        public static bool TryDecodeGrant(byte[] bytes, out Grant grant)
        {
            grant = default;
            if (bytes == null || bytes.Length != GrantSize || bytes[0] != Version) return false;
            var token = new byte[TokenSize];
            Buffer.BlockCopy(bytes, 1, token, 0, TokenSize);
            uint ttl = ReadUInt32(bytes, 17);
            ushort rate = ReadUInt16(bytes, 21), burst = ReadUInt16(bytes, 23);
            if (ttl == 0 || ttl > MaxGrantSeconds || rate == 0) return false;
            grant = new Grant(token, ttl, rate, burst);
            return true;
        }

        public static byte[] EncodeReport(in Report report)
        {
            RequireToken(report.Token);
            byte[] reason = Encoding.ASCII.GetBytes(CleanReason(report.Reason));
            var bytes = new byte[ReportHeaderSize + reason.Length];
            bytes[0] = Version;
            Buffer.BlockCopy(report.Token, 0, bytes, 1, TokenSize);
            WriteUInt32(bytes, 17, report.Sequence);
            WriteInt64(bytes, 21, report.ClientMonotonicMs);
            WriteUInt32(bytes, 29, report.MsgId);
            WriteInt32(bytes, 33, report.SkillId);
            WriteInt64(bytes, 37, report.SourceId);
            WriteInt64(bytes, 45, report.TargetId);
            WriteInt64(bytes, 53, report.SessionId);
            WriteInt64(bytes, 61, report.CorrelationId);
            WriteInt32(bytes, 69, report.ShowPoint);
            WriteInt32(bytes, 73, report.EffectId);
            WriteInt64(bytes, 77, report.StartTimestampMs);
            bytes[85] = (byte)report.EventStage;
            bytes[86] = report.Success ? (byte)1 : (byte)0;
            bytes[87] = (byte)reason.Length;
            Buffer.BlockCopy(reason, 0, bytes, ReportHeaderSize, reason.Length);
            return bytes;
        }

        public static bool TryDecodeReport(byte[] bytes, out Report report)
        {
            report = default;
            if (bytes == null || bytes.Length < ReportHeaderSize || bytes.Length > MaxReportSize || bytes[0] != Version)
                return false;
            int reasonLength = bytes[87];
            if (reasonLength > MaxReasonBytes || bytes.Length != ReportHeaderSize + reasonLength) return false;
            var stage = (Stage)bytes[85];
            if (stage < Stage.RouteReceived || stage > Stage.TimelineFailed || bytes[86] > 1) return false;
            uint msgId = ReadUInt32(bytes, 29);
            if (!IsSkillMessage(msgId)) return false;
            var token = new byte[TokenSize];
            Buffer.BlockCopy(bytes, 1, token, 0, TokenSize);
            string reason = Encoding.ASCII.GetString(bytes, ReportHeaderSize, reasonLength);
            report = new Report(token, ReadUInt32(bytes, 17), ReadInt64(bytes, 21), msgId,
                ReadInt32(bytes, 33), ReadInt64(bytes, 37), ReadInt64(bytes, 45), ReadInt64(bytes, 53),
                ReadInt64(bytes, 61), ReadInt32(bytes, 69), ReadInt32(bytes, 73), ReadInt64(bytes, 77),
                stage, bytes[86] != 0, reason);
            return true;
        }

        private static string CleanReason(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var result = new StringBuilder(Math.Min(value.Length, MaxReasonBytes));
            foreach (char c in value)
            {
                if (result.Length >= MaxReasonBytes) break;
                result.Append(c >= 0x20 && c <= 0x7e ? c : '?');
            }
            return result.ToString();
        }

        private static void RequireToken(byte[] token)
        {
            if (token == null || token.Length != TokenSize)
                throw new ArgumentException("Skill trace token must be 16 bytes.", nameof(token));
        }

        private static void WriteUInt16(byte[] bytes, int at, ushort value)
        {
            bytes[at] = (byte)value;
            bytes[at + 1] = (byte)(value >> 8);
        }

        private static ushort ReadUInt16(byte[] bytes, int at) => (ushort)(bytes[at] | bytes[at + 1] << 8);

        private static void WriteUInt32(byte[] bytes, int at, uint value)
        {
            for (int i = 0; i < 4; i++) bytes[at + i] = (byte)(value >> (8 * i));
        }

        private static uint ReadUInt32(byte[] bytes, int at)
        {
            uint value = 0;
            for (int i = 0; i < 4; i++) value |= (uint)bytes[at + i] << (8 * i);
            return value;
        }

        private static void WriteInt32(byte[] bytes, int at, int value) => WriteUInt32(bytes, at, unchecked((uint)value));
        private static int ReadInt32(byte[] bytes, int at) => unchecked((int)ReadUInt32(bytes, at));

        private static void WriteInt64(byte[] bytes, int at, long value)
        {
            ulong unsigned = unchecked((ulong)value);
            for (int i = 0; i < 8; i++) bytes[at + i] = (byte)(unsigned >> (8 * i));
        }

        private static long ReadInt64(byte[] bytes, int at)
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++) value |= (ulong)bytes[at + i] << (8 * i);
            return unchecked((long)value);
        }
    }

    /// <summary>Client-side copy of the grant's token bucket, driven by Unity's monotonic clock.</summary>
    internal sealed class SkillTraceReportLimiter
    {
        private readonly double _capacity;
        private readonly double _eventsPerSecond;
        private double _tokens;
        private long _lastRefillMs;

        internal SkillTraceReportLimiter(ushort eventsPerSecond, ushort burst, long nowMs)
        {
            if (eventsPerSecond == 0) throw new ArgumentOutOfRangeException(nameof(eventsPerSecond));
            _eventsPerSecond = eventsPerSecond;
            _capacity = eventsPerSecond + (double)burst;
            _tokens = _capacity;
            _lastRefillMs = nowMs;
        }

        internal bool TryTake(long nowMs)
        {
            if (nowMs > _lastRefillMs)
            {
                _tokens = Math.Min(_capacity, _tokens + (nowMs - _lastRefillMs) / 1000d * _eventsPerSecond);
                _lastRefillMs = nowMs;
            }
            if (_tokens < 1) return false;
            _tokens--;
            return true;
        }
    }
}
