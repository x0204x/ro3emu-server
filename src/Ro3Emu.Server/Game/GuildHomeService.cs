using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The guild base: DungeonConfig GuildSettingConfig.GuildMap (4000101, gameplay type 7
/// GuildHome). 32256 GUILD_ENTER_HOME replies {result, guildSceneId}; its client handler is empty, so the server moves
/// the player itself with the ordinary private-instance handoff (70010 reason 3, then the client's 30005). The original
/// keeps one scene per guild (DBGuildHome.sceneID); here one live InstanceService room per guild is created on the
/// first entry and closes with its last member (inferred: the persisted scene id behaves the same for players).
/// Guild events (Bonfire Party, later Dracula / Hunting Challenge / rally) attach to <see cref="RoomOf"/>.
/// </summary>
public sealed class GuildHomeService : IGameTicker
{
    public const uint EnterHome = 32256;
    public const long DefaultScene = 4000101;
    /// <summary>GuildHomepageShowConfig[1]._iFunction (Guild, condition 2000010 = Lv21).</summary>
    private const int GuildFunction = 13;
    /// <summary>SwitchSceneReason_EnterDungeon, as 30400 (inferred for 32256).</summary>
    private const int EnterReason = 3;

    private readonly GameData _data;
    private readonly GuildStore _guilds;
    private readonly FunctionService _functions;
    private readonly InstanceService _instances;
    private readonly SceneService _scene;
    private readonly DungeonService _dungeons;
    private readonly GameLoop _loop;
    private readonly Dictionary<long, InstanceService.Room> _homes = new();
    private readonly Dictionary<long, long> _guildOfRoom = new();
    private readonly HashSet<GameSession> _ejecting = new();
    private long _lastSecond = -1;
    private readonly TimeProvider _time;

    public GuildHomeService(GameData data, GuildStore guilds, FunctionService functions, InstanceService instances,
        SceneService scene, DungeonService dungeons, GameLoop loop, TimeProvider time)
    {
        this._data = data; this._guilds = guilds; this._functions = functions; this._instances = instances;
        this._scene = scene; this._dungeons = dungeons; this._loop = loop; this._time = time;
        Scene = data.Table("GuildSettingConfig").TryGetValue("GuildMap", out var row)
            && row.TryGetProperty("_kValue", out var value) && GameData.Number(value) is { } map && map > 0
            ? (long)map : DefaultScene;
        // (inferred, spec §6.5) Exiting the home revives a dead member on return, as ordinary dungeon runs do.
        dungeons.RecoverDeadOnLeave = roomId => GuildOfRoom(roomId) != null;
    }

    /// <summary>GuildSettingConfig.GuildMap, 4000101 when the export lacks it.</summary>
    public long Scene { get; }

    /// <summary>The guild's live home room, or null when nobody is inside.</summary>
    public InstanceService.Room? RoomOf(long guildId) =>
        _homes.TryGetValue(guildId, out var room) && !room.Closed ? room : null;

    public long? GuildOfRoom(long roomId) =>
        _guildOfRoom.TryGetValue(roomId, out long guild) && RoomOf(guild)?.Id == roomId ? guild : null;

    /// <summary>Every live home room with its guild (event owners iterate these).</summary>
    public IReadOnlyList<(long Guild, InstanceService.Room Room)> Rooms =>
        _homes.Where(p => !p.Value.Closed).Select(p => (p.Key, p.Value)).ToArray();

    /// <summary>The session's guild when the durable roster and the session's own guild view agree.</summary>
    public long? GuildId(GameSession session)
    {
        if (session.Role == null || session.Guild?.GuildId is not > 0) return null;
        var owned = _guilds.ForRole(session.Role.Id);
        return owned?.Id == session.Guild.GuildId && owned.Members.ContainsKey(session.Role.Id) ? owned.Id : null;
    }

    /// <summary>Loaded inside its own guild's home.</summary>
    public bool InHome(GameSession session, out long guild)
    {
        guild = 0;
        if (session.SceneEntry != Scene || session.SceneLoading || GuildId(session) is not { } id
            || RoomOf(id) is not { } room || room.Id != session.InstanceId) return false;
        guild = id;
        return true;
    }

    /// <summary>first failure wins.</summary>
    private int Admission(GameSession s, out long guild)
    {
        guild = 0;
        if (s.Role == null || s.Progress == null) return ErrorCodes.Parameter;
        if (GuildId(s) is not { } id) return ErrorCodes.NotInGuild;
        guild = id;
        if (!_functions.IsOpen(s, GuildFunction)) return ErrorCodes.FeatureUnavailable;
        if (s.SceneLoading || s.Handoff is { Completing: false } || s.SceneRecoveryRequired
            || s.SceneTransitionGate.CurrentCount == 0) return ErrorCodes.SwitchingScenes;
        if (s.InstanceId != 0 && s.SceneEntry == Scene && RoomOf(id)?.Id == s.InstanceId)
            return ErrorCodes.AlreadyInGuildHome;
        if (s.InstanceId != 0 || s.TowerAttempt != null || s.DungeonAttempt != null) return ErrorCodes.DungeonBusy;
        if (_dungeons.Preparing(s)) return ErrorCodes.DungeonPreparing;
        if (s.Dead) return ErrorCodes.FeatureUnavailable;
        if ((s.StatusBits & (1L << 15)) != 0) return ErrorCodes.ActionInBattle;
        if (_scene.BornPoint(Scene) == null) return ErrorCodes.DungeonDataMissing;
        if (RoomOf(id) is { } room && room.ActiveMembers.Count >= Capacity()) return ErrorCodes.DungeonBusy;
        return 0;
    }

    private int Capacity() => _data.Row("DungeonConfig", Scene) is { } row && GameData.Int(row, "_iRoleCntLimit") is > 0 and var limit
        ? (int)Math.Min(limit, int.MaxValue) : 300;

    /// <summary>Create or join the guild's room. No await separates the lookup from the store, so two first entries
    /// of one guild cannot create two rooms (the default Realm reservation serializes the routes as well).</summary>
    private (int Result, InstanceService.Room? Room) Reserve(GameSession s)
    {
        int result = Admission(s, out long guild);
        if (result != 0) return (result, null);
        if (RoomOf(guild) is { } room)
            return _instances.AddParticipant(room, s) ? (0, room) : (ErrorCodes.DungeonBusy, null);
        if (_homes.TryGetValue(guild, out var closed)) _guildOfRoom.Remove(closed.Id);
        room = _instances.Create(Scene, [s]);
        _homes[guild] = room;
        _guildOfRoom[room.Id] = guild;
        return (0, room);
    }

    /// <summary>32256: reply, then the 70010 handoff into the guild's room.</summary>
    public async Task EnterAsync(GameSession s)
    {
        var (result, room) = Reserve(s);
        await s.SendGameAsync(EnterHome, new GuildEnterHomeResp { Result = result, GuildSceneId = room?.Id ?? 0 });
        // A refused or failed handoff already detached this member inside InstanceService.
        if (room != null) await _instances.EnterAsync(room, s, EnterReason);
    }

    /// <summary>30400 for the home scene (the GM tool's GmEnterDungeon 4000101): same rules, EnterDungeonRsp reply.</summary>
    public async Task<bool> TryEnterViaDungeonAsync(GameSession s, EnterDungeonReq req)
    {
        if (req.SceneEntry != Scene) return false;
        var (result, room) = Reserve(s);
        await s.SendGameAsync(DungeonService.Enter, new EnterDungeonRsp { Result = result });
        if (room != null) await _instances.EnterAsync(room, s, EnterReason);
        return true;
    }

    public void Tick(GameTime gameTime)
    {
        long second = _time.GetUtcNow().ToUnixTimeSeconds();
        if (_lastSecond == second) return;
        _lastSecond = second;
        foreach (var (guild, room) in _homes.ToArray())
        {
            if (room.Closed)
            {
                _homes.Remove(guild);
                _guildOfRoom.Remove(room.Id);
                continue;
            }
            foreach (var member in room.ActiveMembers)
            {
                var s = member.Session;
                // (inferred) A member who left, was kicked or whose guild dissolved is moved back outside without a
                // 30402 reply; one still loading is checked again once loaded.
                if (s.Role == null || s.SceneLoading || s.InstanceId != room.Id || s.SceneEntry != Scene
                    || GuildId(s) == guild || !_ejecting.Add(s)) continue;
                var role = s.Role;
                _loop.TrackLegacyDetached("guild home eject", async () =>
                {
                    try
                    {
                        if (ReferenceEquals(s.Role, role) && !s.SceneLoading && s.InstanceId == room.Id
                            && s.SceneEntry == Scene && GuildId(s) != guild)
                            await _instances.LeaveAsync(s, recoverDeadOnReturn: true);
                    }
                    finally { _ejecting.Remove(s); }
                });
            }
        }
    }
}
