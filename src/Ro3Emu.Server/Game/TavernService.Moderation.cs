using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The mic seats and the owner's tools (LC_BarRoomHelper: MemberApplyOpenMic, OnOwnerHandleOpenMic, SetTalkMode,
/// ForbiddenUser, KickUser, ShutUpAll, TransferOwner, SetBarroomInfo; UI_Sys_Chat_BarRoom_PermissionPanel and
/// _SetUpListWidget). Each answers its caller and pushes 82506 to the room, whose open windows then ask for it again.
/// </summary>
public sealed partial class TavernService
{
    /// <summary>
    /// 82487: a member asks for a seat (the edit panel's reason, nil from the mic button). Refused in Free mode and for
    /// the owner (2567 "No request is required"), for a member on the list already (2560), muted (2558) or already
    /// asking (2568). The request shows on the owner's panel (applyUsers, with its red dot).
    /// </summary>
    private async Task ApplyMicAsync(GameSession s, ApplyOpenMicReq req)
    {
        long self = s.Role!.Id;
        string reason = req.Reason ?? "";
        int code;
        long guid = 0;
        lock (_sync)
        {
            code = InRoomLocked(self, req.BarRoomGuid, out var room);
            if (code == 0)
            {
                guid = room.Guid;
                code = room.Mode == SpeakAuthorityType.Free || room.Owner == self ? ErrorCodes.TavernNoApplyNeeded
                    : room.Speakers.Contains(self) ? ErrorCodes.TavernAlreadyOnMic
                    : IsForbidden(room, self, NowSec()) ? ErrorCodes.TavernMuted
                    : room.Applications.Any(a => a.Role == self) ? ErrorCodes.TavernAlreadyApplied
                    : reason.Length > MaxReasonLength ? ErrorCodes.Parameter
                    : 0;
                if (code == 0) room.Applications.Add(new Application(self, reason));
            }
        }
        await s.SendGameAsync(ApplyMicMsg, new ApplyOpenMicResp { Result = code });
        if (code == 0) await PushChangeAsync(guid, except: self);
    }

    /// <summary>82488: a member gives up its seat; the owner keeps the mic (2561), a member without one has none (2562).</summary>
    private async Task DownMicAsync(GameSession s, DownMicReq req)
    {
        long self = s.Role!.Id;
        int code;
        long guid = 0;
        lock (_sync)
        {
            code = InRoomLocked(self, req.BarRoomGuid, out var room);
            if (code == 0)
            {
                guid = room.Guid;
                code = room.Owner == self ? ErrorCodes.TavernOwnerKeepsMic : !room.Speakers.Remove(self) ? ErrorCodes.TavernNotOnMic : 0;
            }
        }
        await s.SendGameAsync(DownMicMsg, new DownMicResp { Result = code });
        if (code == 0) await PushChangeAsync(guid, except: self);
    }

    /// <summary>
    /// 82492: the owner agrees to or declines a request (2569 when there is none). Agreeing seats the member at the end
    /// of the list (2559 once BarroomSpeakNum seats are taken). The applicant gets the same reply, which it shows as
    /// "The host has Agreed / Declined" (59206 / 59207, LC_BarRoomHelper.OnOwnerHandleOpenMic).
    /// </summary>
    private async Task HandleApplyAsync(GameSession s, OwnerHandleOpenMicReq req)
    {
        long self = s.Role!.Id;
        int code;
        long guid = 0;
        lock (_sync)
        {
            code = HostedLocked(self, req.BarRoomGuid, out var room);
            if (code == 0)
            {
                guid = room.Guid;
                int at = room.Applications.FindIndex(a => a.Role == req.ApplyerId);
                code = at < 0 ? ErrorCodes.TavernNotApplied
                    : req.IsAgree && room.Speakers.Count >= SpeakNum() ? ErrorCodes.TavernMicFull
                    : 0;
                if (code == 0)
                {
                    room.Applications.RemoveAt(at);
                    if (req.IsAgree && !room.Speakers.Contains(req.ApplyerId)) room.Speakers.Add(req.ApplyerId);
                }
            }
        }
        var reply = new OwnerHandleOpenMicResp { Result = code, IsAgree = req.IsAgree };
        await s.SendGameAsync(HandleApplyMsg, reply);
        if (code != 0) return;
        if (req.ApplyerId != self && Online(req.ApplyerId) is { } applicant) await applicant.SendGameAsync(HandleApplyMsg, reply);
        await PushChangeAsync(guid, except: self);
    }

