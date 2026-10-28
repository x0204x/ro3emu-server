namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Message ids private to RO3Emu's patched client/server bridge. The 0x7F52 namespace is below Int32.MaxValue and
    /// outside the complete Asia/Genesis protobuf, EZ protobuf and AutoSync client catalogs checked on 28 October
    /// 2026. Keep every emulator-only id here so a client catalog update has one collision gate.
    /// </summary>
    public static class PrivateMessageIds
    {
        public const uint ChatTunnelUp = 0x7F52_0001;
        public const uint ChatTunnelDown = 0x7F52_0002;
        public const uint Diagnostics = 0x7F52_0003;
        public const uint Feedback = 0x7F52_0004;
        public const uint PassChallenges = 0x7F52_0005;
        public const uint VoiceTicketRequest = 0x7F52_0006;
        public const uint VoiceTicket = 0x7F52_0007;
        public const uint SkillTraceReport = 0x7F52_0008;
        public const uint SkillTraceGrant = 0x7F52_0009;

        public static readonly uint[] All =
        {
            ChatTunnelUp, ChatTunnelDown, Diagnostics, Feedback, PassChallenges,
            VoiceTicketRequest, VoiceTicket, SkillTraceReport, SkillTraceGrant,
        };
    }
}
