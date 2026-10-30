using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>實際 G 的酒館 82481–82514 與 PartyService type4；房間停留計時政策為推定。</summary>
public sealed partial class TavernService : IGameTicker
{
    public const uint Sync = 5043, Create = 82481, Enter = 82482, Exit = 82483, Transfer = 82489;
    public const uint SetInfo = 82494, Changed = 82506, UserEntered = 82507, Get = 82508;
    public const int Function = 2410;
    private readonly GameData _data;
    private readonly RoleStore _roles;
    private readonly AccountStore _accounts;
    private readonly TaskService _tasks;
    private readonly DailyActivityService _daily;
    private readonly FunctionService _functions;
    private readonly SessionRegistry _sessions;
    private readonly GameLoop _loop;
    private readonly TimeProvider _clock;
    private readonly ILogger<TavernService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<long, LiveLease> _live = new();
    private readonly Dictionary<GameSession, long> _detaching = new();
    private readonly ChatService _chat;
    private WorldState _state;
    private TimeSpan _nextTick;

    public TavernService(GameData data, RoleStore roles, AccountStore accounts, TaskService tasks,
        DailyActivityService daily, FunctionService functions, SessionRegistry sessions, GameLoop loop,
        TimeProvider clock, RoleHandover handover, WorldCoreService worldCore, ChatService chat, ReconnectService reconnects, ILogger<TavernService> log)
    {
        _data = data; _roles = roles; _accounts = accounts; _tasks = tasks; _daily = daily;
        _functions = functions; _sessions = sessions; _loop = loop; _clock = clock; _log = log;
        _state = accounts.ReadSync<byte[]?>(new TerritoryRealmReadPlan(RealmStateKind.Tavern)) is { } bytes
            ? JsonSerializer.Deserialize<WorldState>(bytes) ?? new() : new();
        // （推定）重啟不還原連線租約，也不補算停機時間。
        foreach (var room in _state.Rooms.Values) { room.Closed = true; room.Members.Clear(); }
        _chat = chat;
        worldCore.SyncTavernAsync = SyncAsync;
        chat.TavernAllowed = (s, group) => !group.StartsWith("10_", StringComparison.Ordinal)
            || long.TryParse(group.AsSpan(3), out long id) && group == $"10_{id}" && RoomAllowed(s, id);
        reconnects.TransportDisconnected += ObserveTransportLost;
        reconnects.TransportRestored += ObserveTransportRestored;
        daily.BeforeTavernReset = BeforeResetAsync;
        daily.PlanTavernRollover = PlanRollover;
        handover.Beginning += s => { if (ValidLease(s)) _detaching.TryAdd(s, Now); };
    }

