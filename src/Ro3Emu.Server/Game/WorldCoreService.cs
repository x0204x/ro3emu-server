using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Durable visited maps; transient subscriptions and movement state belong to the selected session.</summary>
public sealed class WorldRoleState
{
    public HashSet<long> VisitedMaps { get; set; } = new();
}

public sealed class WorldSessionState
{
    public WorldRoleState Saved { get; set; } = new();
    public bool MiniMap { get; set; }
    public ulong NameBoardLimit { get; set; }
    public string NameBoardSnapshot { get; set; } = "";
    public string TeamTargetSnapshot { get; set; } = "";
    public bool Flying { get; set; }
    public long LastFlyMs { get; set; }
    public bool Watching { get; set; }
    public EFightStatus? FightStatus { get; set; }
    public HashSet<long> Hatred { get; } = new();
    public HashSet<long> Monitored { get; } = new();
    public Dictionary<long, CustomData> CustomData { get; } = new();
    public long QueueScene { get; set; }
    public long LastEscapeMs { get; set; }
    public long LastHelpMs { get; set; }
    public long ReviveSource { get; set; }
    public long ReviveExpiresMs { get; set; }
    public long ResurrectionCastId { get; set; }
    public long ReviveHpRate { get; set; } = 10000;
    public long ReviveSpRate { get; set; } = 10000;
    public long TemporaryLeaveUntilMs { get; set; }
}

/// <summary>
/// Everyday scene/player contracts (MG_Scene/Entity/Player).
/// Client subscriptions are scoped to WorldScene, including private instances; a reconnect replays
/// authoritative AUTO_SYNC snapshots rather than acknowledging a request without its requested data.
/// </summary>
public sealed partial class WorldCoreService : IGameTicker
{
    public const uint VisitedMaps = 1016;
    public const uint PlayerDetail = 30013, SceneQueue = 30014, QueueCancel = 30015, ServerInfo = 30130;
    public const uint ReliveAccept = 31009, ReliveAccepted = 31010, ReliveHelp = 31011, ReliveInvitation = 31012, ReliveItem = 31019;
    public const uint LifeStatus = 31024, AoiInfo = 31036, AoiUnview = 31038, MiniMap = 31042, NameBoards = 31045, QueryNpc = 31047;
    public const uint FightStatus = 31050, Hatred = 31051, MonsterLocked = 31052, WatchState = 31055;
    public const uint CustomChange = 31018, CustomAll = 31071, MonitorCustom = 31072, MonitorSync = 31073;
    public const uint TempLeave = 31077, ForceLeave = 31078, RecoverLeave = 31080, FlySwitch = 31083, FlyMove = 31084;
    public const uint FreeFall = 31087, FixLand = 31088, TeleportObject = 31089, PerformanceWatch = 31090, StuckEscape = 31136;
    public const uint TeamTargets = 30111;

    private readonly World world;
    private readonly WorldData worldData;
    private readonly GameData data;
    private readonly RoleStore roles;
    private readonly SessionRegistry sessions;
    private readonly SceneService scene;
    private readonly CharacterService character;
    private readonly CombatService combat;
    private readonly TeamService teams;
    private readonly DungeonService dungeons;
    private readonly DailyActivityService daily;
    private readonly SeasonRefineService seasonRefine;
    private readonly FirstHeartService firstHeart;
    private readonly PersonalService personal;
    private readonly TimeProvider clock;
    private readonly GameLoop loop;
    private readonly PublicLineManager lines;
    private bool ticking;
    private long nextSnapshotMs;
    private readonly ulong serverOpenSeconds;

