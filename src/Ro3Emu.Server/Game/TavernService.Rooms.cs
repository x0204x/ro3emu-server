using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Creating, entering and leaving rooms, the room detail the client rebuilds its window from and the invitations
/// (LC_BarRoomHelper OnPartyGetClient / InviteFriend / OnInviteEnterBarRoomNotify / ResponseInvite).
/// </summary>
public sealed partial class TavernService
{
    /// <summary>
    /// 82481: a new room with the caller as owner and only member (LC_BarRoomServer.CreateBarRoomReq sends name, topic
    /// and language). The reply carries the whole room: OnPartyGetClient joins its chat and voice from it.
    /// (inferred) A caller already in a room leaves it first; a new room starts in Free mode (enum default).
    /// </summary>
    private async Task CreateAsync(GameSession s, CreateBarRoomReq req)
    {
        long self = s.Role!.Id;
        string name = (req.BarRoomName ?? "").Trim();
        int code = !_functions.IsOpen(s, Function) ? ErrorCodes.FeatureUnavailable : InfoRefusal(name, req.BarRoomType, req.LanguageType);
        if (code != 0)
        {
            await s.SendGameAsync(CreateMsg, new CreateBarRoomResp { Result = code });
            return;
        }
        await RemoveAsync(self, s, "create");
        BarRoomTotalDetail info;
        Room room;
        lock (_sync)
        {
            long now = NowSec();
            room = new Room(_nextGuid++, self, now, s.Role.Area) { Type = req.BarRoomType, Language = req.LanguageType, Name = name };
            room.Members[self] = new Member(now, s.Role.Area);
            _rooms[room.Guid] = room;
            _roomOf[self] = room.Guid;
            info = Detail(room);
        }
        LogOpened(_log, s.Role.Name, room.Guid, name);
        await s.SendGameAsync(CreateMsg, new CreateBarRoomResp { Result = 0, Info = info });
        await SyncAsync(s);
    }

    /// <summary>82482 from the room list, Discover or a switch of rooms (LC_BarRoomHelper.EnterRoom after its EXIT).</summary>
    private Task EnterAsync(GameSession s, EnterBarRoomReq req) =>
        JoinAsync(s, req.BarRoomGuid, (code, info) => s.SendGameAsync(EnterMsg, new EnterBarRoomResp { Result = code, Info = info }));

    /// <summary>
    /// Puts the caller in room <paramref name="guid"/> and answers through <paramref name="reply"/> (82482, or 82486 for an
    /// accepted invitation). Refused for a room that is gone (2552), the caller already in it (2571), on its Blocks list
    /// (2553) or a full room (PublicParamConfig.BarroomPersonNum, 2572). (inferred) A caller in another room leaves it.
    /// The others get 82507 UserEnter: their client asks for the room again and shows the welcome line.
    /// </summary>
    private async Task JoinAsync(GameSession s, long guid, Func<int, BarRoomTotalDetail?, Task> reply)
    {
        long self = s.Role!.Id;
        bool open = _functions.IsOpen(s, Function);
        int code;
        lock (_sync)
            code = !open ? ErrorCodes.FeatureUnavailable
                : !_rooms.TryGetValue(guid, out var target) ? ErrorCodes.TavernNotFound
                : target.Members.ContainsKey(self) ? ErrorCodes.TavernAlreadyIn
                : target.Blocked.ContainsKey(self) ? ErrorCodes.TavernBlocked
                : target.Members.Count >= Param("BarroomPersonNum", DefaultPersonNum) ? ErrorCodes.TavernFull
                : 0;
        if (code != 0)
        {
            await reply(code, null);
            return;
        }
        await RemoveAsync(self, s, "switch");
        BarRoomTotalDetail? info = null;
        List<GameSession> others = [];
        lock (_sync)
            if (_rooms.TryGetValue(guid, out var room)) // the room may have closed meanwhile
            {
                room.Members[self] = new Member(NowSec(), s.Role.Area);
                _roomOf[self] = guid;
                info = Detail(room);
                others = OnlineMembersLocked(room, except: self);
            }
        await reply(info == null ? ErrorCodes.TavernCleared : 0, info);
        if (info == null) return;
        await SyncAsync(s);
        var enter = new BarRoomUserEnterNotify { UserId = self };
        foreach (var other in others) await other.SendGameAsync(UserEnterNotify, enter);
    }

