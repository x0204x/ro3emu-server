using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Threading;
using HappyEngine.Base;
using NetworkLib;
using Debug = UnityEngine.Debug;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Team voice through the server's voice relay instead of Tencent GME. The patched NetworkLib.VoiceCallAdapter
    /// (ClientTools VoiceStubPatch) keeps its own join/leave bookkeeping and Lua OnJoinChannel/OnLeaveChannel, and also
    /// calls the static methods here with the same arguments as Lua gave it.
    /// <list type="bullet">
    /// <item>JoinChannel (team channels only: the team id in decimal) asks the game server for a ticket (77015), connects
    /// to the relay the ticket names (77016), authenticates and joins. One connection; Leave closes it.</item>
    /// <item>SetMicrophoneMute / SetSpeakerMute become Mic / Speaker on the relay and start or stop capture and playback.
    /// The relay echoes MicState to the sender too, and that echo is what turns on the client's Talk state.</item>
    /// <item>Relay events become the VoiceCall_Util callbacks GME used to fire: OnMemberChange, OnMemberMicMute
    /// (a mute flag: mic on is false), OnMemberVad, and OnLeaveChannel when the relay drops us.</item>
    /// </list>
    /// Threads: the adapter calls and the pump run on the main thread and own all state here; the socket reader posts its
    /// events into a bounded queue the pump drains (Lua is only ever called from there); received audio goes from the
    /// reader thread straight into the playback mixer. Nothing writes to the socket but the link's own writer thread:
    /// the main and capture threads only queue frames, so a stalled network never blocks the game.
    /// </summary>
    public static class VoiceClient
    {
        private const int ConnectTimeoutMs = 5000;
        private const long PingEveryMs = 10000;
        private const int MaxPerFrame = 50;
        /// <summary>Relay events waiting for the pump; a relay that floods past this is dropped.</summary>
        private const int MaxPending = 512;
        /// <summary>No frame from the relay this long (it answers the 10 s ping) ends the link.</summary>
        private const int ReceiveTimeoutMs = 30000;
        /// <summary>A ticket request without an answer (a server without voice support) may be repeated after this.</summary>
        private const long TicketRetryMs = 10000;
        /// <summary>A link that dropped while in a channel reconnects after this, doubling up to RejoinMaxMs.</summary>
        private const int RejoinFirstMs = 2000, RejoinMaxMs = 30000;

        private static readonly ConcurrentQueue<Action> Main = new ConcurrentQueue<Action>();
        private static int _pending;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private static string _userId;
        private static string _wanted;     // the channel Lua asked for
        private static string _joined;     // the channel the relay confirmed
        private static bool _micOn, _speakerOn;
        private static long _ticketAskedAt = -TicketRetryMs;
        private static long _rejoinAt; // 0: no reconnect scheduled
        private static int _rejoinDelay = RejoinFirstMs;
        private static long _lastPing;
        private static Link _link;
        /// <summary>The link captured frames go to: set while joined with the mic on (read by the capture thread).</summary>
        private static volatile Link _audioLink;
        private static int _audioSeq;
        private static long _selfId; // also read by the reader thread: Interlocked
        private const long StatsEveryMs = 10000;
        private static long _lastStats;
        // 10 s stats line while joined (Interlocked: capture, writer and reader threads count).
        private static int _captured, _peak, _linkDropped, _heard;
        private static string _lastNote;
        private static readonly VoiceOffHint OffHint = new VoiceOffHint();
        private const string OffHintText = "Voice chat is off on this server.";

        // ---- Called by the patched VoiceCallAdapter (main thread). Nothing may throw back into the game. ----

        public static void Init(string userId)
        {
            Guard("Init", () =>
            {
                _userId = userId;
                Note("init as " + userId);
            });
        }

        public static void JoinChannel(string channel)
        {
            Guard("JoinChannel", () =>
            {
                if (!IsServedChannel(channel)) { Note("not a party or guild channel, ignored: " + channel); return; }
                if (_wanted != null && _wanted != channel) LeaveCurrent();
                _wanted = channel;
                if (_link != null && _link.Authed) { _link.Send(VoiceProtocol.ChannelFrame(VoiceProtocol.Join, channel)); return; }
                if (_link != null) return; // the join goes out once authenticated
                if (Clock.ElapsedMilliseconds - _ticketAskedAt < TicketRetryMs) return;
                RequestTicket("join " + channel);
            });
        }

        private static void RequestTicket(string why)
        {
            _ticketAskedAt = Clock.ElapsedMilliseconds;
            SingletonMonoBehaviour<NW_CBBNetworkManager>.Instance.Obf_jBd(VoiceProtocol.TicketRequestMsgId, new byte[0]);
            Debug.Log("[Ro3Emu.Voice] " + why + ": ticket requested");
        }

        public static void LeaveChannel(string channel)
        {
            Guard("LeaveChannel", () => { if (channel != null && channel == _wanted) LeaveCurrent(); });
        }

        public static void LeaveChannelAll()
        {
            Guard("LeaveChannelAll", () => { if (_wanted != null) LeaveCurrent(); });
        }

        public static void SetMicrophoneMute(string channel, bool mute)
        {
            Guard("SetMicrophoneMute", () =>
            {
                if (channel == null || channel != _wanted) return;
                if (OffHint.ShowOnMicChange(mute)) ShowTip(OffHintText);
                _micOn = !mute;
                if (_joined != null) _link?.Send(VoiceProtocol.SwitchFrame(VoiceProtocol.Mic, _micOn));
                UpdateCapture();
            });
        }

        public static void SetSpeakerMute(string channel, bool mute)
        {
            Guard("SetSpeakerMute", () =>
            {
                if (channel == null || channel != _wanted) return;
                _speakerOn = !mute;
                if (_joined != null) _link?.Send(VoiceProtocol.SwitchFrame(VoiceProtocol.Speaker, _speakerOn));
                UpdatePlayback();
            });
        }

        /// <summary>GME volume, 0..200 with 100 = unchanged (assumed the same scale here). Volume is a device setting,
        /// the same in every channel, so the channel is not checked.</summary>
        public static void SetSpeakerVolume(string channel, int volume) => Guard("SetSpeakerVolume", () => VoiceAudio.SpeakerGain = Gain(volume));

        public static void SetMicVolume(string channel, int volume) => Guard("SetMicVolume", () => VoiceAudio.MicGain = Gain(volume));

        /// <summary>The game connection's IM adapter is torn down (logout, back to the login screen): voice ends with it,
        /// whether or not Lua also leaves the channel.</summary>
        public static void Shutdown()
        {
            Guard("Shutdown", () =>
            {
                OffHint.Reset();
                if (_wanted == null && _link == null) return;
                Debug.Log("[Ro3Emu.Voice] game connection closed: voice off");
                _wanted = null;
                _rejoinAt = 0;
                _rejoinDelay = RejoinFirstMs;
                Drop();
            });
        }

        // ---- From the game connection and the frame pump. ----

        /// <summary>77016 from the game connection's receive callback (any thread).</summary>
        public static void OnTicket(byte[] body) => Post(() => UseTicket(VoiceProtocol.ParseTicket(body)));

        /// <summary>Every frame from ServerImAdapter's pump: relay events into Lua, keep-alive.</summary>
        public static void Pump()
        {
            int n = 0;
            while (n++ < MaxPerFrame && Main.TryDequeue(out var action))
            {
                Interlocked.Decrement(ref _pending);
                try { action(); }
                catch (Exception e) { Debug.LogError("[Ro3Emu.Voice] " + e); }
            }
            if (_rejoinAt > 0 && Clock.ElapsedMilliseconds >= _rejoinAt) Rejoin();
            if (_joined != null) VoiceSettings.Poll(Clock.ElapsedMilliseconds);
            if (_joined != null && Clock.ElapsedMilliseconds - _lastStats >= StatsEveryMs) LogStats();
            var link = _link;
            if (link != null && link.Authed && Clock.ElapsedMilliseconds - _lastPing >= PingEveryMs)
            {
                _lastPing = Clock.ElapsedMilliseconds;
                link.Send(VoiceProtocol.Frame(VoiceProtocol.Ping, null));
            }
        }

        // ---- Main thread. ----

        private static void UseTicket(VoiceProtocol.Ticket ticket)
        {
            _ticketAskedAt = -TicketRetryMs;
            if (ticket == null) { Debug.LogWarning("[Ro3Emu.Voice] malformed ticket"); return; }
            OffHint.OnTicket(ticket.Port == 0);
            if (ticket.Port == 0) { _rejoinAt = 0; Note("voice is off on this server"); return; }
            if (string.IsNullOrEmpty(ticket.Host)) { Debug.LogWarning("[Ro3Emu.Voice] the ticket names no relay host"); return; }
            if (_wanted == null || _link != null) return; // left meanwhile, or already connected
            if (_userId != ticket.RoleId.ToString(CultureInfo.InvariantCulture)) Debug.LogWarning("[Ro3Emu.Voice] ticket for role " + ticket.RoleId + ", voice user " + _userId);
            Interlocked.Exchange(ref _selfId, ticket.RoleId);
            _link = new Link(ticket);
            _link.Start();
            Debug.Log("[Ro3Emu.Voice] connecting to " + ticket.Host + ":" + ticket.Port);
        }

        private static void OnAuthed(Link link)
        {
            if (link != _link) return;
            Debug.Log("[Ro3Emu.Voice] authenticated");
            _lastPing = Clock.ElapsedMilliseconds;
            if (_wanted != null) link.Send(VoiceProtocol.ChannelFrame(VoiceProtocol.Join, _wanted));
        }

        private static void OnJoinResult(Link link, int code, string channel)
        {
            if (link != _link) return;
            if (code != 0)
            {
                Debug.LogWarning("[Ro3Emu.Voice] join " + channel + " refused: " + JoinCode(code));
                return;
            }
            if (channel != _wanted)
            {
                link.Send(VoiceProtocol.ChannelFrame(VoiceProtocol.Leave, channel)); // a join that was overtaken
                return;
            }
            _joined = channel;
            _rejoinAt = 0;
            _rejoinDelay = RejoinFirstMs;
            Debug.Log("[Ro3Emu.Voice] joined " + channel);
            link.Send(VoiceProtocol.SwitchFrame(VoiceProtocol.Speaker, _speakerOn));
            link.Send(VoiceProtocol.SwitchFrame(VoiceProtocol.Mic, _micOn));
            UpdatePlayback();
            UpdateCapture();
        }

        private static void OnRoleEvent(Link link, byte type, long roleId, bool flag)
        {
            if (link != _link || _joined == null) return;
            string uid = roleId.ToString(CultureInfo.InvariantCulture);
            var members = new[] { uid };
            switch (type)
            {
                case VoiceProtocol.MemberChange:
                    if (!flag) VoiceAudio.Forget(roleId);
                    CallLua("OnMemberChange", _joined, uid, flag, 1, members);
                    break;
                case VoiceProtocol.MicState:
                    CallLua("OnMemberMicMute", _joined, uid, !flag, 1, members);
                    break;
                case VoiceProtocol.Vad:
                    CallLua("OnMemberVad", _joined, uid, flag, 1, members);
                    break;
            }
        }

        /// <summary>
        /// The relay connection ended without the game asking (relay restart, network, a kick): audio off, then back into
        /// the same channel with a new ticket after a growing pause, keeping the player's mic and speaker choices. Lua is not
        /// told anything: OnLeaveChannel only answers the game's own LeaveChannel (it reads the callback that call
        /// registered), and the game still means to be in the channel. A connection of the same account that took over
        /// (kick code 1) is not fought over.
        /// </summary>
        private static void OnClosed(Link link, string why)
        {
            if (link != _link) return;
            Debug.Log("[Ro3Emu.Voice] disconnected: " + why);
            bool mic = _micOn, speaker = _speakerOn;
            Drop();
            _micOn = mic;
            _speakerOn = speaker;
            if (_wanted == null) return;
            if (why.StartsWith("kicked (code 1)", StringComparison.Ordinal))
            {
                Note("another voice connection of this account took over");
                _wanted = null;
                return;
            }
            ScheduleRejoin();
        }

        private static void ScheduleRejoin()
        {
            _rejoinAt = Clock.ElapsedMilliseconds + _rejoinDelay;
            Debug.Log("[Ro3Emu.Voice] reconnecting to " + _wanted + " in " + _rejoinDelay / 1000 + " s");
            _rejoinDelay = Math.Min(_rejoinDelay * 2, RejoinMaxMs);
        }

        /// <summary>Due reconnect: a new ticket, and the next attempt already scheduled in case no ticket or link comes.</summary>
        private static void Rejoin()
        {
            _rejoinAt = 0;
            if (_wanted == null || _link != null) return;
            RequestTicket("rejoin " + _wanted);
            ScheduleRejoin();
        }

        private static void LeaveCurrent()
        {
            var link = _link;
            if (link != null && link.Authed && _wanted != null) link.Send(VoiceProtocol.ChannelFrame(VoiceProtocol.Leave, _wanted));
            Debug.Log("[Ro3Emu.Voice] leave " + _wanted);
            _wanted = null;
            _rejoinAt = 0;
            _rejoinDelay = RejoinFirstMs;
            Drop();
        }

        /// <summary>Closes the link and stops audio; the next JoinChannel starts again with a new ticket.</summary>
        private static void Drop()
        {
            _joined = null;
            _micOn = _speakerOn = false;
            UpdateCapture();
            UpdatePlayback();
            var link = _link;
            _link = null;
            link?.Close();
        }

        private static void UpdateCapture()
        {
            bool on = _joined != null && _micOn && _link != null;
            _audioLink = on ? _link : null;
            if (on) VoiceAudio.StartCapture(SendFrame);
            else VoiceAudio.StopCapture();
        }

        private static void UpdatePlayback()
        {
            if (_joined != null && _speakerOn) VoiceAudio.StartPlayback();
            else VoiceAudio.StopPlayback();
        }

        // ---- Other threads. ----

        /// <summary>Capture thread: one 20 ms frame, mu-law, to the relay.</summary>
        private static readonly byte[] Encoded = new byte[VoiceAudio.FrameSamples];

        private static void SendFrame(short[] pcm)
        {
            var link = _audioLink;
            if (link == null) return;
            Interlocked.Increment(ref _captured);
            int peak = VoiceAudio.Agc.Process(pcm); // raw peak, before the automatic gain
            if (peak > _peak) Interlocked.Exchange(ref _peak, peak); // racy max is fine for a log line
            MuLaw.Encode(pcm, pcm.Length, Encoded, 0);
            link.SendAudio(VoiceProtocol.AudioFrame((ushort)Interlocked.Increment(ref _audioSeq), VoiceProtocol.CodecMuLaw, Encoded, Encoded.Length));
        }

        /// <summary>Reader thread: a received audio frame goes to the mixer (never our own voice).</summary>
        private static void OnAudio(byte[] payload)
        {
            if (!VoiceProtocol.ParseAudio(payload, out long speaker, out _, out byte codec, out int at) || speaker == Interlocked.Read(ref _selfId)) return;
            if (codec != VoiceProtocol.CodecMuLaw) { Post(() => Note("unknown audio codec " + codec)); return; }
            if (payload.Length - at != VoiceAudio.FrameSamples) return;
            Interlocked.Increment(ref _heard);
            VoiceAudio.Push(speaker, MuLaw.Decode(payload, at, VoiceAudio.FrameSamples));
        }

        // ---- Helpers. ----

        /// <summary>One line per 10 s while joined, for diagnosing choppy or late audio: frames the mic produced and its
        /// loudest sample (0..32767; under ~300 the mic hears almost nothing), frames dropped before sending, frames
        /// heard, and playback buffers that ran dry or overflowed. Expect about 500 frames per 10 s each way.</summary>
        private static void LogStats()
        {
            _lastStats = Clock.ElapsedMilliseconds;
            int captured = Interlocked.Exchange(ref _captured, 0), peak = Interlocked.Exchange(ref _peak, 0);
            int dropped = Interlocked.Exchange(ref _linkDropped, 0), heard = Interlocked.Exchange(ref _heard, 0);
            int dry = Interlocked.Exchange(ref VoiceAudio.Underruns, 0), over = Interlocked.Exchange(ref VoiceAudio.Overflows, 0);
            if (captured == 0 && heard == 0 && dry == 0) return;
            Debug.Log("[Ro3Emu.Voice] stats 10s: mic " + captured + " frames (peak " + peak + ", auto gain x"
                + VoiceAudio.Agc.Gain.ToString("0.0", CultureInfo.InvariantCulture) + "), not sent " + dropped + ", heard " + heard
                + ", playback ran dry " + dry + "x, overflow " + over + ", volume " + (int)(VoiceAudio.OutputScale * 100) + "%");
        }

        /// <summary>Queues work for the main thread; false (nothing queued) once <see cref="MaxPending"/> wait.</summary>
        private static bool Post(Action action)
        {
            if (Interlocked.Increment(ref _pending) > MaxPending)
            {
                Interlocked.Decrement(ref _pending);
                return false;
            }
            Main.Enqueue(action);
            return true;
        }

        /// <summary>VoiceCall_Util[name](args...), as the adapter's own callback path does (LA_LuaManager.Obf_OD).</summary>
        private static void CallLua(string name, params object[] args)
        {
            var manager = SingletonMonoBehaviour<LA_LuaManager>.Instance;
            if (manager == null) return;
            var util = manager.Obf_jD()?.Obf_A?.Obf_RuA<Obf_R.Obf_ff>("VoiceCall_Util");
            if (util == null) { Note("VoiceCall_Util not loaded; " + name + " dropped"); return; }
            manager.Obf_OD(util, name, args);
        }

        /// <summary>The game's own short pop-up, LC_TipHelper.PopCommonTips(text) (it skips a repeat within 3 s).</summary>
        private static void ShowTip(string text)
        {
            var manager = SingletonMonoBehaviour<LA_LuaManager>.Instance;
            if (manager == null) return;
            var tips = manager.Obf_jD()?.Obf_A?.Obf_RuA<Obf_R.Obf_ff>("LC_TipHelper");
            if (tips == null) { Note("LC_TipHelper not loaded; hint dropped"); return; }
            manager.Obf_OD(tips, "PopCommonTips", text);
        }

        /// <summary>What the relay serves: a party (its team id) or a guild voice channel "2_&lt;guildId&gt;_&lt;index&gt;"
        /// (LC_VoiceHelper.GetGuildVoiceChannelId); the relay checks the rest (Ro3Emu.Server.Voice.VoiceRoomKey).</summary>
        private static bool IsServedChannel(string channel)
        {
            if (IsDigits(channel)) return true;
            var parts = channel?.Split('_');
            return parts != null && parts.Length == 3 && parts[0] == "2" && IsDigits(parts[1]) && IsDigits(parts[2]);
        }

        private static bool IsDigits(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
                if (c < '0' || c > '9') return false;
            return true;
        }

        private static float Gain(int volume) => Math.Max(0, Math.Min(200, volume)) / 100f;

        private static string JoinCode(int code)
        {
            switch (code)
            {
                case 1: return "not a member";
                case 2: return "unsupported channel";
                case 3: return "channel full";
                case 4: return "not authenticated";
                default: return "code " + code;
            }
        }

        private static void Guard(string what, Action action)
        {
            try { action(); }
            catch (Exception e) { Debug.LogError("[Ro3Emu.Voice] " + what + ": " + e); }
        }

        /// <summary>Logs a message unless it repeats the previous one.</summary>
        private static void Note(string message)
        {
            if (message == _lastNote) return;
            _lastNote = message;
            Debug.Log("[Ro3Emu.Voice] " + message);
        }

        /// <summary>
        /// One TCP connection to the relay: connect and read on one thread, every write on another. Send/SendAudio only
        /// queue (control frames bounded, audio dropping its oldest), so no caller ever waits on the network. Every frame
        /// but Auth is sealed with the ticket's secret by the writer (one thread, so the counters follow the wire order)
        /// and every frame read is opened by the reader; one that fails to open ends the link.
        /// </summary>
        private sealed class Link
        {
            private const int MaxControl = 64;
            private const int MaxAudio = 8; // 160 ms; older frames are stale anyway
            private const int WriterJoinMs = 2000;

            private readonly VoiceProtocol.Ticket _ticket;
            private readonly object _queue = new object();
            private readonly Queue<byte[]> _control = new Queue<byte[]>();
            private readonly Queue<byte[]> _audio = new Queue<byte[]>();
            private TcpClient _tcp;
            private NetworkStream _stream;
            private Thread _writer;
            /// <summary>Requested close vs failure: only a failure is reported to the main thread, exactly once.</summary>
            private readonly VoiceLinkState _state = new VoiceLinkState();
            private readonly VoiceCipher _cipher;
            public volatile bool Authed;

            public Link(VoiceProtocol.Ticket ticket)
            {
                _ticket = ticket;
                _cipher = VoiceCipher.ForClient(ticket.Secret);
            }

            public void Start() => new Thread(Run) { IsBackground = true, Name = "Ro3Emu voice" }.Start();

            /// <summary>Queues a control frame; a link that cannot drain its control frames fails.</summary>
            public void Send(byte[] frame)
            {
                lock (_queue)
                {
                    if (_state.Closed) return;
                    if (_control.Count >= MaxControl) { Fail("the relay is not taking our frames"); return; }
                    _control.Enqueue(frame);
                    Monitor.Pulse(_queue);
                }
            }

            /// <summary>Queues a captured audio frame; the oldest queued one goes first when the network lags.</summary>
            public void SendAudio(byte[] frame)
            {
                lock (_queue)
                {
                    if (_state.Closed) return;
                    if (_audio.Count >= MaxAudio) { _audio.Dequeue(); Interlocked.Increment(ref _linkDropped); }
                    _audio.Enqueue(frame);
                    Monitor.Pulse(_queue);
                }
            }

            /// <summary>The game ends the link (main thread): no OnClosed follows, the caller does its own cleanup.</summary>
            public void Close()
            {
                if (_state.RequestClose()) Shut();
            }

            /// <summary>
            /// Something broke (any thread): the link ends and the main thread is told once, whichever thread failed first,
            /// so OnClosed forgets the link, stops audio and tells Lua. Before, a failed write closed the link as if the
            /// game had asked, the reader then took it for a local close, and the dead link stayed current.
            /// </summary>
            private void Fail(string why)
            {
                if (_state.Fail(why)) Shut();
                if (!_state.TakeFailureNotice()) return;
                // Queued even past MaxPending: the pump must learn the link is gone.
                Interlocked.Increment(ref _pending);
                Main.Enqueue(() => OnClosed(this, _state.Reason));
            }

            private void Shut()
            {
                lock (_queue) Monitor.PulseAll(_queue); // the writer sees Closed and ends
                try { _tcp?.Close(); } catch (Exception) { }
            }

            /// <summary>The only thread that writes to the socket; control frames go ahead of audio.</summary>
            private void Write()
            {
                try
                {
                    while (true)
                    {
                        byte[] frame;
                        lock (_queue)
                        {
                            while (!_state.Closed && _control.Count == 0 && _audio.Count == 0) Monitor.Wait(_queue);
                            if (_state.Closed) return;
                            frame = _control.Count > 0 ? _control.Dequeue() : _audio.Dequeue();
                        }
                        if (frame[2] != VoiceProtocol.Auth) frame = Seal(frame);
                        _stream.Write(frame, 0, frame.Length);
                    }
                }
                catch (Exception e) { Fail("send failed: " + e.GetType().Name + ": " + e.Message); }
            }

            /// <summary>The same frame with its payload sealed (writer thread only).</summary>
            private byte[] Seal(byte[] frame)
            {
                var payload = new byte[frame.Length - VoiceProtocol.HeaderSize];
                Buffer.BlockCopy(frame, VoiceProtocol.HeaderSize, payload, 0, payload.Length);
                return VoiceProtocol.Frame(frame[2], _cipher.Seal(frame[2], payload));
            }

            private void Run()
            {
                string why = "closed";
                try
                {
                    _tcp = new TcpClient { NoDelay = true, SendTimeout = 5000, ReceiveTimeout = ReceiveTimeoutMs };
                    // A bracketed IPv6 literal (SdkEndpoints.GateHost) is unwrapped, as the bot client does.
                    var connecting = _tcp.BeginConnect(_ticket.Host.Trim('[', ']'), _ticket.Port, null, null);
                    if (!connecting.AsyncWaitHandle.WaitOne(ConnectTimeoutMs)) throw new TimeoutException("connect timed out");
                    _tcp.EndConnect(connecting);
                    _stream = _tcp.GetStream();
                    if (!_state.Closed)
                    {
                        _writer = new Thread(Write) { IsBackground = true, Name = "Ro3Emu voice send" };
                        _writer.Start();
                        Send(VoiceProtocol.AuthFrame(_ticket.RoleId, _ticket.Token, _ticket.Secret));
                        var header = new byte[VoiceProtocol.HeaderSize];
                        while (!_state.Closed)
                        {
                            if (!ReadFully(header)) break;
                            int length = VoiceProtocol.PayloadLength(header);
                            if (length < 0) { why = "bad frame"; break; }
                            var sealedPayload = new byte[length];
                            if (!ReadFully(sealedPayload)) break;
                            byte[] payload = _cipher.Open(header[2], sealedPayload);
                            if (payload == null) { why = "a frame failed authentication"; break; }
                            if (!Handle(header[2], payload, ref why)) break;
                        }
                    }
                }
                catch (Exception e)
                {
                    why = e.GetType().Name + ": " + e.Message;
                }
                // The reader is done, so the link is too. A no-op after the game's own Close (that is not news) or after
                // the writer already failed (that failure was reported, once).
                Fail(why);
                // Only once neither thread can still seal or open: the reader is this thread, the writer ends on Closed
                // (a write blocked on the closed socket throws at once). A writer still stuck after that is left to the GC.
                if (_writer == null || _writer.Join(WriterJoinMs)) _cipher.Dispose();
            }

            /// <summary>One received frame; false ends the connection.</summary>
            private bool Handle(byte type, byte[] payload, ref string why)
            {
                switch (type)
                {
                    case VoiceProtocol.AuthOk:
                        Authed = true;
                        return Post(() => OnAuthed(this)) || Flooded(ref why);
                    case VoiceProtocol.JoinResult:
                        if (VoiceProtocol.ParseJoinResult(payload, out int code, out string channel))
                            return Post(() => OnJoinResult(this, code, channel)) || Flooded(ref why);
                        return true;
                    case VoiceProtocol.MemberChange:
                    case VoiceProtocol.MicState:
                    case VoiceProtocol.Vad:
                        if (VoiceProtocol.ParseRoleFlag(payload, out long roleId, out bool flag))
                            return Post(() => OnRoleEvent(this, type, roleId, flag)) || Flooded(ref why);
                        return true;
                    case VoiceProtocol.AudioDown:
                        OnAudio(payload);
                        return true;
                    case VoiceProtocol.Kicked:
                        why = "kicked (code " + (payload.Length > 0 ? payload[0] : -1) + ")";
                        return false;
                    default:
                        return true; // Pong and anything newer
                }
            }

            private static bool Flooded(ref string why)
            {
                why = "the relay sent more events than the game can take";
                return false;
            }

            private bool ReadFully(byte[] buffer)
            {
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = _stream.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) return false;
                    read += n;
                }
                return true;
            }
        }
    }
}