    public WorldCoreService(World world, WorldData worldData, GameData data, RoleStore roles, SessionRegistry sessions,
        SceneService scene, CharacterService character, CombatService combat, MonsterService monsters, TeamService teams,
        GameLoop loop, TimeProvider clock, GmCommands gm, DungeonService dungeons, SeasonService seasons,
        DailyActivityService daily, SeasonRefineService seasonRefine, FirstHeartService firstHeart, PersonalService personal, PublicLineManager lines, ChatService chat)
    {
        this.world = world; this.worldData = worldData; this.data = data; this.roles = roles; this.sessions = sessions;
        this.scene = scene; this.character = character; this.combat = combat; this.teams = teams; this.clock = clock;
        this.loop = loop;
        this.lines = lines;
        this.dungeons = dungeons;
        this.daily = daily;
        this.seasonRefine = seasonRefine;
        this.firstHeart = firstHeart;
        this.personal = personal;
        serverOpenSeconds = (ulong)seasons.StartTime;
        scene.AddOwnedAfterLoaded(LoadedAsync);
        scene.Leaving.Add(LeavingAsync);
        monsters.TargetChanged.Add(TargetChangedAsync);
        combat.ReviveOffer = OfferReviveAsync;
        combat.ReviveCheck = dungeons.ExternalReviveError;
        combat.ReviveItemSkill = ReviveItemSkillAsync;
        character.WingJump = WingJumpAsync;
        character.RegisterItemAction(TaskJumpMapAction, UseQuestWingAsync);
        scene.QueuePublicAdmission = QueueAdmissionAsync;
        scene.PreferredLine = PreferredLine;
        lines.HasPendingWork.Add(monsters.HasPendingWork);
        lines.HasPendingWork.Add(combat.HasPendingMapWork);
        lines.Retiring.Add(monsters.RetireRuntimeAsync);
        lines.Retiring.Add(chat.RetireLineAsync);
        lines.Retiring.Add(key => { sceneVariables.Remove(key.WorldScene); return Task.CompletedTask; });
        gm.Register("line", "[n|new|info] (TEST AID)", scene.LineCommandAsync);
        gm.Register("worldcustom", "id value", async (s, a) => await SetCustomAsync(s, new CustomData
        { Id = long.Parse(a[0]), CustomParam = new CustomParam { Tp = (CustomParam.Types.ParamType)3, LongParam = long.Parse(a[1]) } }));
        gm.Register("worldleave", "seconds", (s, a) => BeginTemporaryLeaveAsync(s, int.Parse(a[0])));
        gm.Register("scenevar", "id value", (s, a) => SetSceneVariableAsync(s.WorldScene, new CommonPublicVar { VarId = int.Parse(a[0]), Vi64 = { long.Parse(a[1]) } }));
    }

    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    private GameSession? Player(long uid) => sessions.All().FirstOrDefault(s => s.Role?.Id == uid);
    private int PreferredLine(GameSession s, long map)
    {
        if (s.Role == null) return 0;
        long leader = teams.TeamOf(s.Role.Id).Leader;
        var target = leader != 0 && leader != s.Role.Id ? Player(leader) : null;
        return target is { InstanceId: 0, SceneLoading: false } && target.SceneEntry == map ? target.LineNumber : 0;
    }
    private static bool Ready(GameSession s) => s.Role != null && s.Progress != null && !s.SceneLoading;

    /// <summary>1016/30130: visits are durable; LC_DAL_ServerInfo divides the two Unix-second clocks by 86400.</summary>
    public async Task LoginAsync(GameSession s)
    {
        s.WorldState = new WorldSessionState
        {
            Saved = roles.LoadWorldState(s.Role!.Id) is { } saved
                ? JsonSerializer.Deserialize<WorldRoleState>(saved) ?? new() : new(),
        };
        await s.SendGameAsync(ServerInfo, new ServerBasicInfo
        {
            ServerOpenTime = serverOpenSeconds, ServerLogicTime = (ulong)(Now / 1000),
        });
        if (s.WorldState.Saved.VisitedMaps.Count > 0) await VisitsAsync(s);
    }

    private Task VisitsAsync(GameSession s) => s.SendGameAsync(VisitedMaps,
        new SyncRoleVisitMapInfoNtf { Mapid = { s.WorldState.Saved.VisitedMaps.Order() } });