    /// <summary>82483. The client has already left the voice and cleared the chat (LC_BarRoomHelper.LeaveRoom).</summary>
    private async Task ExitAsync(GameSession s, ExitBarRoomReq req)
    {
        int code;
        lock (_sync) code = InRoomLocked(s.Role!.Id, req.BarRoomGuid, out _);
        await s.SendGameAsync(ExitMsg, new ExitBarRoomResp { Result = code });
        if (code == 0) await RemoveAsync(s.Role.Id, s, "exit");
    }

    /// <summary>82508: the caller's room in full (OnPartyGetClient indexes barRoomSummary, so a caller in no room is
    /// refused with 2578 rather than answered with an empty detail).</summary>
    private async Task GetAsync(GameSession s)
    {
        var reply = new BarRoomGetResp();
        lock (_sync)
        {
            reply.Result = InRoomLocked(s.Role!.Id, 0, out var room);
            if (reply.Result == 0) reply.Info = Detail(room);
        }
        await s.SendGameAsync(GetMsg, reply);
    }

    /// <summary>
    /// Takes <paramref name="role"/> out of its room, if any: its seat and mic request go, the next owner takes over an
    /// owner's room and the last member closes it (its chat with it). The leaver's time is written, its 5043 says no room,
    /// and the rest get 82506. <paramref name="session"/> is the leaver's when it is the caller (or online).
    /// (inferred) The next owner is the first seated speaker who is online, else the longest-present online member.
    /// </summary>
    private async Task RemoveAsync(long role, GameSession? session, string why)
    {
        Room? room;
        bool closed;
        List<GameSession> others = [];
        lock (_sync)
        {
            if (!_roomOf.Remove(role, out long guid) || !_rooms.TryGetValue(guid, out room)) return;
            room.Members.Remove(role);
            room.Speakers.Remove(role);
            room.Applications.RemoveAll(a => a.Role == role);
            closed = room.Members.Count == 0;
            if (closed) _rooms.Remove(guid);
            else
            {
                if (room.Owner == role)
                {
                    room.Owner = NextOwnerLocked(room);
                    room.Speakers.Remove(room.Owner); // the owner always may speak, as after 82489
                    room.Applications.RemoveAll(a => a.Role == room.Owner);
                }
                others = OnlineMembersLocked(room);
            }
        }
        session ??= Online(role);
        if (session == null) lock (_sync) _pendingSeconds.Remove(role); // gone offline: its time was written on leaving
        else
        {
            _chat.TavernLeft(session, room.Guid);
            await FlushNowAsync(session);
            await SyncAsync(session);
        }
        if (closed) _chat.TavernClosed(room.Guid);
        LogLeft(_log, role, room.Guid, why, closed ? ", room closed" : "");
        var change = new BarRoomInfoUpdateNotify { BarRoomGuid = room.Guid };
        foreach (var other in others) await other.SendGameAsync(InfoChangeNotify, change);
    }