    /// <summary>
    /// 82490 from the permission panel's Save: the speaking mode (controlMicType = talk mode - 1) and its list (Manual:
    /// the members given a seat, Order: the speech order), micOrder from 1. The reply echoes the list as kept: the
    /// owner's client then turns off the mic of every other member (and asks 82514 to mute them outside Free mode).
    /// Refused past BarroomSpeakNum (2559) or with a muted member on it (2558). (inferred) Non-members are dropped from
    /// the list, the owner needs no seat, and requests already on the list (or all of them, in Free mode) are answered.
    /// </summary>
    private async Task MicTypeAsync(GameSession s, ModifyControlMicTypeReq req)
    {
        var (code, kept, guid) = SetSpeakers(s, req.BarRoomGuid, req.ControlMicType, req.MicOrder?.SpeakUsers);
        var reply = new ModifyControlMicTypeResp { Result = code };
        if (code == 0) reply.MicOrder = kept;
        await s.SendGameAsync(MicTypeMsg, reply);
        if (code == 0) await PushChangeAsync(guid, except: s.Role!.Id);
    }

    /// <summary>82493: the list alone, in the current mode. No client code sends it; kept as 82490's list half.</summary>
    private async Task MicOrderAsync(GameSession s, SetMicOrderReq req)
    {
        SpeakAuthorityType mode;
        lock (_sync) mode = _roomOf.TryGetValue(s.Role!.Id, out long at) ? _rooms[at].Mode : SpeakAuthorityType.Free;
        var (code, _, guid) = SetSpeakers(s, req.BarRoomGuid, mode, req.SpeakUsers);
        await s.SendGameAsync(MicOrderMsg, new SetMicOrderResp { Result = code });
        if (code == 0) await PushChangeAsync(guid, except: s.Role!.Id);
    }

    private (int Code, SetMicOrderReq Kept, long Guid) SetSpeakers(GameSession s, long guid, SpeakAuthorityType mode,
        IEnumerable<CurSpeakUsers>? users)
    {
        long self = s.Role!.Id;
        var kept = new SetMicOrderReq { BarRoomGuid = guid };
        lock (_sync)
        {
            int code = HostedLocked(self, guid, out var room);
            if (code != 0) return (code, kept, 0);
            if (!Enum.IsDefined(mode)) return (ErrorCodes.Parameter, kept, 0);
            var list = (users ?? []).OrderBy(u => u.MicOrder).Select(u => u.UserId)
                .Where(id => id != self && room.Members.ContainsKey(id)).Distinct().ToList();
            if (list.Count > SpeakNum()) return (ErrorCodes.TavernMicFull, kept, 0);
            long now = NowSec();
            if (list.Any(id => IsForbidden(room, id, now))) return (ErrorCodes.TavernMuted, kept, 0);
            room.Mode = mode;
            room.Speakers.Clear();
            room.Speakers.AddRange(list);
            room.Applications.RemoveAll(a => mode == SpeakAuthorityType.Free || list.Contains(a.Role));
            for (int i = 0; i < list.Count; i++) kept.SpeakUsers.Add(new CurSpeakUsers { UserId = list[i], MicOrder = i + 1 });
            return (0, kept, room.Guid);
        }
    }

    /// <summary>
    /// 82489: the owner hands the room to a member (2578 when the target is not in it, 2557 when blocked, 2558 when muted)
    /// once PublicParamConfig.BarRoomTransferRoomCd (30 s) has passed since the last hand-over (2575). The client posts
    /// the 59238 line and shows 59217 itself.
    /// </summary>
    private async Task TransferAsync(GameSession s, TransferOwnerReq req)
    {
        long self = s.Role!.Id;
        int code;
        long guid = 0;
        lock (_sync)
        {
            code = HostedLocked(self, req.BarRoomGuid, out var room);
            if (code == 0)
            {
                guid = room.Guid;
                long now = NowSec();
                code = req.TargetGuid == self ? ErrorCodes.TavernNoPermission
                    : room.Blocked.ContainsKey(req.TargetGuid) ? ErrorCodes.TavernTransferBlocked
                    : !room.Members.ContainsKey(req.TargetGuid) ? ErrorCodes.TavernNotInRoom
                    : IsForbidden(room, req.TargetGuid, now) ? ErrorCodes.TavernMuted
                    : room.TransferReadySec > now ? ErrorCodes.TavernTransferCooldown
                    : 0;
                if (code == 0)
                {
                    room.Owner = req.TargetGuid;
                    room.TransferReadySec = now + Param("BarRoomTransferRoomCd", DefaultTransferCdSec);
                    room.Speakers.Remove(req.TargetGuid);
                    room.Applications.RemoveAll(a => a.Role == req.TargetGuid);
                }
            }
        }
        await s.SendGameAsync(TransferMsg, new TransferOwnerResp { Result = code });
        if (code == 0) await PushChangeAsync(guid);
    }