    // PBEnumDefine.PBAsset_Type and LC_AssetInit.AssetTypeToDAL use category ids, not AUTO_SYNC message ids.
    private static readonly Dictionary<long, uint[]> AssetDatasets = new()
    {
        [0] = [5021], [1] = [5066], [3] = [5056], [4] = [5012], [5] = [5025], [6] = [5009], [7] = [5026], [8] = [5007], [9] = [5047],
        [10] = [5031], [11] = [5035], [12] = [5059], [14] = [5061], [15] = [5019], [17] = [5065], [18] = [5004],
        [19] = [5009, 5026, 5057], [20] = [5040], [21] = [5017], [22] = [5011], [23] = [5027], [26] = [5034], [27] = [5058],
        [30] = [5060], [31] = [5067], [35] = [5024], [36] = [5032], [37] = [5057], [38] = [5005], [39] = [5062],
        [40] = [5016], [41] = [5043], [43] = [5006, 5041], [44] = [5003], [46] = [5008], [47] = [5022], [48] = [5013],
        [51] = [5015], [52] = [5033], [54] = [5023], [55] = [5018], [56] = [5020], [57] = [5064], [58] = [5063],
        [59] = [5010], [60] = [5001], [63] = [5002], [64] = [5038], [65] = [5039], [66] = [5042], [68] = [5044],
        [69] = [5045], [71] = [5046], [72] = [5049], [73] = [5050], [75] = [5052], [76] = [5053], [78] = [5055],
        [79] = [5068], [81] = [5069], [82] = [5070], [84] = [5071],
    };

    /// <summary>1023: an empty category list means reconnect/all; replay materialized datasets before marking assets ready.</summary>
    public async Task AssetsAsync(GameSession s, PlayerAssetReq req)
    {
        var response = new PlayerAssetResp();
        var requested = req.AssetType.Count == 0 ? AssetDatasets.Keys.ToArray() : req.AssetType.Distinct().ToArray();
        var emitted = new HashSet<uint>();
        foreach (long type in requested)
        {
            if (!AssetDatasets.TryGetValue(type, out var ids)) continue;
            bool present = false;
            foreach (uint id in ids)
            {
                if (!s.Synced.TryGetValue(id, out var snapshot)) continue;
                present = true;
                if (!emitted.Add(id)) continue;
                if (id == DailyActivityService.StageSync) { await daily.ReplayClaimsAsync(s); continue; }
                if (id == SeasonRefineService.SeasonRefineSync) { await seasonRefine.ReplayAsync(s); continue; }
                if (id == FirstHeartService.Sync) { await firstHeart.ReplayAsync(s); continue; }
                if (id == PersonalService.Sync) { await personal.ReplayAsync(s); continue; }
                s.Synced.Remove(id); // Force the existing AUTO_SYNC encoder's full-state path.
                await CharacterService.SyncAsync(s, id, snapshot);
            }
            if (present) response.AssetType.Add(type);
        }
        await s.SendGameAsync(MsgId.PlayerAsset, response);
    }

    private async Task LoadedAsync(GameSession s)
    {
        var handoff = s.Handoff;
        long generation = s.SceneGeneration;
        bool CurrentScene() => s.Handoff == handoff && s.SceneGeneration == generation && !s.SceneLoading;
        using var held = await CharacterService.Lock(s);
        if (!CurrentScene()) return;
        if (!s.WorldState.Saved.VisitedMaps.Contains(s.SceneEntry))
        {
            var next = new WorldRoleState { VisitedMaps = new(s.WorldState.Saved.VisitedMaps) };
            next.VisitedMaps.Add(s.SceneEntry);
            var transaction = roles.NewTransaction();
            transaction.RoleBlob(s.Role!.Id, "world_state", JsonSerializer.SerializeToUtf8Bytes(next));
            await roles.CommitAsync(transaction, "world/visited-map", _ => s.WorldState.Saved = next);
            if (!CurrentScene()) return;
            await VisitsAsync(s);
        }
        if (!CurrentScene()) return;
        // LC_Scene_Sync._syncAOIInfos indexes #aoi_data; keep at least self in a full snapshot.
        var info = new AoIInfoTotal { AoITickMs = 50, TickNowMs = (ulong)Now };
        info.AoiData.Add(new AoIInfoSingle { EntityUuid = (ulong)s.Role!.Id });
        info.EntityTypes[(ulong)s.Role.Id] = 3;
        foreach (var other in world.Viewers(s))
        {
            info.AoiData.Add(new AoIInfoSingle { EntityUuid = (ulong)other.Role!.Id });
            info.EntityTypes[(ulong)other.Role.Id] = 3;
        }
        foreach (var unit in s.VisibleUnits)
        {
            info.AoiData.Add(new AoIInfoSingle { EntityUuid = (ulong)unit.Uid });
            info.EntityTypes[(ulong)unit.Uid] = unit.Entity().EntityType;
        }
        await s.SendGameAsync(AoiInfo, info);
        if (!CurrentScene()) return;
        await SyncFightAsync(s, true);
        if (!CurrentScene()) return;
        await BoardsAsync(s);
        if (!CurrentScene()) return;
        await SceneVariablesAsync(s);
        if (!CurrentScene()) return;
        if (s.WorldState.CustomData.Count > 0) await s.SendGameAsync(CustomAll, new CustomDataChange
        { EntityId = s.Role!.Id, CustomDatas = { s.WorldState.CustomData.Values } });
    }