    public sealed class WorldState { public Dictionary<long, Room> Rooms { get; set; } = new(); }
    public sealed class Room
    {
        public long Id { get; set; }
        public long Owner { get; set; }
        public long CreatedMs { get; set; }
        public string Name { get; set; } = "";
        public int Type { get; set; }
        public int Language { get; set; }
        public int Area { get; set; }
        public bool Closed { get; set; }
        public long LastEditMs { get; set; }
        public long LastTransferMs { get; set; }
        public Dictionary<long, Member> Members { get; set; } = new();
    }
    public sealed class Member { public int Area { get; set; } public long JoinedMs { get; set; } }
    /// <summary>純房間／時間控制欄位，沒有資產；一次性獎勵仍由任務帳本處理。</summary>
    public sealed class Meter
    {
        public long DayEpoch { get; set; }
        public long ActiveMs { get; set; }
        public long AccountedAtMs { get; set; }
        public long RoomId { get; set; }
        public string Lease { get; set; } = "";
    }
    private sealed record LiveLease(GameSession Session, AttachmentLease Attachment, string Token, long Room);
    private long Now => _clock.GetUtcNow().ToUnixTimeMilliseconds();
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value))!;
    private static ReservationKey RealmKey => ReservationKey.Entity("realm", nameof(RealmStateKind.Tavern));
    public static ReservationKey[] SharedKeys() => [RealmKey];

    public void Load(GameSession s)
    {
        _detaching.Remove(s);
        _transportGaps.Remove(s);
        var saved = _roles.LoadTavern(s.Role!.Id);
        var meter = saved is { } bytes ? JsonSerializer.Deserialize<Meter>(bytes) ?? new() : new() { AccountedAtMs = Now };
        // 尚無租約或已退出的登入不寫回，也不改游標；真正加入房間才開始計時與持久化。
        if (meter.RoomId != 0 || !string.IsNullOrEmpty(meter.Lease))
        {
            // 舊程序的活躍租約必須失效，保留當日已提交秒數，不補算離線期間。
            meter.RoomId = 0; meter.Lease = ""; meter.AccountedAtMs = Now;
            _roles.SaveTavern(s.Role.Id, JsonSerializer.SerializeToUtf8Bytes(meter));
        }
        s.Tavern = meter;
    }

    public Task SyncAsync(GameSession s)
    {
        var meter = s.Tavern ?? new();
        var info = new DBBarRoomInfo
        {
            BarRoomId = ValidLease(s) ? meter.RoomId : 0,
            BeginBarRoomTime = ValidLease(s) ? meter.AccountedAtMs / 1000 : 0,
            InBarRoomTime = (int)Math.Min(int.MaxValue, meter.ActiveMs / 1000),
            IsToDayActComplete = s.Tasks?.CompleteTaskNum.GetValueOrDefault(PublicParamConfig.BarroomTaskId(_data)) > 0
        };
        return CharacterService.SyncAsync(s, Sync, info);
    }

    private bool ValidLease(GameSession s) => s.Role is { } role && s.Tavern is { } meter
        && _live.TryGetValue(role.Id, out var lease) && ReferenceEquals(lease.Session, s)
        && ReferenceEquals(lease.Attachment, s.Attachment) && lease.Token == meter.Lease
        && lease.Room == meter.RoomId && _state.Rooms.TryGetValue(lease.Room, out var room)
        && !room.Closed && room.Members.ContainsKey(role.Id);

    private async Task<LegacyOwnerScope?> EnterScopeAsync(GameSession s, bool room)
    {
        if (_loop.Owners is not { } owners) throw new InvalidOperationException("Tavern needs a role owner.");
        var keys = room ? new[] { ReservationKey.Role(s.Role!.Id), RealmKey } : [ReservationKey.Role(s.Role!.Id)];
        var scope = await owners.EnterAsync("tavern/" + (room ? "room" : "time"), keys);
        if (s.Owner is { } owner) scope.Context.Pin(owner);
        return scope;
    }

    private async Task CommitRoomAsync(WorldState next, GameSession s, Meter meter, string label)
    {
        var plan = _roles.NewTransaction();
        plan.Add(new RealmStatePlan(RealmStateKind.Tavern, JsonSerializer.SerializeToUtf8Bytes(next)));
        plan.RoleBlob(s.Role!.Id, "tavern", JsonSerializer.SerializeToUtf8Bytes(meter));
        await _roles.CommitAsync(plan, "tavern/" + label, _ =>
        {
            _state = next; s.Tavern = meter;
            _transportGaps.Remove(s);
            if (meter.RoomId == 0) _live.Remove(s.Role!.Id);
            else _live[s.Role!.Id] = new(s, s.Attachment!, meter.Lease, meter.RoomId);
        });
    }

    public async Task LeaveAsync(GameSession s)
    {
        // 沒有酒館租約時不介入其他每日活動的結算；AFK 待領債務仍可依原流程登出保存。
        if (s.Role == null || s.Tavern == null || !ValidLease(s))
        {
            _detaching.Remove(s);
            _transportGaps.Remove(s);
            return;
        }
        using var scope = await EnterScopeAsync(s, room: true);
        await _daily.ReconcileAsync(s);
        await _gate.WaitAsync();
        try { await ExitCoreAsync(s, s.Tavern!.RoomId, _detaching.GetValueOrDefault(s, Now)); }
        finally { _gate.Release(); _detaching.Remove(s); }
    }

    public bool RoomAllowed(GameSession s, long room) => ValidLease(s) && s.Tavern!.RoomId == room;

    public void Tick(GameTime time)
    {
        if (time.Now < _nextTick) return;
        _nextTick = time.Now + TimeSpan.FromSeconds(1);
        foreach (var s in _sessions.All().Where(s => s.Role != null && s.Tavern != null && ValidLease(s)).ToArray())
            _loop.PostOwned(s, async () =>
            {
                try
                {
                    await ReconcileAsync(s);
                }
                catch (Exception e) { _log.LogWarning(e, "Tavern time remains retryable for role {Role}", s.Role?.Id); }
            }, "tavern/time");
    }
}