    /// <summary>82494: the owner renames the room or changes its topic or language, at most every BarRoomSetInfoCd (5 s,
    /// 2573 "clicking too quickly") and under the creation rules.</summary>
    private async Task SetInfoAsync(GameSession s, SetBarRoomInfoReq req)
    {
        long self = s.Role!.Id;
        string name = (req.BarRoomName ?? "").Trim();
        int code = InfoRefusal(name, req.BarRoomType, req.LanguageType);
        long guid = 0;
        lock (_sync)
        {
            int hosted = HostedLocked(self, req.BarRoomGuid, out var room);
            if (hosted != 0) code = hosted;
            else if (code == 0)
            {
                guid = room.Guid;
                long now = NowSec();
                code = room.InfoReadySec > now ? ErrorCodes.TavernTooQuick : 0;
                if (code == 0)
                {
                    (room.Name, room.Type, room.Language) = (name, req.BarRoomType, req.LanguageType);
                    room.InfoReadySec = now + Param("BarRoomSetInfoCd", DefaultSetInfoCdSec);
                }
            }
        }
        await s.SendGameAsync(SetInfoMsg, new SetBarRoomInfoResp { Result = code });
        if (code == 0) await PushChangeAsync(guid, except: self);
    }

    /// <summary>
    /// 82498: the owner mutes a member for PublicParamConfig.BarroomForbiddentime (24 h, ms) in this room: the relay holds
    /// its mic off and its client gets the reply too, which it shows as "The host has muted you." (59201, shown only to
    /// a receiver who is not the owner). A member already muted is refused (2558).
    /// </summary>
    private async Task ForbidAsync(GameSession s, ForbiddenUserReq req)
    {
        long self = s.Role!.Id;
        int code;
        long guid = 0;
        lock (_sync)
        {
            code = HostedLocked(self, req.BarRoomGuid, out var room);
            if (code == 0)
            {
                guid = room.Guid;
                long now = NowSec();
                code = req.TargetGuid == self ? ErrorCodes.TavernNoPermission
                    : !room.Members.ContainsKey(req.TargetGuid) ? ErrorCodes.TavernNotInRoom
                    : IsForbidden(room, req.TargetGuid, now) ? ErrorCodes.TavernMuted
                    : 0;
                if (code == 0) room.Forbidden[req.TargetGuid] = now + Param("BarroomForbiddentime", DefaultForbiddenMs) / 1000;
            }
        }
        var reply = new ForbiddenUserResp { Result = code };
        await s.SendGameAsync(ForbidMsg, reply);
        if (code != 0) return;
        if (Online(req.TargetGuid) is { } target) await target.SendGameAsync(ForbidMsg, reply);
        await PushChangeAsync(guid, except: self);
    }

    /// <summary>82499: the mute is lifted (2570 when there is none). Its reply has no handler in this client build: its
    /// dataset key is spelled "CanecelForbiddenUser" and LC_DAL_BarRoom registers "CancelForbiddenUser".</summary>
    private async Task UnforbidAsync(GameSession s, CanecelForbiddenUserReq req)
    {
        int code;
        long guid = 0;
        lock (_sync)
        {
            code = HostedLocked(s.Role!.Id, req.BarRoomGuid, out var room);
            if (code == 0)
            {
                guid = room.Guid;
                code = room.Forbidden.Remove(req.TargetGuid, out long until) && until > NowSec() ? 0 : ErrorCodes.TavernNotMuted;
            }
        }
        await s.SendGameAsync(UnforbidMsg, new CanecelForbiddenUserResp { Result = code });
        if (code == 0) await PushChangeAsync(guid, except: s.Role.Id);
    }