    public async Task LeavingAsync(GameSession s)
    {
        admissions.Remove(s);
        if (s.InstanceId != 0 && world.PlayersIn(s.WorldScene).Count <= 1) sceneVariables.Remove(s.WorldScene);
        var state = s.WorldState;
        state.ResurrectionCastId = 0;
        if (state.Hatred.Count > 0)
        {
            await s.SendGameAsync(Hatred, new PlayerBeHatredNotify { RemoveEnemyList = { state.Hatred } });
            foreach (var id in state.Hatred) await s.SendGameAsync(MonsterLocked, new PlayerBeMonsterLockedNotify { RemoveEnemy = id });
        }
        state.Hatred.Clear(); state.Monitored.Clear(); state.MiniMap = false; state.Flying = false;
        state.Watching = false; state.ReviveSource = 0; state.ReviveExpiresMs = 0;
        state.NameBoardSnapshot = ""; state.QueueScene = 0; state.TemporaryLeaveUntilMs = 0;
        state.FightStatus = null;
    }

    /// <summary>30013: return only characters in this exact public/private scene; the requested flags are echoed.</summary>
    public async Task DetailAsync(GameSession s, GetPlayerInSceneDetailReq req)
    {
        var response = new GetPlayerInSceneDetailResp
        {
            BGetGuildOfficals = req.BGetGuildOfficals, BGetName = req.BGetName, BGetGuildFull = req.BGetGuildFull,
        };
        foreach (var id in req.Ids.Distinct().Take(256))
            if (Player((long)id) is { Role: { } role } p && p.WorldScene == s.WorldScene && !p.SceneLoading)
                response.Data.Add(new PlayerDetailInScene
                {
                    Uid = role.Id, Name = req.BGetName ? role.Name : "", GuildId = p.Guild?.GuildId ?? 0,
                    Official = req.BGetGuildOfficals || req.BGetGuildFull ? p.Guild?.Official ?? 0 : 0,
                    HonorOfficial = req.BGetGuildOfficals || req.BGetGuildFull ? p.Guild?.HonorOfficial ?? 0 : 0,
                    GuildName = req.BGetGuildFull ? p.Guild?.GuildName ?? "" : "",
                    Icon = req.BGetGuildFull ? p.Guild?.GuildIcon?.Clone() : null,
                });
        await s.SendGameAsync(PlayerDetail, response);
    }

    /// <summary>31043/31042: LC_DAL_MapLine subscribes only while the map is open.</summary>
    public async Task MiniMapAsync(GameSession s, MiniMapStatus req)
    {
        s.WorldState.MiniMap = req.IsOpen;
        if (req.IsOpen && Ready(s)) await MiniMapSnapshotAsync(s);
    }

    private async Task MiniMapSnapshotAsync(GameSession s)
    {
        var response = new MiniMapMsg();
        foreach (var p in world.PlayersIn(s.WorldScene).Where(Ready))
            response.MiniInfo.Add(new MiniMapInfo { Uid = (ulong)p.Role!.Id, Pos = p.Position.Clone(), Camp = 1, Hp = (ulong)Math.Max(0, character.Stats(p)[2000]) });
        foreach (var m in world.AllUnits().OfType<MonsterUnit>().Where(m => m.WorldScene == s.WorldScene && !m.Dead))
            response.MiniInfo.Add(new MiniMapInfo { Uid = (ulong)m.Uid, Pos = m.Pos, Camp = (ulong)(m.CampOverride ?? m.Template.Camp), NpcConfigId = (ulong)m.Template.NpcId, Hp = (ulong)Math.Max(0, m.Hp) });
        await s.SendGameAsync(MiniMap, response);
    }

    public async Task ViewAsync(GameSession s, ulong nameBoardLimit)
    {
        s.WorldState.NameBoardLimit = Math.Min(nameBoardLimit, 1000);
        s.WorldState.NameBoardSnapshot = "";
        if (Ready(s)) await BoardsAsync(s);
    }

