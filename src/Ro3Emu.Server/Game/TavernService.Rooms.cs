using System.Text;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class TavernService
{
    private bool Available(GameSession s) => s.Role != null && s.Tavern != null && Goal > 0 && !s.TransportLost && !s.Channel.PeerClosed
        && _functions.IsOpen(s, Function) && s.Attachment is { Cancelled: false };
    public static bool ValidOption(Data.GameData data, int id, int category) => BarroomTypeConfig.Get(data, id) is { } row && row.Type == category;
    private bool Option(int id, int category) => ValidOption(_data, id, category);

    public async Task CreateAsync(GameSession s, CreateBarRoomReq req)
    {
        using var scope = await EnterScopeAsync(s, room: true);
        await _daily.ReconcileAsync(s);
        await _gate.WaitAsync();
        CreateBarRoomResp response;
        try
        {
            if (!Available(s) || !Option(req.BarRoomType, 0) || !Option(req.LanguageType, 1)
                || string.IsNullOrWhiteSpace(req.BarRoomName)
                || req.BarRoomName.EnumerateRunes().Count() > PublicParamConfig.BarRoomNameLen(_data))
                response = new() { Result = ErrorCodes.FeatureUnavailable };
            else if (ValidLease(s))
                response = _state.Rooms[s.Tavern!.RoomId].Owner == s.Role!.Id
                    ? new() { Info = Detail(_state.Rooms[s.Tavern.RoomId]) }
                    : new() { Result = ErrorCodes.FeatureUnavailable };
            else
            {
                var identity = _roles.NewTransaction();
                int index = identity.Add(new RoleIdentityWritePlan(RoleIdentityWriteKind.AllocateInstance));
                var ids = await identity.CommitAsync(_accounts, "tavern/allocate-room");
                long id = (long)ids[index]!;
                var next = Copy(_state);
                var room = new Room
                {
                    Id = id, Owner = s.Role!.Id, Area = s.Role.Area, Name = req.BarRoomName,
                    Type = req.BarRoomType, Language = req.LanguageType, CreatedMs = Now
                };
                next.Rooms[id] = room;
                await JoinCoreAsync(next, room, s);
                response = new() { Info = Detail(room) };
            }
        }
        finally { _gate.Release(); }
        await s.SendGameAsync(Create, response);
        if (response.Result == 0) await SyncAsync(s);
    }

    private async Task JoinCoreAsync(WorldState next, Room room, GameSession s)
    {
        var meter = Copy(s.Tavern!);
        meter.RoomId = room.Id; meter.Lease = Guid.NewGuid().ToString("N");
        meter.AccountedAtMs = Now; meter.DayEpoch = _daily.TavernBoundary;
        room.Members[s.Role!.Id] = new() { Area = s.Role.Area, JoinedMs = Now };
        await CommitRoomAsync(next, s, meter, "join");

    }

    public async Task EnterAsync(GameSession s, EnterBarRoomReq req)
    {
        using var scope = await EnterScopeAsync(s, room: true);
        await _daily.ReconcileAsync(s);
        await _gate.WaitAsync();
        EnterBarRoomResp response;
        try
        {
            if (!Available(s) || !_state.Rooms.TryGetValue(req.BarRoomGuid, out var room) || room.Closed
                || req.AreaId != 0 && req.AreaId != room.Area
                || ValidLease(s) && s.Tavern!.RoomId != room.Id
                || !ValidLease(s) && room.Members.Count >= PublicParamConfig.BarroomPersonNum(_data))
                response = new() { Result = ErrorCodes.FeatureUnavailable };
            else
            {
                if (!ValidLease(s))
                {
                    var next = Copy(_state);
                    room = next.Rooms[room.Id];
                    await JoinCoreAsync(next, room, s);
                }
                response = new() { Info = Detail(room) };
            }
        }
        finally { _gate.Release(); }
        await s.SendGameAsync(Enter, response);
        if (response.Result == 0) { await SyncAsync(s); await NotifyAsync(s.Tavern!.RoomId); }
    }

    public async Task ExitAsync(GameSession s, ExitBarRoomReq req)
    {
        using var scope = await EnterScopeAsync(s, room: true);
        await _daily.ReconcileAsync(s);
        await _gate.WaitAsync();
        int result;
        try { result = await ExitCoreAsync(s, req.BarRoomGuid, Now); }
        finally { _gate.Release(); }
        await s.SendGameAsync(Exit, new ExitBarRoomResp { Result = result });
        if (result == 0) { await SyncAsync(s); await _tasks.RefreshAsync(s); }
    }

    private async Task<int> ExitCoreAsync(GameSession s, long roomId, long cutoff)
    {
        if (!ValidLease(s)) return 0;
        if (s.Tavern!.RoomId != roomId) return ErrorCodes.FeatureUnavailable;
        await AdvanceCoreAsync(s, cutoff);
        var next = Copy(_state); var room = next.Rooms[roomId];
        room.Members.Remove(s.Role!.Id);
        if (room.Members.Count == 0) room.Closed = true;
        else if (room.Owner == s.Role.Id) room.Owner = room.Members.OrderBy(p => p.Value.JoinedMs).ThenBy(p => p.Key).First().Key;
        var meter = Copy(s.Tavern!); meter.RoomId = 0; meter.Lease = "";
        await CommitRoomAsync(next, s, meter, "leave");
        _live.Remove(s.Role.Id);
        _chat.LeaveTavernChannel(s, $"10_{roomId}");
        await NotifyAsync(roomId);
        return 0;
    }

    public Task GetAsync(GameSession s) => s.SendGameAsync(Get, ValidLease(s)
        ? new BarRoomGetResp { Info = Detail(_state.Rooms[s.Tavern!.RoomId]) }
        : new BarRoomGetResp { Result = ErrorCodes.FeatureUnavailable });

    private BarRoomSummary Summary(Room room)
    {
        var result = new BarRoomSummary
        {
            BarRoomGuid = room.Id, BarRoomType = room.Type, LanguageType = room.Language,
            BarRoomName = room.Name, RegionId = room.Area
        };
        foreach (var (id, member) in room.Members)
            result.MemberList.Add(new MemberInfo { MemberId = id, AreaId = member.Area, JoinTime = member.JoinedMs / 1000 });
        return result;
    }
    private BarRoomTotalDetail Detail(Room room) => new()
    {
        BarRoomSummary = Summary(room), OwnerGuid = room.Owner, CreateTime = room.CreatedMs / 1000,
        HistoryHot = new HistoryHotInfo(), IsCloseMic = true
    };
    private async Task NotifyAsync(long room)
    {
        foreach (var lease in _live.Values.Where(l => l.Room == room).ToArray())
            if (ValidLease(lease.Session))
                await lease.Session.SendGameAsync(Changed, new BarRoomInfoUpdateNotify { BarRoomGuid = room });
    }
}
