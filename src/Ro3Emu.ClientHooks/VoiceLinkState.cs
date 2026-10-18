namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// How a voice relay link ended, kept apart from the socket so the rule is testable without Unity (the server test
    /// project compiles this file too). A link ends exactly once, either because the game asked (<see cref="RequestClose"/>:
    /// a leave, a channel switch, logout) or because something failed (<see cref="Fail"/>: a write or read error, EOF,
    /// a Kicked frame, an overflowing queue), whichever comes first and on whichever thread. Only a failure has to reach
    /// the main thread, and only once (<see cref="TakeFailureNotice"/>), so its usual cleanup runs: forget the link,
    /// stop capture and playback, tell Lua the channel is gone.
    /// </summary>
    internal sealed class VoiceLinkState
    {
        private enum End { Open, Requested, Failed }

        private readonly object _sync = new object();
        private End _end;
        private bool _noticeTaken;
        private string _reason = "";

        /// <summary>True once the link ended for any reason: nothing more is queued or sent.</summary>
        public bool Closed { get { lock (_sync) return _end != End.Open; } }

        /// <summary>True when it ended through <see cref="Fail"/>.</summary>
        public bool HasFailed { get { lock (_sync) return _end == End.Failed; } }

        /// <summary>Why it failed ("" unless <see cref="HasFailed"/>).</summary>
        public string Reason { get { lock (_sync) return _reason; } }

        /// <summary>The game ends the link; true when this call ended it (the caller then closes the transport). No
        /// notice follows: the game already knows.</summary>
        public bool RequestClose()
        {
            lock (_sync)
            {
                if (_end != End.Open) return false;
                _end = End.Requested;
                return true;
            }
        }

        /// <summary>Something failed; true when this call ended the link (the caller then closes the transport). A link
        /// the game already closed stays a requested close: the error is only the consequence of that close.</summary>
        public bool Fail(string reason)
        {
            lock (_sync)
            {
                if (_end != End.Open) return false;
                _end = End.Failed;
                _reason = reason ?? "";
                return true;
            }
        }

        /// <summary>True exactly once, for the first caller after a failure: that caller tells the main thread.</summary>
        public bool TakeFailureNotice()
        {
            lock (_sync)
            {
                if (_end != End.Failed || _noticeTaken) return false;
                _noticeTaken = true;
                return true;
            }
        }
    }
}