    /// <param name="boards">One entry per player for this snapshot, shared by every viewer: a row needs the subject's
    /// stat sheet, and building it per viewer made the 1 s snapshot O(viewers x players) stat sheets per map.</param>
    private async Task BoardsAsync(GameSession s, Dictionary<GameSession, EntityNameBoard>? boards = null)
    {
        EntityNameBoard Board(GameSession p)
        {
            if (boards == null) return NameBoard(p);
            if (!boards.TryGetValue(p, out var board)) boards[p] = board = NameBoard(p);
            return board;
        }
        var response = new NameBoardListNotify();
        foreach (var p in world.PlayersIn(s.WorldScene).Where(p => p != s && Ready(p) && !s.Visible.Contains(p))
                     .OrderBy(p => Distance(s.Position, p.Position)).Take((int)s.WorldState.NameBoardLimit))
            response.Datas.Add(Board(p));
        // (inferred compatibility) pb.no_decode_default_array makes an empty datas field nil, but the
        // consumer does #datas. After ENTITYSELF, a real self row is skipped by HasUnitOrCached and
        // RemoveOldUnits({self}) removes every old simple unit (LC_SimpleUnit_Manager:55-112,509-570,735-771).
        if (response.Datas.Count == 0) response.Datas.Add(Board(s));
        string signature = Convert.ToBase64String(response.ToByteArray());
        if (signature == s.WorldState.NameBoardSnapshot) return;
        await s.SendGameAsync(NameBoards, response);
        s.WorldState.NameBoardSnapshot = signature;
    }

    private EntityNameBoard NameBoard(GameSession p)
    {
        // Max HP from the property list CharacterService.Refresh/SetHpSpAsync keep current, current HP from the
        // authoritative session value (unset = full), as the stat sheet does; a full sheet per board was 0.75 ms
        // per player per second (113 ms per snapshot at 150 players in one map).
        long maxHp = 0;
        foreach (var (id, value) in p.EntityProperties) if (id == 101) { maxHp = value; break; }
        if (maxHp <= 0) maxHp = character.Stats(p)[101]; // properties not built yet
        long hp = Math.Clamp(p.CurrentHp ?? maxHp, 0, maxHp);
        return new EntityNameBoard
        {
            Uid = (ulong)p.Role!.Id, CampId = 1, Lv = (ulong)p.Progress!.Level,
            GuildId = (ulong)(p.Guild?.GuildId ?? 0), HpMax = (ulong)Math.Max(0, maxHp), CurHp = (ulong)Math.Max(0, hp),
            // (inferred) Lua uses integer world cells (+0.5 for X/Z), not centimetres; preserve signed integer bits.
            PosX = unchecked((ulong)(long)MathF.Floor(p.Position.X)), PosY = unchecked((ulong)(long)MathF.Floor(p.Position.Y)),
            PosZ = unchecked((ulong)(long)MathF.Floor(p.Position.Z)),
        };
    }

    /// <summary>31047: intersect candidate scene-editor point ids with live NPCs, never echo nonexistent spawns.</summary>
    public Task QueryNpcAsync(GameSession s, QueryCurrentNpcReq req)
    {
        var response = new QueryCurrentNpcResp();
        var map = worldData.Map(s.SceneEntry);
        foreach (var point in req.PointIDs.Distinct().Take(256))
            if (map?.Npcs.Any(n => n.NpcId == req.NpcConfigID && (GameData.Int(n.Entry, "Id") == point || GameData.Int(n.Entry, "AreaID") == point)) == true
                || world.AllUnits().OfType<MonsterUnit>().Any(m => m.WorldScene == s.WorldScene && !m.Dead && m.Home.Id == point && m.Template.NpcId == req.NpcConfigID))
                response.PointIDs.Add(point);
        return s.SendGameAsync(QueryNpc, response);
    }

    /// <summary>31038 is an unview request, not authorization to remove another player's authoritative entity.</summary>
    public Task UnviewAsync(GameSession s, SyncAoIUnviewReq req)
    {
        var response = new AoIViewChangeData();
        var visible = s.Visible.Where(p => p.Role != null).Select(p => p.Role!.Id).Concat(s.VisibleUnits.Select(u => u.Uid)).ToHashSet();
        foreach (long id in req.Uuids.Distinct().Take(256))
            if (id != s.Role!.Id && !visible.Contains(id)) response.LeaveAoi.Add((ulong)id);
        return s.SendGameAsync(AoiUnview, response);
    }

