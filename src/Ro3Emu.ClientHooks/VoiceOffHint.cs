namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// When Talk shows "voice chat is off on this server", kept apart from Unity so the rule is testable (the server test
    /// project compiles this file too). With the relay off the menu still offers Talk, but nothing would visibly happen:
    /// the ticket answers port 0 and Talk falls back to Listen. Only a mic-on request shows the hint (joining the channel
    /// mutes the mic by itself, which must stay silent), only after the server answered, and only until logout (the next
    /// login may reach a server with voice on). Main thread only.
    /// </summary>
    internal sealed class VoiceOffHint
    {
        private bool _relayOff;

        /// <summary>The server's ticket: port 0 means the relay is off.</summary>
        public void OnTicket(bool relayOff) => _relayOff = relayOff;

        /// <summary>Logout: forget the answer.</summary>
        public void Reset() => _relayOff = false;

        /// <summary>True when this mic change (Talk = mute false) should show the hint.</summary>
        public bool ShowOnMicChange(bool mute) => _relayOff && !mute;
    }
}