    /// <summary>
    /// 82500: the owner removes a member and puts it on the room's Blocks list (it cannot come back: 2553) until 82501.
    /// The owner's client shows 59253. The removed player's own client learns it from 5043 barRoomId 0 and from 82506,
    /// whose 82508 then answers 2578. (inferred) The block lasts as long as the room.
    /// </summary>
    private async Task KickAsync(GameSession s, KickUserReq req)
    {
        long self = s.Role!.Id;
        int code;
        long guid = 0;
        lock (_sync)
        {
            code = HostedLocked(self, req.BarRoomGuid, out var room);
            if (code == 0)
            {
                guid = room.Guid;
                code = req.TargetGuid == self ? ErrorCodes.TavernNoPermission
                    : !room.Members.ContainsKey(req.TargetGuid) ? ErrorCodes.TavernNotInRoom : 0;
                if (code == 0) room.Blocked[req.TargetGuid] = NowSec();
            }
        }
        await s.SendGameAsync(KickMsg, new KickUserResp { Result = code });
        if (code != 0) return;
        var target = Online(req.TargetGuid);
        await RemoveAsync(req.TargetGuid, target, "removed by the owner");
        if (target != null) await target.SendGameAsync(InfoChangeNotify, new BarRoomInfoUpdateNotify { BarRoomGuid = guid });
    }

    /// <summary>82501: a player comes off the Blocks list (2566 when it is not on it).</summary>
    private async Task UnkickAsync(GameSession s, CancelKickUserReq req)
    {
        int code;
        long guid = 0;
        lock (_sync)
        {
            code = HostedLocked(s.Role!.Id, req.BarRoomGuid, out var room);
            if (code == 0)
            {
                guid = room.Guid;
                code = room.Blocked.Remove(req.TargetGuid) ? 0 : ErrorCodes.TavernNotBlocked;
            }
        }
        await s.SendGameAsync(UnkickMsg, new CancelKickUserResp { Result = code });
        if (code == 0) await PushChangeAsync(guid, except: s.Role.Id);
    }

    /// <summary>82504 / 82505: the owner closes or reopens every other mic (isCloseMic). The owner's client posts 59220 /
    /// 59221 to the room and asks 82514 for each member; the relay holds the mics off meanwhile.</summary>
    private async Task CloseMicsAsync(GameSession s, long guid, bool close)
    {
        int code;
        lock (_sync)
        {
            code = HostedLocked(s.Role!.Id, guid, out var room);
            if (code == 0)
            {
                guid = room.Guid;
                room.MicClosed = close;
            }
        }
        if (close) await s.SendGameAsync(ShutUpMsg, new ShutUpAllResp { Result = code });
        else await s.SendGameAsync(UnshutUpMsg, new CancelShutUpAllResp { Result = code });
        if (code == 0) await PushChangeAsync(guid, except: s.Role.Id);
    }

    /// <summary>
    /// 82514 from LC_BarRoomTalk.SetOtherMicMute (the owner's mode change, mute and close-all): barRoomUid is the room's
    /// voice id (its guid), targetPlayerUid the member. The member gets 77003 MutePlayerInChannel, on which its client
    /// mutes or unmutes its own Tavern mic (LC_DAL_BarRoom). Only the owner may ask (2554); the relay's own rule
    /// (<see cref="MaySpeak"/>) is what keeps a mic off.
    /// </summary>
    private async Task MuteAsync(GameSession s, MuteBarRoomPlayerReq req)
    {
        int code;
        lock (_sync)
        {
            code = HostedLocked(s.Role!.Id, req.BarRoomUid, out var room);
            if (code == 0 && !room.Members.ContainsKey(req.TargetPlayerUid)) code = ErrorCodes.TavernNotInRoom;
        }
        string group = req.BarRoomUid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await s.SendGameAsync(MuteMsg, new MuteBarRoomPlayerResp
        {
            Result = code, GroupId = group, IsMute = req.IsMute, TargetPlayerUid = req.TargetPlayerUid,
        });
        if (code == 0 && req.TargetPlayerUid != s.Role.Id && Online(req.TargetPlayerUid) is { } target)
            await target.SendGameAsync(MuteInChannelNotify, new MutePlayerInChannelNotify { ChatChannelGroupId = group, IsMute = req.IsMute });
    }
}