    private async Task TargetChangedAsync(MonsterUnit monster, GameSession? before, GameSession? after)
    {
        if (before?.Role != null && before.WorldState.Hatred.Remove(monster.Uid))
        {
            await before.SendGameAsync(Hatred, new PlayerBeHatredNotify { RemoveEnemyList = { monster.Uid } });
            await before.SendGameAsync(MonsterLocked, new PlayerBeMonsterLockedNotify { RemoveEnemy = monster.Uid });
        }
        if (after?.Role != null && after.WorldScene == monster.WorldScene && after.WorldState.Hatred.Add(monster.Uid))
        {
            await after.SendGameAsync(Hatred, new PlayerBeHatredNotify { AddEnemyList = { monster.Uid } });
            await after.SendGameAsync(MonsterLocked, new PlayerBeMonsterLockedNotify { AddEnemy = monster.Uid });
        }
    }

    private async Task SyncFightAsync(GameSession s, bool initial = false)
    {
        var status = s.Dead ? EFightStatus.Dead : (s.StatusBits & (1L << 15)) != 0 ? EFightStatus.Fight : EFightStatus.Idle;
        if (!initial && s.WorldState.FightStatus == status) return;
        var previous = s.WorldState.FightStatus;
        await ToSelfAndViewersAsync(s, FightStatus, new PlayerFightStatusNotify { PlayerId = s.Role!.Id, Status = status });
        if (initial || (previous == EFightStatus.Dead) != s.Dead)
            await ToSelfAndViewersAsync(s, LifeStatus, new PlayerLifeStatusNotify { EntityId = s.Role.Id, PlayerLifeStatus = (EPlayerLifeStatus)(s.Dead ? 1 : 0) });
        s.WorldState.FightStatus = status;
    }

    public async Task WatchAsync(GameSession s, ScenePerformaceWatchReq req)
    {
        if (!Ready(s) || s.Dead) { await ErrorAsync(s, PerformanceWatch, ErrorCodes.SwitchingScenes); return; }
        s.WorldState.Watching = req.StartWatch;
        await ToSelfAndViewersAsync(s, WatchState, new EntityWatchStateNotify { Uuid = (ulong)s.Role!.Id, WatchState = (WatchState)(req.StartWatch ? 1 : 0) });
    }

    public async Task MonitorAsync(GameSession s, MonitorCustomDataReq req)
    {
        var valid = req.EntityIds.Count <= 256 && req.EntityIds.All(id => Entity(s, id) != null);
        if (valid)
        {
            s.WorldState.Monitored.Clear(); s.WorldState.Monitored.UnionWith(req.EntityIds);
            foreach (long id in s.WorldState.Monitored)
            {
                var snapshot = new CustomDataChange { EntityId = id };
                snapshot.CustomDatas.Add(Entity(s, id)!.CustomDatas);
                if (snapshot.CustomDatas.Count > 0) await s.SendGameAsync(MonitorSync, snapshot);
            }
        }
        await s.SendGameAsync(MonitorCustom, new MonitorCustomDataResp { Result = valid ? 0 : ErrorCodes.Parameter });
    }

    private EntityBase? Entity(GameSession viewer, long id)
    {
        if (Player(id) is { } p && Ready(p) && p.WorldScene == viewer.WorldScene && (p == viewer || viewer.Visible.Contains(p))) return RoleSessionService.PlayerEntity(p);
        if (viewer.VisibleUnits.FirstOrDefault(u => u.Uid == id && u.WorldScene == viewer.WorldScene) is { } unit) return unit.Entity();
        return worldData.Npc(id) is { } npc && npc.SceneEntry == viewer.SceneEntry ? SceneService.NpcEntity(npc) : null;
    }

