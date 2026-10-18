using Microsoft.Extensions.Options;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Voice;

/// <summary>Join() outcome: the accepted/refused code and, on success, the room's members before the newcomer and
/// the members of the room it left (a switch), for the caller to notify.</summary>
public readonly record struct VoiceJoinOutcome(byte Code, VoiceConnection[] ExistingMembers, VoiceConnection[] PreviousRoomOthers);

/// <summary>SetMic() outcome: the whole room (sender included) and whether the sender was speaking until now.</summary>
public readonly record struct VoiceMicOutcome(VoiceConnection[] Members, bool StoppedSpeaking);

/// <summary>
/// The voice relay's shared state: who is authed, who is in which room, mic/speaker flags, and the VAD and
/// membership-revocation sweep. Everything here is guarded by one lock: connections mutate it from their own
/// socket-reading tasks, and <see cref="Tick"/> (the game loop) reads <see cref="TeamService"/> and
/// <see cref="SessionRegistry"/> once a second to revoke stale membership, so the lock has to be safe to take
/// from either side. Never touches <see cref="VoiceConnection"/>'s socket directly; it only calls the Send*
/// helpers, which queue.
/// </summary>
public sealed class VoiceRooms(IOptions<ServerOptions> options, TeamService teams, SessionRegistry sessions,
    ILogger<VoiceRooms> log) : IGameTicker
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan VadSilence = TimeSpan.FromMilliseconds(300);

    private readonly object _sync = new();
    private readonly Dictionary<string, VoiceConnection> _byAccount = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, HashSet<VoiceConnection>> _rooms = new();
    private TimeSpan _sinceSweep;

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

    public VoiceJoinOutcome Join(VoiceConnection connection, long roomId)
    {
        lock (_sync)
        {
            _rooms.TryGetValue(roomId, out var members);
            if (connection.RoomId == roomId && members != null)
                return new VoiceJoinOutcome(VoiceJoinCode.Ok, members.Where(c => c != connection).ToArray(), []);
            // A full room refuses before the connection leaves the room it is in.
            if (members != null && members.Count >= options.Value.Voice.MaxRoomSize) return new VoiceJoinOutcome(VoiceJoinCode.Full, [], []);
            var previousOthers = OthersLocked(connection);
            LeaveLocked(connection);
            if (members == null) _rooms[roomId] = members = new();
            var existing = members.ToArray();
            members.Add(connection);
            connection.RoomId = roomId;
            return new VoiceJoinOutcome(VoiceJoinCode.Ok, existing, previousOthers);
        }
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

    /// <summary>Sets the sender's mic; returns the whole room (the sender included: it must see its own state too) and
    /// whether a mic-off cut a speaking turn, so the caller also sends Vad speaking=0.</summary>
    public VoiceMicOutcome SetMic(VoiceConnection connection, bool on)
    {
        lock (_sync)
        {
            connection.MicOn = on;
            bool stopped = !on && connection.Speaking;
            if (!on) connection.Speaking = false;
            var members = connection.RoomId != 0 && _rooms.TryGetValue(connection.RoomId, out var set) ? set.ToArray() : [];
            return new VoiceMicOutcome(members, stopped);
        }
    }

    /// <summary>Sends a SetMic() result to the room: MicState, plus Vad speaking=0 when the mic-off cut a speaking turn.</summary>
    public static void BroadcastMic(VoiceConnection connection, VoiceMicOutcome outcome, bool on)
    {
        foreach (var member in outcome.Members)
        {
            member.SendMicState(connection.RoleId, on);
            if (outcome.StoppedSpeaking) member.SendVad(connection.RoleId, false);
        }
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

    /// <summary>The game loop's regular tick: VAD silence timeout every tick, the team-membership sweep once a
    /// second. Both run here (not via GameLoop.InvokeAsync) because Tick already executes on the loop thread.</summary>
    public void Tick(GameTime time)
    {
        SweepVad();
        _sinceSweep += time.Delta;
        if (_sinceSweep < SweepInterval) return;
        _sinceSweep = TimeSpan.Zero;
        SweepMembership();
    }

    private void SweepVad()
    {
        List<(VoiceConnection Connection, long Room)>? silenced = null;
        long now = Environment.TickCount64;
        lock (_sync)
        {
            foreach (var room in _rooms)
                foreach (var c in room.Value)
                    if (c.Speaking && now - c.LastAudioAtMs >= VadSilence.TotalMilliseconds)
                    {
                        c.Speaking = false;
                        (silenced ??= new()).Add((c, room.Key));
                    }
        }
        if (silenced == null) return;
        foreach (var (connection, room) in silenced)
            foreach (var member in MembersOf(room)) member.SendVad(connection.RoleId, false);
    }

    /// <summary>
    /// Re-validates every joined connection against TeamService/SessionRegistry: a session that is gone, switched
    /// teams or no longer matches its room is removed (Kicked + MemberChange to the rest); a member the team leader
    /// muted gets its mic forced off and a MicState broadcast, without leaving the room.
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
            var (teamId, _) = teams.TeamOf(connection.RoleId);
            if (!alive || teamId != room)
            {
                var others = Leave(connection);
                connection.Kick(VoiceKickCode.RemovedFromRoom);
                foreach (var other in others) other.SendMemberChange(connection.RoleId, false);
                log.LogInformation("Voice: role {RoleId} dropped from room {Room} (sweep)", connection.RoleId, room);
                continue;
            }
            bool muted = teams.IsForbiddenChat(connection.RoleId);
            if (muted && connection.MicOn) BroadcastMic(connection, SetMic(connection, false), false);
        }
    }
}
