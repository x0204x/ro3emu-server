using System.Text.Json;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Tavern (MG_Barroom 82481-82514; FunctionConfig 2410 "Tavern", enum ChannelBarRoom; client LC_BarRoomServer,
/// LC_DAL_BarRoom, LC_BarRoomHelper and its offline mock Lua_BarRoom_Mock): voice and text rooms opened from the chat
/// side button. A player creates a room (topic and language from BarroomTypeConfig), browses the others through the
/// micro-service PartyService.getPartysSummary (party type 4, <see cref="Directory"/>), joins, chats on public channel
/// 10 (group "10_&lt;guid&gt;", ChatService) and talks on the voice relay (room = the guid in decimal, VoiceRooms).
///
/// The client keeps no room state of its own: every 82506 InfoChange makes an open Tavern window ask 82508 for the whole
/// BarRoomTotalDetail again, and 82507 UserEnter does the same plus a welcome line. So every change answers its caller
/// and pushes 82506 to the rest of the room. The owner (ownerGuid; the client has no other role, gmSpRoomCount is a GM
/// field it never reads) runs the room: the speaking mode (Free: anyone, Manual: the seat list, Order: the speech
/// list, at most PublicParamConfig.BarroomSpeakNum), mic requests, mute (forbiddenSpeaker, BarroomForbiddentime),
/// remove (blackList), close all mics, transfer and the room's name, topic and language. The relay enforces it:
/// <see cref="MaySpeak"/>. The client posts the gift, transfer and close-mic lines into the room chat itself.
///
/// Gifts are the four drinks (ItemConfig type 2 / subtype 213): use effect 110 GainBarroomHot adds to the room's heat,
/// 111 GainStar to the receiver's Star Power (DBBarRoomInfo.popularity, achievement condition 106008). Time spent in a
/// room counts toward the daily quest 2000 (target 106010 BarroomToDayActTime, TaskService.TavernTasks), whose award
/// (MultiverseGroup 8000 -> AwardConfig 1008000) is three Hot Cocoa.
///
/// Rooms are online state, as the original's room list only ever shows live rooms: in memory, gone when the last member
/// leaves or after a restart. A role's own counters are its role blob "barroom" (<see cref="RoleState"/>).
/// (inferred) Members whose session is gone keep their place for <see cref="OfflineGraceMs"/> (5043 barRoomId brings a
/// relogging client back in; error 2563 "recipient offline" names a member who is not online), then leave.
/// </summary>
public sealed partial class TavernService : Messaging.IMessageModule, IGameTicker
{
    public const uint CreateMsg = 82481, EnterMsg = 82482, ExitMsg = 82483, InviteMsg = 82484, InviteNotify = 82485;
    public const uint InviteResponseMsg = 82486, ApplyMicMsg = 82487, DownMicMsg = 82488, TransferMsg = 82489;
    public const uint MicTypeMsg = 82490, GiftMsg = 82491, HandleApplyMsg = 82492, MicOrderMsg = 82493, SetInfoMsg = 82494;
    public const uint ForbidMsg = 82498, UnforbidMsg = 82499, KickMsg = 82500, UnkickMsg = 82501;
    public const uint ShutUpMsg = 82504, UnshutUpMsg = 82505, InfoChangeNotify = 82506, UserEnterNotify = 82507;
    public const uint GetMsg = 82508, MuteMsg = 82514, MuteInChannelNotify = 77003, Sync = 5043;

    /// <summary>FunctionConfig 2410 "Tavern" (MG_Function Function_Barroom).</summary>
    public const long Function = 2410;

    /// <summary>LC_BarRoomServer's party type for the PartyService directory.</summary>
    public const int PartyType = 4;

    /// <summary>BarroomTypeConfig._iType of the topics and of the languages.</summary>
    private const long TopicKind = 0, LanguageKind = 1;

    // ItemConfig._iUseEffect ids of the gift drinks (enum UseEffect GainBarroomHot / GainStar).
    private const int EffectHeat = 110, EffectStar = 111;

    /// <summary>The higher drinks (ItemConfig 2/213): Gingerbread, Craft Brew, Grape Wine.</summary>
    private const int Gingerbread = 1200802, CraftBrew = 1200803, GrapeWine = 1200804;

    // PublicParamConfig fallbacks (the client's own values), for a data set without them.
    private const int DefaultNameLength = 18, DefaultSetInfoCdSec = 5, DefaultTransferCdSec = 30, DefaultPersonNum = 1000;
    private const int DefaultSpeakNum = 10, DefaultHeatMiniIncrease = 50, DefaultHeatTagsNum = 2, DefaultHotTagsNum = 4;
    private const long DefaultForbiddenMs = 86_400_000, DefaultHeatTagsMs = 300_000, DefaultTaskId = 2000, DefaultActSeconds = 1200;

    /// <summary>LC_ConditionDefine BarroomToDayActTime, the daily quest's target.</summary>
    private const long TargetStay = 106010;

    /// <summary>(inferred) A member whose session is gone keeps its place this long.</summary>
    public const long OfflineGraceMs = 300_000;

    /// <summary>(inferred) Time in a room is written to the role every minute, and at once when it completes the day.</summary>
    private const int FlushSeconds = 60;

    /// <summary>The client sets no limit on a mic request's reason or an invite list; these bounds are ours.</summary>
    private const int MaxReasonLength = 50, MaxInviteTargets = 50, MaxGiftCount = 999;

    /// <summary>(inferred) A GM tag (op 137 GMSetBarroomRedBag) lasts a day: the panel gives it no end.</summary>
    private const long GmTagSeconds = 86_400;

    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    private readonly GameData _data;
    private readonly RoleStore _roles;
    private readonly CharacterService _character;
    private readonly ChatService _chat;
    private readonly FunctionService _functions;
    private readonly TaskService _tasks;
    private readonly SessionRegistry _sessions;
    private readonly GameLoop _loop;
    private readonly IOptions<ServerOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TavernService> _log;

    private readonly object _sync = new();
    private readonly Dictionary<long, Room> _rooms = [];
    private readonly Dictionary<long, long> _roomOf = []; // role -> room guid
    private readonly Dictionary<long, double> _pendingSeconds = []; // role -> time in a room not yet written
    private readonly HashSet<long> _flushing = []; // roles with a write of their time already posted
    private long _nextGuid = Voice.VoiceRoomKey.TavernBase + 1;
    private TimeSpan _lastSweep;
    private long _heatWindowEndsMs;

    public TavernService(GameData data, RoleStore roles, CharacterService character, ChatService chat, FunctionService functions,
        TaskService tasks, SessionRegistry sessions, GameLoop loop, GmCommands gm, IOptions<ServerOptions> options,
        TimeProvider clock, ILogger<TavernService> log)
    {
        _data = data; _roles = roles; _character = character; _chat = chat; _functions = functions; _tasks = tasks;
        _sessions = sessions; _loop = loop; _options = options; _clock = clock; _log = log;
        chat.TavernMember = IsMember;
        tasks.TavernSeconds = TodaySeconds;
        gm.Register("tavern", "[list] (Tavern test aid: the open rooms, or the room you are in)", GmTavernAsync);
    }

    /// <summary>Raised on the loop after a role's Star Power changed (AchievementService re-reads 106008).</summary>
    public List<Action<GameSession>> PopularityChanged { get; } = [];

    /// <summary>A role's own Tavern counters, role blob "barroom" (JSON). Popularity and WeekPopularity are the Star Power
    /// received from gifts (5043 popularity / weekPopularity); ActDays counts the days whose stay reached the daily quest's
    /// 1200 s (5043 actDays, condition 106009 BarroomActDays, inferred meaning); TodaySeconds is the day's time in a room
    /// (5043 inBarRoomTime, quest target 106010), DayStartMs and WeekStartMs the 05:00 boundaries they belong to.</summary>
    public sealed class RoleState
    {
        public int Version { get; set; } = 1;
        public int Popularity { get; set; }
        public int WeekPopularity { get; set; }
        public long WeekStartMs { get; set; }
        public int ActDays { get; set; }
        public long DayStartMs { get; set; }
        public int TodaySeconds { get; set; }
        public bool TodayComplete { get; set; }
        /// <summary>The drink an active day earned and the bag has not received yet (<see cref="ServerOptions.TavernDayDrinks"/>).</summary>
        public int DrinkOwed { get; set; }

        public RoleState Clone() => (RoleState)MemberwiseClone();
    }

    private sealed class Member(long joinSec, int area)
    {
        public long JoinSec { get; } = joinSec;
        public int Area { get; } = area;
        /// <summary>Unix ms since the member's session is gone; 0 while online.</summary>
        public long OfflineSinceMs { get; set; }
    }

    private sealed record Application(long Role, string Reason);

    private sealed class Room(long guid, long owner, long createSec, int region)
    {
        public long Guid { get; } = guid;
        public long CreateSec { get; } = createSec;
        public int Region { get; } = region;
        public long Owner { get; set; } = owner;
        public int Type { get; set; }
        public int Language { get; set; }
        public string Name { get; set; } = "";
        public Dictionary<long, Member> Members { get; } = [];
        public Dictionary<long, long> Blocked { get; } = []; // role -> removed at, unix s
        public Dictionary<long, long> Forbidden { get; } = []; // role -> muted until, unix s
        public List<Application> Applications { get; } = [];
        public List<long> Speakers { get; } = []; // the seat (Manual) or speech (Order) list, in order
        public SpeakAuthorityType Mode { get; set; } = SpeakAuthorityType.Free;
        public bool MicClosed { get; set; }
        public int Hot { get; set; }
        public int LastHot { get; set; }
        public int LastHotAdd { get; set; }
        public Dictionary<RoomTagType, long> Tags { get; } = []; // tag -> ends, unix s
        public long TransferReadySec { get; set; }
        public long InfoReadySec { get; set; }
        public int GmCount { get; set; }
    }

    public void Map(Messaging.MessageRoutes routes)
    {
        routes.Map(CreateMsg, "tavern", (s, _, body) => CreateAsync(s, CreateBarRoomReq.Parser.ParseFrom(body.Span)));
        routes.Map(EnterMsg, "tavern", (s, _, body) => EnterAsync(s, EnterBarRoomReq.Parser.ParseFrom(body.Span)));
        routes.Map(ExitMsg, "tavern", (s, _, body) => ExitAsync(s, ExitBarRoomReq.Parser.ParseFrom(body.Span)));
        routes.Map(GetMsg, "tavern", (s, _, _) => GetAsync(s));
        routes.Map(InviteMsg, "tavern", (s, _, body) => InviteAsync(s, InviteEnterBarRoomReq.Parser.ParseFrom(body.Span)));
        routes.Map(InviteResponseMsg, "tavern", (s, _, body) => InviteResponseAsync(s, ResponseInviteEnterBarRoomReq.Parser.ParseFrom(body.Span)));
        routes.Map(ApplyMicMsg, "tavern", (s, _, body) => ApplyMicAsync(s, ApplyOpenMicReq.Parser.ParseFrom(body.Span)));
        routes.Map(DownMicMsg, "tavern", (s, _, body) => DownMicAsync(s, DownMicReq.Parser.ParseFrom(body.Span)));
        routes.Map(HandleApplyMsg, "tavern", (s, _, body) => HandleApplyAsync(s, OwnerHandleOpenMicReq.Parser.ParseFrom(body.Span)));
        routes.Map(MicTypeMsg, "tavern", (s, _, body) => MicTypeAsync(s, ModifyControlMicTypeReq.Parser.ParseFrom(body.Span)));
        routes.Map(MicOrderMsg, "tavern", (s, _, body) => MicOrderAsync(s, SetMicOrderReq.Parser.ParseFrom(body.Span)));
        routes.Map(TransferMsg, "tavern", (s, _, body) => TransferAsync(s, TransferOwnerReq.Parser.ParseFrom(body.Span)));
        routes.Map(SetInfoMsg, "tavern", (s, _, body) => SetInfoAsync(s, SetBarRoomInfoReq.Parser.ParseFrom(body.Span)));
        routes.Map(ForbidMsg, "tavern", (s, _, body) => ForbidAsync(s, ForbiddenUserReq.Parser.ParseFrom(body.Span)));
        routes.Map(UnforbidMsg, "tavern", (s, _, body) => UnforbidAsync(s, CanecelForbiddenUserReq.Parser.ParseFrom(body.Span)));
        routes.Map(KickMsg, "tavern", (s, _, body) => KickAsync(s, KickUserReq.Parser.ParseFrom(body.Span)));
        routes.Map(UnkickMsg, "tavern", (s, _, body) => UnkickAsync(s, CancelKickUserReq.Parser.ParseFrom(body.Span)));
        routes.Map(ShutUpMsg, "tavern", (s, _, body) => CloseMicsAsync(s, ShutUpAllReq.Parser.ParseFrom(body.Span).BarRoomGuid, true));
        routes.Map(UnshutUpMsg, "tavern", (s, _, body) => CloseMicsAsync(s, CancelShutUpAllReq.Parser.ParseFrom(body.Span).BarRoomGuid, false));
        routes.Map(MuteMsg, "tavern", (s, _, body) => MuteAsync(s, MuteBarRoomPlayerReq.Parser.ParseFrom(body.Span)));
        routes.Map(GiftMsg, "tavern", (s, _, body) => GiftAsync(s, PresentGiftReq.Parser.ParseFrom(body.Span)));
    }

    // ---- for the relay and the chat ----

    /// <summary>For the relay and channel 10: <paramref name="role"/> is a member of room <paramref name="roomId"/>.</summary>
    public bool IsMember(long role, long roomId)
    {
        lock (_sync) return _roomOf.TryGetValue(role, out long at) && at == roomId;
    }

    /// <summary>
    /// For the relay: whether the room hears <paramref name="role"/>'s mic. The owner always (LC_BarRoomHelper
    /// CheckMemberCanOpenMic); nobody else while all mics are closed (isCloseMic) or while muted (forbiddenSpeaker); in
    /// Free mode everyone else, in Manual and Order mode only who is on the list (curSpeaks).
    /// </summary>
    public bool MaySpeak(long role, long roomId)
    {
        lock (_sync)
        {
            if (!_rooms.TryGetValue(roomId, out var room) || !room.Members.ContainsKey(role)) return false;
            if (room.Owner == role) return true;
            if (room.MicClosed || IsForbidden(room, role, NowSec())) return false;
            return room.Mode == SpeakAuthorityType.Free || room.Speakers.Contains(role);
        }
    }

    // ---- ticks ----

    public void Tick(GameTime time)
    {
        if (time.Now - _lastSweep < SweepInterval) return;
        double seconds = _lastSweep == TimeSpan.Zero ? 0 : (time.Now - _lastSweep).TotalSeconds;
        _lastSweep = time.Now;
        long nowMs = NowMs();
        var online = _sessions.All().Where(s => s.Role != null).GroupBy(s => s.Role!.Id).ToDictionary(g => g.Key, g => g.First());
        List<long> dropped = [];
        List<GameSession> flush = [];
        lock (_sync)
        {
            if (_rooms.Count == 0 && _pendingSeconds.Count == 0) return;
            foreach (var room in _rooms.Values)
                foreach (var (role, member) in room.Members)
                {
                    if (online.TryGetValue(role, out var session))
                    {
                        member.OfflineSinceMs = 0;
                        if (session.Tavern != null && Accrue(session, role, seconds)) flush.Add(session);
                    }
                    else if (member.OfflineSinceMs == 0) member.OfflineSinceMs = nowMs;
                    else if (nowMs - member.OfflineSinceMs >= OfflineGraceMs) dropped.Add(role);
                }
            PruneLocked(nowMs);
        }
        foreach (var session in flush) QueueFlush(session);
        foreach (long role in dropped) _loop.Post(() => RemoveAsync(role, null, "offline"));
    }

    /// <summary>Adds a second of stay; true when the role's time is due to be written. Under the lock.</summary>
    private bool Accrue(GameSession session, long role, double seconds)
    {
        double pending = _pendingSeconds.GetValueOrDefault(role) + seconds;
        _pendingSeconds[role] = pending;
        if (pending >= FlushSeconds) return true;
        // The day's quest completes on time, not at the next minute.
        var state = Rolled(session.Tavern!);
        return !state.TodayComplete && state.TodaySeconds + (int)pending >= ActSeconds();
    }

    /// <summary>Expired mutes and tags, and the heat window. Under the lock.</summary>
    private void PruneLocked(long nowMs)
    {
        long now = nowMs / 1000;
        foreach (var room in _rooms.Values)
        {
            foreach (var (role, until) in room.Forbidden.ToList())
                if (until <= now) room.Forbidden.Remove(role);
            foreach (var (tag, ends) in room.Tags.ToList())
                if (ends <= now) room.Tags.Remove(tag);
        }
        if (nowMs < _heatWindowEndsMs) return;
        long window = Param("BarroomHeatTagsTime", DefaultHeatTagsMs);
        _heatWindowEndsMs = nowMs + window;
        HeatWindowLocked(now + window / 1000);
    }

    // ---- helpers ----

    /// <summary>The caller's room when it is a member, and <paramref name="guid"/> (when given) names it.</summary>
    private int InRoomLocked(long role, long guid, out Room room)
    {
        room = null!;
        if (!_roomOf.TryGetValue(role, out long at)) return guid != 0 && !_rooms.ContainsKey(guid) ? ErrorCodes.TavernNotFound : ErrorCodes.TavernNotInRoom;
        if (guid != 0 && guid != at) return _rooms.ContainsKey(guid) ? ErrorCodes.TavernNotInRoom : ErrorCodes.TavernNotFound;
        room = _rooms[at];
        return 0;
    }

    /// <summary>As <see cref="InRoomLocked"/>, and the caller owns the room.</summary>
    private int HostedLocked(long role, long guid, out Room room)
    {
        int code = InRoomLocked(role, guid, out room);
        return code != 0 ? code : room.Owner == role ? 0 : ErrorCodes.TavernNoPermission;
    }

    private static bool IsForbidden(Room room, long role, long now) => room.Forbidden.TryGetValue(role, out long until) && until > now;

    private GameSession? Online(long role) => _sessions.All().FirstOrDefault(s => s.Role?.Id == role);

    private long NowMs() => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    private long NowSec() => _clock.GetUtcNow().ToUnixTimeSeconds();

    private long Param(string key, long fallback) => _data.Table("PublicParamConfig").TryGetValue(key, out var value)
        && value.ValueKind == JsonValueKind.Number ? (long)(GameData.Number(value) ?? fallback) : fallback;

    private long[] ParamList(string key) => _data.Table("PublicParamConfig").TryGetValue(key, out var value)
        && value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0))] : [];

    private int SpeakNum() => (int)Math.Clamp(Param("BarroomSpeakNum", DefaultSpeakNum), 1, 100);

    /// <summary>PublicParamConfig.BarroomTaskID, the daily Tavern quest.</summary>
    public long TaskId() => Param("BarroomTaskID", DefaultTaskId);

    /// <summary>A day's stay that counts as active: the daily quest's 106010 requirement (TaskConfig 2000: 1200 s).</summary>
    private int ActSeconds()
    {
        var row = Data.Config.TaskConfig.Get(_data, TaskId());
        long required = row?.ProgressTarget.FirstOrDefault(t => t is [TargetStay, _])?[1] ?? DefaultActSeconds;
        return (int)Math.Clamp(required, 1, int.MaxValue);
    }

    private static int AddClamped(int value, long delta) => (int)Math.Clamp(value + delta, 0, int.MaxValue);
}