    public async Task EscapeAsync(GameSession s, StuckEscapeReq req)
    {
        int error = !Ready(s) ? ErrorCodes.SwitchingScenes : s.Dead ? ErrorCodes.UseWhileDead
            : data.Row("MapConfig", s.SceneEntry) is not { } row || GameData.Int(row, "_StuckEscape") == 0 ? ErrorCodes.Parameter
            : Now - s.WorldState.LastEscapeMs < 10_000 ? ErrorCodes.Cooldown : 0;
        var at = worldData.Map(s.SceneEntry)?.Born;
        if (at == null) error = ErrorCodes.SceneMissing;
        await s.SendGameAsync(StuckEscape, new StuckEscapeRsp { Result = error });
        if (error != 0) return;
        s.WorldState.LastEscapeMs = Now; // (inferred) 10-second repeat guard; client MapConfig controls availability.
        await scene.TeleportInMapAsync(s, at!.Value);
    }

    public async Task RecoverAsync(GameSession s)
    {
        if (s.WorldState.TemporaryLeaveUntilMs <= Now) { await ErrorAsync(s, RecoverLeave, ErrorCodes.Parameter); return; }
        s.WorldState.TemporaryLeaveUntilMs = 0;
        await s.SendGameAsync(TempLeave, new TempLeaveNtf { CountdownSeconds = 0 });
    }

    public void Tick(GameTime time)
    {
        if (ticking) return;
        ticking = true;
        // Queue admissions can await a scene callback while another player cancels/replaces an admission.
        // Durable leaving hooks acquire their own owner scopes before their existing gates.
        loop.PostRuntime(async () => { try { await TickAsync(); } finally { ticking = false; } });
    }

    private async Task TickAsync()
    {
        foreach (var s in sessions.All().Where(Ready)) await SyncFightAsync(s);
        if (Now < nextSnapshotMs) return;
        nextSnapshotMs = Now + 1000;
        foreach (var s in sessions.All()) scene.ExpireLineLoad(s);
        await lines.RetireEmptyAsync();
        var boards = new Dictionary<GameSession, EntityNameBoard>();
        foreach (var s in sessions.All().Where(Ready))
        {
            await BoardsAsync(s, boards);
            if (s.WorldState.MiniMap) await MiniMapSnapshotAsync(s);
            if (s.WorldState.Flying && !CanFly(s)) await LandAsync(s, s.Position, s.Face);
            if (s.WorldState.TemporaryLeaveUntilMs > 0 && s.WorldState.TemporaryLeaveUntilMs <= Now) await ForceLeaveAsync(s);
        }
        await AdmitQueuedAsync();
        foreach (var s in sessions.All().Where(s => Ready(s) && teams.TeamOf(s.Role!.Id).TeamId == 0 && s.WorldState.TeamTargetSnapshot.Length > 0))
        {
            await s.SendGameAsync(TeamTargets, new TeamTargetAndEnemy());
            s.WorldState.TeamTargetSnapshot = "";
        }
        foreach (var group in sessions.All().Where(Ready).GroupBy(s => (Team: teams.TeamOf(s.Role!.Id).TeamId, Scene: s.WorldScene)).Where(g => g.Key.Team != 0))
        {
            var member = group.First();
            var leader = Player(teams.TeamOf(member.Role!.Id).Leader);
            var packet = new TeamTargetAndEnemy { LeaderTargetId = leader?.WorldScene == group.Key.Scene ? leader.LockTarget : 0 };
            packet.TeamEnemyList.Add(group.SelectMany(s => s.WorldState.Hatred).Distinct().Order());
            string signature = Convert.ToBase64String(packet.ToByteArray());
            foreach (var s in group)
            {
                if (s.WorldState.TeamTargetSnapshot == signature) continue;
                await s.SendGameAsync(TeamTargets, packet);
                s.WorldState.TeamTargetSnapshot = signature;
            }
        }
    }

    public async Task ToSelfAndViewersAsync(GameSession s, uint id, IMessage body)
    {
        var prepared = new PreparedMessage(id, body);
        await s.SendPreparedAsync(prepared);
        foreach (var p in world.Viewers(s)) await p.SendPreparedAsync(prepared);
    }

    /// <summary>Request-without-result errors use APIError, with a nonempty parameter list for the Lua decoder.</summary>
    public static Task ErrorAsync(GameSession s, uint origin, int code) => s.SendGameAsync(99999, new APIErrorMsg
    { ErrorCode = code, OriginMsgId = origin, ErrorParams = { new ErrorParam { ParamType = ErrorParamType.ErrorParamString, ParamValue = { "" } } } });

    internal static bool Finite(Vec3? p) => p != null && float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    internal static float Distance(Vec3 a, Vec3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
}