    private static long NextOwnerLocked(Room room)
    {
        var online = room.Members.Where(m => m.Value.OfflineSinceMs == 0).Select(m => m.Key).ToHashSet();
        foreach (long speaker in room.Speakers)
            if (online.Contains(speaker)) return speaker;
        var pool = online.Count > 0 ? room.Members.Where(m => online.Contains(m.Key)) : room.Members;
        return pool.OrderBy(m => m.Value.JoinSec).ThenBy(m => m.Key).First().Key;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name} opens Tavern room {Room} \"{Title}\"")]
    private static partial void LogOpened(ILogger logger, string name, long room, string title);

    [LoggerMessage(Level = LogLevel.Information, Message = "Role {Role} leaves Tavern room {Room} ({Why}){Closed}")]
    private static partial void LogLeft(ILogger logger, long role, long room, string why, string closed);

    // ---- invitations ----

    /// <summary>
    /// 82484 from the invite panel (one friend per click; LC_BarRoomHelper.InviteFriend checks the 10 s cooldown and the
    /// target's level itself). The client sends no inviter details, so 82485 is filled here: the room's own name, the
    /// inviter's name, job and level. (inferred) Members, players offline and players without the Tavern function are
    /// left out; the reply lists who was invited.
    /// </summary>
    private async Task InviteAsync(GameSession s, InviteEnterBarRoomReq req)
    {
        long self = s.Role!.Id;
        var reply = new InviteEnterBarRoomResp { BarRoomGuid = req.BarRoomGuid };
        string roomName = "";
        HashSet<long> members = [];
        lock (_sync)
        {
            reply.Result = InRoomLocked(self, req.BarRoomGuid, out var room);
            if (reply.Result == 0)
            {
                roomName = room.Name;
                members = [.. room.Members.Keys];
            }
        }
        if (reply.Result == 0 && (req.TargetGuidList.Count == 0 || req.TargetGuidList.Count > MaxInviteTargets)) reply.Result = ErrorCodes.Parameter;
        List<GameSession> invited = [];
        if (reply.Result == 0)
            foreach (long target in req.TargetGuidList.Distinct())
                if (!members.Contains(target) && Online(target) is { } to && _functions.IsOpen(to, Function))
                {
                    invited.Add(to);
                    reply.TargetGuidList.Add(target);
                }
        await s.SendGameAsync(InviteMsg, reply);
        var notify = new InviteEnterBarRoomNotify
        {
            BarRoomGuid = req.BarRoomGuid, InviterGuid = self, RoomName = roomName, InviterName = s.Role.Name,
            InviterJob = s.Progress?.Career ?? s.Role.Career, InviterLevel = s.Progress?.Level ?? s.Role.Level,
        };
        foreach (var to in invited) await to.SendGameAsync(InviteNotify, notify);
    }

    /// <summary>
    /// 82486 from the invitation tip. Accepting enters the room as 82482 does (the client sends no ENTER of its own); the
    /// invitee's reply sets inviterGuid to itself, so LC_BarRoomHelper opens the window and asks for the room, and the
    /// inviter gets a copy with inviterGuid = the invitee, which it shows as "${1} has accepted / declined your
    /// invitation" (59224 / 59225) with that player's name. (inferred) No pending invitation is checked: anybody may
    /// enter a room anyway.
    /// </summary>
    private async Task InviteResponseAsync(GameSession s, ResponseInviteEnterBarRoomReq req)
    {
        long self = s.Role!.Id;
        int code = 0;
        BarRoomTotalDetail? info = null;
        if (req.IsAccept)
            await JoinAsync(s, req.BarRoomGuid, (c, detail) =>
            {
                code = c;
                info = detail;
                return Task.CompletedTask;
            });
        var reply = new ResponseInviteEnterBarRoomResp { Result = code, BarRoomGuid = req.BarRoomGuid, InviterGuid = self, IsAccept = req.IsAccept };
        if (info != null) reply.Info = info;
        await s.SendGameAsync(InviteResponseMsg, reply);
        if (code != 0 || req.InviterGuid == self || Online(req.InviterGuid) is not { } inviter) return;
        await inviter.SendGameAsync(InviteResponseMsg, new ResponseInviteEnterBarRoomResp
        {
            Result = 0, BarRoomGuid = req.BarRoomGuid, InviterGuid = self, IsAccept = req.IsAccept,
        });
    }

    // ---- the room as the client sees it ----

    /// <summary>The validity of a room's name, topic and language (82481 / 82494). Under no lock.</summary>
    private int InfoRefusal(string name, int type, int language)
    {
        if (name.Length == 0 || name.Any(char.IsControl)) return ErrorCodes.TavernNameInvalid;
        if (name.Length > Param("BarRoomNameLen", DefaultNameLength)) return ErrorCodes.TavernNameTooLong;
        if (BarroomTypeConfig.Get(_data, type) is not { Type: TopicKind }) return ErrorCodes.TavernBadType;
        if (BarroomTypeConfig.Get(_data, language) is not { Type: LanguageKind }) return ErrorCodes.TavernBadLanguage;
        return 0;
    }

    /// <summary>The room's list entry (the micro directory's GameSummaryData and the detail's head). Under the lock.</summary>
    private static BarRoomSummary Summary(Room room)
    {
        var summary = new BarRoomSummary
        {
            BarRoomGuid = room.Guid, BarRoomType = room.Type, LanguageType = room.Language, BarRoomName = room.Name,
            HotValue = room.Hot, RegionId = room.Region,
        };
        foreach (var (tag, ends) in room.Tags.OrderBy(t => t.Key)) summary.BarRoomTags.Add(new RoomTagInfo { Type = tag, Endtime = ends });
        foreach (var (role, member) in room.Members.OrderBy(m => m.Value.JoinSec).ThenBy(m => m.Key))
            summary.MemberList.Add(new MemberInfo { MemberId = role, AreaId = member.Area, JoinTime = member.JoinSec });
        return summary;
    }

    /// <summary>
    /// BarRoomTotalDetail as LC_BarRoomHelper reads it: blackList and forbiddenSpeaker keys (values: when removed and
    /// until when muted, unix seconds, as the mock's timestamps), applyUsers role -> reason, curSpeaks in list order with
    /// micOrder from 1, controlMicType, isCloseMic, ownerGuid. historyHot is the last heat window, transferCd the second
    /// the next transfer is allowed (neither is read by this client). Under the lock.
    /// </summary>
    private BarRoomTotalDetail Detail(Room room)
    {
        long now = NowSec();
        var detail = new BarRoomTotalDetail
        {
            BarRoomSummary = Summary(room), ControlMicType = room.Mode, IsCloseMic = room.MicClosed, OwnerGuid = room.Owner,
            CreateTime = room.CreateSec, TransferCd = room.TransferReadySec, GmSpRoomCount = room.GmCount,
            HistoryHot = new HistoryHotInfo { LastHot = room.LastHot, LastHotAdd = room.LastHotAdd },
        };
        foreach (var (role, at) in room.Blocked) detail.BlackList[role] = at;
        foreach (var (role, until) in room.Forbidden.Where(f => f.Value > now)) detail.ForbiddenSpeaker[role] = until;
        foreach (var application in room.Applications) detail.ApplyUsers[application.Role] = application.Reason;
        for (int i = 0; i < room.Speakers.Count; i++) detail.CurSpeaks.Add(new CurSpeakUsers { UserId = room.Speakers[i], MicOrder = i + 1 });
        return detail;
    }

    /// <summary>The online sessions of the room's members. Under the lock.</summary>
    private List<GameSession> OnlineMembersLocked(Room room, long except = 0)
    {
        List<GameSession> online = [];
        foreach (var session in _sessions.All())
            if (session.Role is { } role && role.Id != except && room.Members.ContainsKey(role.Id) && !online.Any(o => o.Role!.Id == role.Id))
                online.Add(session);
        return online;
    }

    /// <summary>82506 to every online member (but <paramref name="except"/>, who just had the room in a reply).</summary>
    private async Task PushChangeAsync(long guid, long except = 0)
    {
        List<GameSession> members;
        lock (_sync) members = _rooms.TryGetValue(guid, out var room) ? OnlineMembersLocked(room, except) : [];
        var change = new BarRoomInfoUpdateNotify { BarRoomGuid = guid };
        foreach (var member in members) await member.SendGameAsync(InfoChangeNotify, change);
    }
}
