using Microsoft.Extensions.Options;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Voice;

/// <summary>Join() outcome: the accepted/refused code and, on success, the room's members before the newcomer, the
/// members of the room it left (a switch) and the newcomer's own mic as the room sees it, for the caller to notify.
/// <see cref="AlreadyIn"/>: a repeated Join of the room the connection is already in, which changes nothing and
/// notifies nobody.</summary>
public readonly record struct VoiceJoinOutcome(byte Code, VoiceConnection[] ExistingMembers, VoiceConnection[] PreviousRoomOthers,
    bool MicOn = false, bool AlreadyIn = false);

/// <summary>SetMic() outcome. <see cref="Changed"/>: the mic the room sees flipped to <see cref="On"/>, and
/// <see cref="Members"/> (the whole room, sender included) are to be told, plus Vad speaking=0 when
/// <see cref="StoppedSpeaking"/>. <see cref="Refused"/>: a mic-on the leader's mute overrides, answered to the sender
/// alone with MicState off. Neither: an unchanged state, nothing to send.</summary>
public readonly record struct VoiceMicOutcome(VoiceConnection[] Members, bool StoppedSpeaking, bool On, bool Changed, bool Refused);

/// <summary>
/// The voice relay's shared state: who is authed, who is in which room, mic/speaker flags, and the VAD and
/// membership-revocation sweep. Everything here is guarded by one lock: connections mutate it from their own
/// socket-reading tasks, and <see cref="Tick"/> (the game loop) reads <see cref="TeamService"/> and
/// <see cref="SessionRegistry"/> once a second to revoke stale membership, so the lock has to be safe to take
/// from either side. Never touches <see cref="VoiceConnection"/>'s socket directly; it only calls the Send*
/// helpers, which queue.
/// </summary>
public sealed class VoiceRooms(IOptions<ServerOptions> options, TeamService teams, GuildVoiceService guildVoice,
    SessionRegistry sessions, ILogger<VoiceRooms> log) : IGameTicker
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan VadSilence = TimeSpan.FromMilliseconds(300);

    private readonly object _sync = new();
    // Ordinal: the account's stored spelling is its identity, and a legacy database can hold "admin" and "Admin" as
    // two accounts, which must not evict each other's voice connection.
    private readonly Dictionary<string, VoiceConnection> _byAccount = new(StringComparer.Ordinal);
    private readonly Dictionary<long, HashSet<VoiceConnection>> _rooms = new();
    private TimeSpan _sinceSweep;

    /// <summary>The account's authed voice connection, or null.</summary>
    public VoiceConnection? ConnectionOf(string account) { lock (_sync) return _byAccount.GetValueOrDefault(account); }

    /// <summary>Registers an authed connection under its account, replacing (and returning) whatever connection
    /// that account already held: one voice connection per account.</summary>
    public VoiceConnection? Authenticate(string account, long roleId, VoiceConnection connection)
    {
        lock (_sync)
        {
            connection.Account = account;
            connection.RoleId = roleId;
            connection.Authed = true;
            _byAccount.TryGetValue(account, out var previous);
            _byAccount[account] = connection;
            return ReferenceEquals(previous, connection) ? null : previous;
        }
    }

    /// <summary>Puts the connection in <paramref name="roomId"/>. <paramref name="chatForbidden"/> is the leader's mute
    /// as the game loop saw it while validating the join: a muted role enters with its mic off whatever it asked for.</summary>
    public VoiceJoinOutcome Join(VoiceConnection connection, long roomId, bool chatForbidden)
    {
        lock (_sync)
        {
            _rooms.TryGetValue(roomId, out var members);
            if (connection.RoomId == roomId && members != null && members.Contains(connection))
                return new VoiceJoinOutcome(VoiceJoinCode.Ok, [], [], connection.MicOn, AlreadyIn: true);
            // A full room refuses before the connection leaves the room it is in.
            if (members != null && members.Count >= options.Value.Voice.MaxRoomSize) return new VoiceJoinOutcome(VoiceJoinCode.Full, [], []);
            var previousOthers = OthersLocked(connection);
            LeaveLocked(connection);
            if (members == null) _rooms[roomId] = members = new();
            var existing = members.ToArray();
            members.Add(connection);
            connection.RoomId = roomId;
            connection.ChatForbidden = chatForbidden;
            connection.MicOn = connection.MicWanted && !chatForbidden;
            return new VoiceJoinOutcome(VoiceJoinCode.Ok, existing, previousOthers, connection.MicOn);
        }
    }

    /// <summary>True when the connection is in <paramref name="roomId"/> already (a repeated Join needs no game-loop
    /// check: the membership sweep re-validates it every second anyway).</summary>
    public bool IsIn(VoiceConnection connection, long roomId)
    {
        lock (_sync) return connection.RoomId == roomId && _rooms.TryGetValue(roomId, out var set) && set.Contains(connection);
    }

    /// <summary>Leaves the current room (if any); returns the other members that were in it, for the caller to notify.</summary>
    public VoiceConnection[] Leave(VoiceConnection connection)
    {
        lock (_sync)
        {
            var others = OthersLocked(connection);
            LeaveLocked(connection);
            return others;
        }
    }

    private VoiceConnection[] OthersLocked(VoiceConnection connection) =>
        connection.RoomId != 0 && _rooms.TryGetValue(connection.RoomId, out var set) ? set.Where(c => c != connection).ToArray() : [];

    private void LeaveLocked(VoiceConnection connection)
    {
        if (connection.RoomId == 0) return;
        if (_rooms.TryGetValue(connection.RoomId, out var set))
        {
            set.Remove(connection);
            if (set.Count == 0) _rooms.Remove(connection.RoomId);
        }
        connection.RoomId = 0;
        connection.Speaking = false;
    }

    /// <summary>Drops the connection entirely: its room (if any) and its one-per-account slot.</summary>
    public VoiceConnection[] Disconnect(VoiceConnection connection)
    {
        lock (_sync)
        {
            if (connection.Account != null && _byAccount.TryGetValue(connection.Account, out var current) && ReferenceEquals(current, connection))
                _byAccount.Remove(connection.Account);
            var others = OthersLocked(connection);
            LeaveLocked(connection);
            return others;
        }
    }

    /// <summary>Records the client's mic request. The mic the room sees follows it unless the leader's mute
    /// (<see cref="VoiceConnection.ChatForbidden"/>) holds it off; see <see cref="VoiceMicOutcome"/> for what to send.</summary>
    public VoiceMicOutcome SetMic(VoiceConnection connection, bool on)
    {
        lock (_sync)
        {
            connection.MicWanted = on;
            return ApplyMicLocked(connection, refuseOn: on);
        }
    }

    /// <summary>Brings <see cref="VoiceConnection.MicOn"/> in line with the wish and the mute.</summary>
    private VoiceMicOutcome ApplyMicLocked(VoiceConnection connection, bool refuseOn)
    {
        bool on = connection.MicWanted && !connection.ChatForbidden;
        if (on == connection.MicOn)
            return new VoiceMicOutcome([], false, on, Changed: false, Refused: refuseOn && !on);
        connection.MicOn = on;
        bool stopped = !on && connection.Speaking;
        if (!on) connection.Speaking = false;
        var members = connection.RoomId != 0 && _rooms.TryGetValue(connection.RoomId, out var set) ? set.ToArray() : [];
        return new VoiceMicOutcome(members, stopped, on, Changed: true, Refused: refuseOn && !on);
    }

    /// <summary>Sends a SetMic() result: a change to the room (MicState, plus Vad speaking=0 when a mic-off cut a
    /// speaking turn), a refused mic-on to the sender alone (MicState off: the leader's mute stands).</summary>
    public static void BroadcastMic(VoiceConnection connection, VoiceMicOutcome outcome)
    {
        if (outcome.Changed)
            foreach (var member in outcome.Members)
            {
                member.SendMicState(connection.RoleId, outcome.On);
                if (outcome.StoppedSpeaking) member.SendVad(connection.RoleId, false);
            }
        // A change already told the sender (it is in Members) unless it is in no room yet.
        if (outcome.Refused && !(outcome.Changed && outcome.Members.Contains(connection)))
            connection.SendMicState(connection.RoleId, false);
    }

    public void SetSpeaker(VoiceConnection connection, bool on)
    {
        lock (_sync) connection.SpeakerOn = on;
    }

    /// <summary>Everyone else in the sender's room with a speaker on; never includes the sender.</summary>
    public VoiceConnection[] ListenersFor(VoiceConnection sender)
    {
        lock (_sync)
            return sender.RoomId != 0 && _rooms.TryGetValue(sender.RoomId, out var set)
                ? set.Where(c => c != sender && c.SpeakerOn).ToArray() : [];
    }

    public VoiceConnection[] MembersOf(long roomId)
    {
        lock (_sync) return _rooms.TryGetValue(roomId, out var set) ? set.ToArray() : [];
    }

    /// <summary>Records a forwarded audio frame; returns true the first time after silence (the caller then
    /// broadcasts Vad speaking=1 to the room).</summary>
    public bool MarkSpeaking(VoiceConnection connection)
    {
        lock (_sync)
        {
            connection.LastAudioAtMs = Environment.TickCount64;
            if (connection.Speaking) return false;
            connection.Speaking = true;
            return true;
        }
    }

    /// <summary>The game loop's regular tick: the leader's mute and the VAD silence timeout every tick, the
    /// team-membership sweep once a second. All run here (not via GameLoop.InvokeAsync) because Tick already executes
    /// on the loop thread, the only one that may read TeamService.</summary>
    public void Tick(GameTime time)
    {
        SweepMuteAndVad();
        _sinceSweep += time.Delta;
        if (_sinceSweep < SweepInterval) return;
        _sinceSweep = TimeSpan.Zero;
        SweepMembership();
    }

    /// <summary>
    /// Copies the leader's forbidden-chat (TeamService, loop-owned) onto every joined connection, where the voice
    /// connection's own task reads it for each Mic and Audio frame without a loop hop, and forces the mic the room
    /// sees off (or back to the player's wish once the mute is lifted), telling the room. Then the VAD timeout: 300 ms
    /// without audio ends a speaking turn.
    /// </summary>
    private void SweepMuteAndVad()
    {
        List<(VoiceConnection Connection, VoiceMicOutcome Mic)>? micChanged = null;
        List<(VoiceConnection Connection, long Room)>? silenced = null;
        long now = Environment.TickCount64;
        // Asked before taking the lock: MicHeldOff reads TeamService and GuildVoiceService (its own lock), and nothing
        // else may ever be locked while _sync is held.
        (VoiceConnection Connection, long Room)[] joined;
        lock (_sync) joined = _rooms.SelectMany(r => r.Value.Select(c => (c, r.Key))).ToArray();
        var heldOff = new Dictionary<VoiceConnection, bool>(joined.Length);
        foreach (var (c, room) in joined) heldOff[c] = MicHeldOff(c.RoleId, room);
        lock (_sync)
        {
            foreach (var room in _rooms)
                foreach (var c in room.Value)
                {
                    // A connection that joined in between keeps the value its Join set until the next tick.
                    if (heldOff.TryGetValue(c, out bool off)) c.ChatForbidden = off;
                    var mic = ApplyMicLocked(c, refuseOn: false);
                    if (mic.Changed) (micChanged ??= new()).Add((c, mic));
                    if (c.Speaking && now - c.LastAudioAtMs >= VadSilence.TotalMilliseconds)
                    {
                        c.Speaking = false;
                        (silenced ??= new()).Add((c, room.Key));
                    }
                }
        }
        if (micChanged != null)
            foreach (var (connection, mic) in micChanged) BroadcastMic(connection, mic);
        if (silenced == null) return;
        foreach (var (connection, room) in silenced)
            foreach (var member in MembersOf(room)) member.SendVad(connection.RoleId, false);
    }

    /// <summary>Whether <paramref name="role"/> may be in room <paramref name="key"/>: its party, or the guild voice
    /// channel it sits in on the panel. Loop-owned reads (TeamService).</summary>
    public bool Belongs(long role, VoiceRoomKey key) => key.IsGuild
        ? guildVoice.IsSeated(role, key.GuildId, key.ChannelIndex)
        : key.IsTeam && teams.TeamOf(role).TeamId == key.Value;

    /// <summary>The mic the room may not hear: the party leader's forbidden-chat, or Order mode without a mic in a
    /// guild channel.</summary>
    public bool MicHeldOff(long role, long room) => room < 0 ? !guildVoice.MaySpeak(role) : teams.IsForbiddenChat(role);

    /// <summary>
    /// Re-validates every joined connection against SessionRegistry and <see cref="Belongs"/>: a session that is gone,
    /// switched teams or left its guild voice channel is removed (Kicked + MemberChange to the rest). The held-off
    /// mic is not here: <see cref="SweepMuteAndVad"/> applies it every tick.
    /// </summary>
    private void SweepMembership()
    {
        VoiceConnection[] snapshot;
        lock (_sync) snapshot = _rooms.Values.SelectMany(s => s).ToArray();
        foreach (var connection in snapshot)
        {
            long room = connection.RoomId;
            if (room == 0) continue;
            bool alive = sessions.All().Any(s => s.Role?.Id == connection.RoleId);
            if (!alive || !Belongs(connection.RoleId, new VoiceRoomKey(room)))
            {
                var others = Leave(connection);
                connection.Kick(VoiceKickCode.RemovedFromRoom);
                foreach (var other in others) other.SendMemberChange(connection.RoleId, false);
                log.LogInformation("Voice: role {RoleId} dropped from room {Room} (sweep)", connection.RoleId, room);
            }
        }
    }
}
