using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class TerritoryWarsService
{
    private const uint GlobalObjectNotify = 30115;
    public sealed class BattleState
    {
        public int Zone { get; set; }
        public int Round { get; set; }
        public long Scene { get; set; }
        public long StatisticsId { get; set; }
        public long Start { get; set; }
        public long End { get; set; }
        public long LastTick { get; set; }
        public bool Settled { get; set; }
        public long Winner { get; set; }
        public List<long> Guilds { get; set; } = new();
        public Dictionary<long, long> Scores { get; set; } = new();
        public Dictionary<long, long> Resources { get; set; } = new();
        public Dictionary<int, CapturePoint> Points { get; set; } = new();
        /// <summary>30129 gameplay points of the stronghold scene, by spawn point id (TerritoryWarsService.ScenePoints.cs).</summary>
        public Dictionary<int, ScenePoint> ScenePoints { get; set; } = new();
        public Dictionary<long, Participant> Players { get; set; } = new();
        public byte[]? Result { get; set; }
    }
    public sealed class Participant
    {
        public long Id { get; set; }
        public long Guild { get; set; }
        public string Name { get; set; } = "";
        public int Job { get; set; }
        public int Level { get; set; }
        public long Damage { get; set; }
        public long Taken { get; set; }
        public long Healed { get; set; }
        public long Score { get; set; }
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public long DeadUntil { get; set; }
        public long Joined { get; set; }
        public bool Loaded { get; set; }
    }
    public sealed class CapturePoint
    {
        public int Id { get; set; }
        public int Npc { get; set; }
        public int EditorPoint { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public long Owner { get; set; }
        public long Challenger { get; set; }
        public long Progress { get; set; }
        public long GuardUntil { get; set; }
        public long RespawnAt { get; set; }
        public long Hp { get; set; }
        public int Captures { get; set; }
        public bool Crystal { get; set; }
        public MapPoint Position => new(X, Y, Z, 0);
    }
    public sealed class Battle(TerritoryWarsService owner, int key, InstanceService.Room room)
    {
        public int Key { get; } = key;
        public string Epoch { get; } = owner.state.Epoch;
        public InstanceService.Room Room { get; } = room;
        public BattleState State => owner._seasonCleanupState is { } retiring && retiring.Epoch == Epoch
            ? retiring.Battles.GetValueOrDefault(Key)
              ?? throw new InvalidOperationException("Retiring territory battle state is missing.")
            : owner.state.Epoch == Epoch && owner.state.Battles.TryGetValue(Key, out var current) ? current
              : throw new InvalidOperationException("Territory battle has no active or retiring epoch state.");
        internal Dictionary<int, MonsterUnit> Actors { get; } = new();
        internal HashSet<long> CapturedActors { get; } = new();
        internal Dictionary<int, ScenePointUnit> ScenePointActors { get; } = new();
        internal bool Spawning;
    }
    private sealed class QueueEntry
    {
        public required int Zone;
        public required long Scene;
        public required Vec3 Position;
        public required long Joined;
        public long EnterAt;
        public long AwaySince;
    }
    private static int BattleKey(int round, int zone) => round * 100 + zone;
    private TerritoryWarsClock.Stage? WarStage(int round) => _clock.RoundStage(round, round == 1 ? 6 : round == 2 ? 10 : 14);
    private IEnumerable<int> BattleZones(int round) => Zones.Where(z => GameData.Int(z, "_iType") == (round == 1 ? 3 : round == 2 ? 2 : 1)).Select(z => (int)Id(z));

    /// <summary>30400 is the shared dungeon ingress; all qualification and scene selection are server derived.</summary>
    public async Task<bool> EnterAsync(GameSession s, EnterDungeonReq req)
    {
        var row = Zones.FirstOrDefault(z => GameData.Int(z, "_iDungeonID") == req.SceneEntry);
        if (row.ValueKind == JsonValueKind.Undefined && req.Param?.Territory == null) return false;
        using var reservation = await EnterGateAsync("territory/battle entry", s);
        using var stagedTerritoryState = StageState();
        try
        {
            await ReconcileLockedAsync();
            int zone = row.ValueKind == JsonValueKind.Undefined ? 0 : (int)Id(row);
            int error = EntryError(s, zone);
            if (error != 0) { await Send(s, 30400, new EnterDungeonRsp { Result = error }); return true; }
            // pointId/fortId are choices inside the selected map, never a way to override its qualification.
            var param = req.Param?.Territory;
            if (param is { StateId: > 0 } && param.StateId != zone && param.StateId != Parent(zone))
            { await Send(s, 30400, new EnterDungeonRsp { Result = ErrorCodes.TerritoryInvalidPoint }); return true; }
            if (s.TerritoryBattle is { } existing && existing.State.Zone == zone)
            { await Send(s, 30400, new EnterDungeonRsp()); return true; }
            if (s.InstanceId != 0) { await Send(s, 30400, new EnterDungeonRsp { Result = ErrorCodes.Parameter }); return true; }
            if (InRoom(zone) >= Capacity(zone))
            {
                _queue[s] = new QueueEntry { Zone = zone, Scene = s.SceneEntry, Position = s.Position.Clone(), Joined = _clock.Now };
                await Send(s, 30400, new EnterDungeonRsp());
                await SendQueueAsync(s, _queue[s]);
            }
            else await AdmitAsync(s, zone, true);
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
        return true;
    }
    private int EntryError(GameSession s, int zone)
    {
        var st = _clock.Current;
        var guild = Own(s);
        if (zone == 0 || !BattleZones(st.Round).Contains(zone)) return ErrorCodes.TerritoryInvalidPoint;
        if (st.Type is not (5 or 6 or 9 or 10 or 13 or 14)) return ErrorCodes.TerritoryWarClosed;
        if (s.Role == null || s.Progress == null || s.Dead || s.SceneLoading) return ErrorCodes.Parameter;
        if (!_functions.IsOpen(s, 72)) return ErrorCodes.FeatureUnavailable;
        if (guild == null) return ErrorCodes.TerritoryGuild;
        if (!QualifiedFor(zone).Contains(guild.Id)) return ErrorCodes.TerritoryNotQualified;
        if (state.Battles.GetValueOrDefault(BattleKey(st.Round, zone))?.Settled == true) return ErrorCodes.TerritoryWarClosed;
        return 0;
    }
    private int Capacity(int zone) => Zone(zone) is { } row && _data.Row("DungeonConfig", GameData.Int(row, "_iDungeonID")) is { } dungeon
        ? (int)(GameData.Int(dungeon, "_iRoleLimit") is > 0 and var n ? n : 200) : 200;
    private int InRoom(int zone) => _battles.GetValueOrDefault(BattleKey(_clock.Current.Round, zone))?.Room.ActiveMembers.Count ?? 0;

    private BattleState MakeBattle(int round, int zone, long statisticsId)
    {
        var cfg = Zone(zone)!.Value;
        var stage = WarStage(round)!;
        long entry = GameData.Int(cfg, "_iDungeonID");
        var map = _maps.Map(entry)!;
        var born = _scene.BornPoint(entry)!.Value;
        int editorId = (int)GameData.Int(cfg, "_iDungeonPointID");
        var usable = map.Points.Where(p => p.Value.X != 0 || p.Value.Z != 0).OrderBy(p => p.Key).ToArray();
        // Configured point 33 is authoritative. Other rows use a grounded non-born editor point (inferred
        // reconstruction for the zero-valued NPC/point columns); never use a fabricated off-map coordinate.
        var selected = editorId > 0 && map.Points.TryGetValue(editorId, out var fixedPoint) ? fixedPoint
            : map.Points.TryGetValue(33, out var sameFlagPoint) ? sameFlagPoint
            : usable.FirstOrDefault(p => MonsterService.Distance(p.Value.X, p.Value.Z, born.X, born.Z) > 10).Value;
        if (selected.X == 0 && selected.Z == 0) selected = born;
        selected = map.Grounded(selected);
        if (!map.IsWalkable(selected.X, selected.Y, selected.Z)) selected = born;
        int npc = (int)GameData.Int(cfg, "_iNPCID");
        if (npc == 0) npc = 4905101; // recovered Stronghold Flag template; zero-column reconstruction (inferred).
        var record = new BattleState
        {
            Zone = zone, Round = round, Scene = entry, StatisticsId = statisticsId,
            Start = stage.StartTime, End = stage.EndTime, LastTick = _clock.Now, Guilds = QualifiedFor(zone, round).ToList(),
            ScenePoints = DefaultScenePoints(entry)
        };
        record.Points[0] = new CapturePoint
        {
            Id = 0, Npc = npc, EditorPoint = editorId > 0 ? editorId : 33,
            X = selected.X, Y = selected.Y, Z = selected.Z, Owner = OwnerOf(zone), Crystal = true
        };
        // GVGConfig.GVGHelpText: four majority-capture fields, shared resource income, and final crystal owner.
        // Their placements use ground-checked offsets around the objective when the editor only gives a born/exit
        // pair. This placement is an explicit reconstruction; the exported scene geometry remains authoritative.
        if (round >= 2)
            for (int i = 0; i < 4; i++)
            {
                var p = FieldPosition(map, selected, i);
                record.Points[i + 1] = new CapturePoint { Id = i + 1, EditorPoint = 100 + i, X = p.X, Y = p.Y, Z = p.Z };
            }
        foreach (long guild in record.Guilds) { record.Scores[guild] = 0; record.Resources[guild] = 0; }
        return record;
    }
    private static MapPoint FieldPosition(MapWorld map, MapPoint center, int index)
    {
        double angle = index * Math.PI / 2;
        for (int radius = 20; radius >= 4; radius -= 2)
        {
            var p = map.Grounded(center with { X = center.X + (float)Math.Cos(angle) * radius, Z = center.Z + (float)Math.Sin(angle) * radius });
            if (map.IsWalkable(p.X, p.Y, p.Z)) return p;
        }
        return center;
    }
    private async Task AdmitAsync(GameSession s, int zone, bool reply)
    {
        int key = BattleKey(_clock.Current.Round, zone);
        if (!state.Battles.ContainsKey(key))
        {
            long statisticsId = await AllocateStatisticsIdAsync();
            state.Battles[key] = MakeBattle(_clock.Current.Round, zone, statisticsId);
            await SaveAsync(state);
        }
        var record = state.Battles[key];
        var guild = Own(s)!;
        if (record.Players.TryGetValue(s.Role!.Id, out var prior) && prior.Guild != guild.Id)
        {
            if (reply) await Send(s, 30400, new EnterDungeonRsp { Result = ErrorCodes.TerritoryNotQualified });
            return;
        }
        long roleId = s.Role.Id;
        var attachment = s.Attachment;
        bool added = record.Players.TryAdd(roleId, new Participant
        {
            Id = roleId, Guild = guild.Id, Name = s.Role.Name,
            Job = s.Progress!.Career, Level = s.Progress.Level, Joined = _clock.Now
        });
        var priorRole = LoadRole(roleId);
        var stagedRole = Copy(priorRole);
        stagedRole.Camp = 3;
        stagedRole.LastFatigue = _clock.Now;
        var admission = _roles.NewTransaction();
        Save(state, admission);
        SaveRole(roleId, stagedRole, admission);
        await _roles.CommitAsync(admission, "territory/battle-admit");
        _committedState = Copy(state);
        async Task RollbackAdmissionAsync()
        {
            if (added) record.Players.Remove(roleId);
            var rollback = _roles.NewTransaction();
            if (added) Save(state, rollback);
            SaveRole(roleId, priorRole, rollback);
            await _roles.CommitAsync(rollback, "territory/battle-admit-rollback");
            if (added) _committedState = Copy(state);
        }
        // A refused durable admission must not leave a live instance member behind.
        Battle? battle = null;
        bool joined;
        try
        {
            if (!_battles.TryGetValue(key, out battle) || battle.Room.Closed)
            {
                battle = new Battle(this, key, _instances.Create(record.Scene, [s]));
                _battles[key] = battle;
                joined = true;
            }
            else joined = _instances.AddParticipant(battle.Room, s);
        }
        catch { await RollbackAdmissionAsync(); throw; }
        if (!joined)
        {
            await RollbackAdmissionAsync();
            if (reply) await Send(s, 30400, new EnterDungeonRsp { Result = ErrorCodes.Parameter });
            return;
        }
        async Task AbandonRoomAsync()
        {
            await _instances.AbandonAsync(battle!.Room, s);
            if (battle.Room.Closed && _battles.TryGetValue(key, out var current) && ReferenceEquals(current, battle))
                _battles.Remove(key);
        }
        // Assign after the scene switch: SceneService.Leaving runs while the old room still owns the session.
        bool entered;
        try
        {
            if (reply) await Send(s, 30400, new EnterDungeonRsp());
            entered = await _instances.EnterAsync(battle!.Room, s, 0);
        }
        catch
        {
            try { await AbandonRoomAsync(); }
            finally { await RollbackAdmissionAsync(); }
            throw;
        }
        if (!entered)
        {
            try { await AbandonRoomAsync(); }
            finally { await RollbackAdmissionAsync(); }
            return;
        }
        if (s.Role?.Id != roleId || !ReferenceEquals(s.Attachment, attachment)) return;
        s.TerritoryBattle = battle;
        s.TerritoryWars = stagedRole;
        await SpawnObjectivesAsync(battle);
        await SpawnScenePointsAsync(battle);
        await SendCountsAsync(battle);
    }
    private async Task SpawnObjectivesAsync(Battle b)
    {
        if (b.Spawning || b.Room.Closed || b.State.Settled) return;
        b.Spawning = true;
        try
        {
            foreach (var p in b.State.Points.Values.Where(p => p.Crystal && p.RespawnAt <= _clock.Now))
            {
                if (b.Actors.GetValueOrDefault(p.Id) is { Dead: false }) continue;
                // An attacker may leave while an awaited damage hook runs. The dead objective still
                // recovers, but that abandoned hit cannot award capture to the session's next role.
                if (b.Actors.GetValueOrDefault(p.Id) is { Dead: true } && p.RespawnAt == 0)
                { p.Hp = 0; p.RespawnAt = _clock.Now + Gvg("GVGEmperiumReviveTime", 30); continue; }
                var actor = await _instances.SpawnAsync(b.Room, p.Npc, p.Position);
                if (actor == null) throw new InvalidOperationException($"Configured territory objective {p.Npc} could not spawn");
                if (p.Hp > 0) actor.Hp = Math.Min(actor.Hp, p.Hp);
                p.Hp = actor.Hp;
                b.Actors[p.Id] = actor;
                p.RespawnAt = 0;
            }
        }
        finally { b.Spawning = false; }
    }
    private bool Active(Battle b) => !b.State.Settled && _clock.Now >= b.State.Start && _clock.Now < b.State.End;
    private bool Member(GameSession s, Battle b) => s.Role != null && s.InstanceId == b.Room.Id && !s.SceneLoading
        && b.Room.ActiveMembers.Any(m => ReferenceEquals(m.Session, s))
        && b.State.Players.TryGetValue(s.Role.Id, out var p) && OwnId(s) == p.Guild && b.State.Guilds.Contains(p.Guild);
    public bool Hostile(GameSession a, GameSession b) => SiegeHostile(a, b) || a.TerritoryBattle is { } battle && ReferenceEquals(b.TerritoryBattle, battle)
        && Active(battle) && Member(a, battle) && Member(b, battle) && OwnId(a) != OwnId(b);
    public bool CanHitObjective(GameSession s, MonsterUnit m)
    {
        if (s.SiegeBattle is { } siege && m.InstanceId == siege.Room.Id)
            return SiegeCanHit(s, m, siege);
        if (s.TerritoryBattle is not { } b || m.InstanceId != b.Room.Id) return true;
        if (!Active(b) || !Member(s, b)) return false;
        var point = b.Actors.FirstOrDefault(x => x.Value == m);
        return point.Value == null || b.State.Points[point.Key].Owner != Own(s)!.Id;
    }
    private async Task LoadedAsync(GameSession s)
    {
        if (s.TerritoryBattle is not { } b || s.InstanceId != b.Room.Id) return;
        if (s.Role == null || !b.State.Players.TryGetValue(s.Role.Id, out var participant)
            || Own(s)?.Id != participant.Guild) { await _instances.LeaveAsync(s); return; }
        participant.Loaded = true;
        await SaveAsync(state);
        await Send(s, 30401, BaseInfo(b));
        await SendObjectsAsync(s, b);
        await SendScenePointsAsync(s, b);
        await SyncSiegeAsync(s);
        await SendMarkersAsync(s);
        await UpdateFieldBuffsAsync(b);
    }
    private DungeonBaseInfoNotify BaseInfo(Battle b)
    {
        var r = b.State;
        var map = MapData(ZoneType(r.Zone) == 1 ? new TerritoryWarsGetMapDataReq { ZoneIds = { r.Zone } }
            : new TerritoryWarsGetMapDataReq { StateIds = { ZoneType(r.Zone) == 3 ? Parent(r.Zone) : r.Zone } });
        var init = new TerritoryWarsInitData { MapData = map, EndTime = r.End * 1000 };
        foreach (long g in r.Guilds)
        {
            init.GuildIds[g] = g;
            if (_guildStore.Get(g) is { } guild) init.GuildInfos[g] = _guilds.Snapshot(guild);
        }
        init.PointWarZoneIdGuild[r.Zone] = r.Points[0].Owner;
        int stage = r.Settled ? 3 : _clock.Now < r.Start ? 1 : 2;
        return new DungeonBaseInfoNotify
        {
            DungeonEntry = r.Scene, DungeonId = b.Room.Id, Stage = stage, Level = 99,
            InitData = new DungeonInitData { TerritoryWarInitData = init, PlaybookId = _clock.PlayBook },
            StageModuleData = new DungeonStageModuleData
            {
                MapState = stage, Stage = stage, StageStartTime = r.Start * 1000,
                StageEndTime = r.End * 1000, MapStateStartTime = r.Start * 1000, MapStateEndTime = r.End * 1000
            },
            ChangeType = (DungeonBaseInfoNotify.Types.eChangeType)2
        };
    }
    private async Task LeavingAsync(GameSession s)
    {
        var b = s.TerritoryBattle;
        var role = s.Role;
        var attachment = s.Attachment;
        var owner = s.Owner;
        var epoch = owner?.LifecycleEpoch;
        s.TerritoryBattle = null;
        await CancelQueueAsync(s, TerritoryWarQueueExitReason.LeaveScene);
        if (b == null) return;
        s.TerritoryMinimapOpen = false;
        if (role != null)
        {
            using (var scope = _loop.Owners is { } owners
                ? await owners.EnterAsync("territory/battle leave fatigue", Ownership.ReservationKey.Role(role.Id)) : null)
            {
                if (!ReferenceEquals(s.Role, role) || !ReferenceEquals(s.Attachment, attachment)
                    || !ReferenceEquals(s.Owner, owner) || owner?.LifecycleEpoch != epoch) return;
                if (owner != null) scope?.Context.Pin(owner);
                var r = LoadRole(role.Id);
                r.LastFatigue = _clock.Now;
                await SaveRoleAsync(role.Id, r);
                s.TerritoryWars = r;
            }
        }
        await RemoveBuffAsync(s, (int)Gvg("GVGFieldBuff", 10301));
        await SendCountsAsync(b);
    }
    public async Task DisconnectAsync(GameSession s)
    {
        await CancelQueueAsync(s, TerritoryWarQueueExitReason.Disconnect);
        await LeavingAsync(s);
        await SiegeLeavingAsync(s);
    }
    public async Task<bool> LeaveAsync(GameSession s)
    {
        if (s.TerritoryBattle == null && s.SiegeBattle == null) return false;
        await Send(s, 30402, new LeaveDungeonRsp());
        await _instances.LeaveAsync(s);
        return true;
    }
    private async Task SendCountsAsync(Battle b)
    {
        foreach (long guild in b.State.Guilds)
        {
            var msg = new TerritoryWarsGuildScenePlayerCountNtf
            {
                GuildId = guild,
                PlayerCount = b.Room.ActiveMembers.Count(m => m.Session.TerritoryBattle == b && Own(m.Session)?.Id == guild)
            };
            await RoleFanout.EachAsync(b.Room.ActiveMembers.Select(m => m.Session), s => Send(s, 83094, msg));
        }
    }
    private Task SendMarkersAsync(GameSession s)
    {
        var msg = new TerritoryWarsMapNotify();
        if (s.TerritoryBattle is { } b)
            foreach (var p in b.State.Points.Values)
                msg.MapInfo.Add(new TerritoryWarsMapInfo
                {
                    SceneConfigId = b.State.Scene, PointId = p.EditorPoint,
                    Data = new InteractInfo { StateId = p.Owner, ActiveObjId = b.Actors.GetValueOrDefault(p.Id)?.Uid ?? 0 }
                });
        return Send(s, MarkerNotify, msg);
    }
    private Task SendObjectsAsync(GameSession s, Battle b)
    {
        var msg = new GlobalObjectDataNotify();
        foreach (var p in b.State.Points.Values)
        {
            var actor = b.Actors.GetValueOrDefault(p.Id);
            long id = actor?.Uid ?? b.State.StatisticsId * 16 + p.Id;
            msg.GlobalDatas[id] = new GlobalObjectData
            {
                GlobalObjId = id, ConfigId = p.Npc,
                Pos = new Vec3 { X = p.X, Y = p.Y, Z = p.Z }, BelongGuild = p.Owner,
                Hp = actor?.Hp ?? p.Progress, MaxHp = actor?.Template.MaxHp ?? Gvg("GVGFieldOccupyAmount", 30),
                DungeonPointId = p.EditorPoint, PointData = new GlobalPointData
                {
                    GvgWarzoneId = b.State.Zone,
                    LockTime = p.GuardUntil * 1000, IsLock = _clock.Now < p.GuardUntil, GetCount = p.Captures
                }
            };
        }
        return Send(s, GlobalObjectNotify, msg);
    }

    private async Task DamageRecordedAsync(GameSession s, MonsterUnit m, long amount)
    {
        if (s.TerritoryBattle is not { } b || !Active(b) || m.InstanceId != b.Room.Id || s.Role == null) return;
        using var reservation = await EnterGateAsync("territory/battle damage", s);
        using var stagedTerritoryState = StageState();
        try
        {
            if (!ReferenceEquals(s.TerritoryBattle, b) || !Active(b) || m.InstanceId != b.Room.Id || s.Role == null) return;
            if (b.State.Players.TryGetValue(s.Role.Id, out var p)) p.Damage += Math.Max(0, amount);
            foreach (var (id, actor) in b.Actors) if (actor == m) b.State.Points[id].Hp = actor.Hp;
            await SaveAsync(state);
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
    }
    private async Task BattleHealRecordedAsync(GameSession s, long amount)
    {
        if (amount <= 0 || s.TerritoryBattle is not { } battle || s.Role == null) return;
        using var reservation = await EnterGateAsync("territory/battle heal", s);
        using var stagedTerritoryState = StageState();
        try
        {
            if (ReferenceEquals(s.TerritoryBattle, battle) && s.Role != null
                && battle.State.Players.TryGetValue(s.Role.Id, out var player))
            {
                player.Healed += amount;
                await SaveAsync(state);
            }
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
    }
    private async Task PvpRecordedAsync(GameSession s, GameSession victim, long amount, bool lethal)
    {
        if (s.TerritoryBattle is not { } b || !Hostile(s, victim)) return;
        using var reservation = await EnterGateAsync("territory/battle pvp", s);
        using var stagedTerritoryState = StageState();
        try
        {
            if (!ReferenceEquals(s.TerritoryBattle, b) || !Hostile(s, victim)) return;
            RoleState? nextRole = null;
            var attacker = b.State.Players[s.Role!.Id]; var defender = b.State.Players[victim.Role!.Id];
            attacker.Damage += Math.Max(0, amount); defender.Taken += Math.Max(0, amount);
            if (lethal)
            {
                attacker.Kills++; attacker.Score += 1; b.State.Scores[attacker.Guild] += 1;
                var role = LoadRole(s.Role.Id); role.FatigueKills++;
                if (_data.Table("PublicParamConfig").TryGetValue("TerritoryFatigueBuffPileAddFromKill", out var tiers))
                    foreach (var tier in tiers.EnumerateArray())
                        if (role.FatigueKills >= GameData.Number(tier[0]) && role.FatigueKills <= GameData.Number(tier[1]))
                            role.Fatigue = (int)Math.Min(20, role.Fatigue + (GameData.Number(tier[2]) ?? 0));
                nextRole = role;
            }
            var transaction = _roles.NewTransaction();
            Save(state, transaction);
            if (nextRole != null) SaveRole(s.Role.Id, nextRole, transaction);
            await _roles.CommitAsync(transaction, "territory/battle-pvp");
            PublishCommitted(state);
            if (nextRole != null) s.TerritoryWars = nextRole;
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
    }
    private async Task KilledAsync(GameSession s, MonsterUnit m)
    {
        if (s.TerritoryBattle is not { } b || !Active(b) || !Member(s, b) || m.InstanceId != b.Room.Id) return;
        var role = s.Role;
        var attachment = s.Attachment;
        using var reservation = await EnterGateAsync("territory/battle actor kill", s);
        using var stagedTerritoryState = StageState();
        try
        {
            if (!ReferenceEquals(s.Role, role) || !ReferenceEquals(s.Attachment, attachment)
                || !Active(b) || !Member(s, b)) return;
            var pair = b.Actors.FirstOrDefault(x => x.Value == m);
            if (pair.Value == null || !b.State.Points.TryGetValue(pair.Key, out var p)) return;
            if (!b.CapturedActors.Add(m.Uid)) return;
            long guild = Own(s)!.Id;
            await CaptureAsync(b, p, guild, async () =>
            {
                p.Hp = 0; p.RespawnAt = _clock.Now + Gvg("GVGEmperiumReviveTime", 30);
                b.State.Players[role!.Id].Score += 50; // WarzoneTaskConfig type 1, 50; last-hit attribution inferred.
                await SaveAsync(state);
            });
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
    }
    private async Task CaptureAsync(Battle b, CapturePoint p, long guild,
        Func<Task>? afterCapture = null)
    {
        bool changed = p.Owner != guild;
        if (changed)
        {
            p.Owner = guild; p.Challenger = 0; p.Progress = 0; p.Captures++;
            p.GuardUntil = _clock.Now + Gvg("GVGFieldGuardTime", 15);
            b.State.Scores[guild] = b.State.Scores.GetValueOrDefault(guild) + 50;
            await SaveAsync(state);
        }
        if (afterCapture != null) await afterCapture();
        if (!changed) return;
        await RoleFanout.EachAsync(b.Room.ActiveMembers.Select(m => m.Session), async s =>
        { await Send(s, 30401, BaseInfo(b)); await SendObjectsAsync(s, b); if (s.TerritoryMinimapOpen) await SendMarkersAsync(s); });
        await UpdateFieldBuffsAsync(b);
    }
    private async Task DiedAsync(GameSession s)
    {
        var role = s.Role;
        var attachment = s.Attachment;
        await CancelQueueAsync(s, TerritoryWarQueueExitReason.Dead);
        if (s.TerritoryBattle is not { } b || role == null) return;
        using (var reservation = await EnterGateAsync("territory/battle death", s))
        {
            using var stagedTerritoryState = StageState();
            try
            {
                if (!ReferenceEquals(s.Role, role) || !ReferenceEquals(s.Attachment, attachment)
                    || !b.State.Players.TryGetValue(role.Id, out var p)) return;
                p.Deaths++; p.DeadUntil = _clock.Now + Gvg("GVGReviveCountDown", 15);
                await SaveAsync(state);
            }
            finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
        }
        var current = b.State.Players[role.Id];
        await Send(s, 30112, new SyncClientPlayerReviveInfoNtf { BDead = true, ReviveTime = current.DeadUntil, SceneEntry = s.SceneEntry });
    }
    public async Task<bool> ReviveAsync(GameSession s, EntityReliveReq req, CombatService combat)
    {
        if (s.TerritoryBattle is not { } b) return false;
        if (!s.Dead || b.State.Players[s.Role!.Id].DeadUntil > _clock.Now)
            await Send(s, 31013, new EntityReliveAck { Result = ErrorCodes.Parameter, EntityID = (ulong)s.Role!.Id });
        else await combat.ReviveAsync(s, req);
        return true;
    }

    private async Task BattleClockAsync()
    {
        var stage = _clock.Current;
        if (stage.Type is 5 or 6 or 9 or 10 or 13 or 14)
            foreach (int zone in BattleZones(stage.Round))
                if (!state.Battles.ContainsKey(BattleKey(stage.Round, zone)) && QualifiedFor(zone).Length > 0)
                    state.Battles[BattleKey(stage.Round, zone)] = MakeBattle(stage.Round, zone,
                        await AllocateStatisticsIdAsync());
        foreach (var (key, r) in state.Battles.ToArray())
        {
            _battles.TryGetValue(key, out var b);
            if (r.Settled)
            {
                if (b != null && _clock.Now >= r.End + Gvg("GVGEndAutoLeaveTime", 5))
                    await RoleFanout.EachAsync(b.Room.ActiveMembers.Select(m => m.Session), async s =>
                    { if (s.TerritoryBattle == b && !s.SceneLoading) await _instances.LeaveAsync(s); });
                continue;
            }
            if (_clock.Now >= r.End) { await SettleAsync(key); continue; }
            long elapsed = Math.Clamp(_clock.Now - Math.Max(r.LastTick, r.Start), 0, 5);
            r.LastTick = _clock.Now;
            if (b == null || b.Room.Closed || _clock.Now < r.Start) continue;
            await RoleFanout.EachAsync(b.Room.ActiveMembers.Select(m => m.Session), async s =>
            { if (!s.SceneLoading && s.TerritoryBattle == b && !Member(s, b)) await _instances.LeaveAsync(s); });
            await SpawnObjectivesAsync(b);
            await SpawnScenePointsAsync(b);
            foreach (var p in r.Points.Values.Where(p => !p.Crystal && _clock.Now >= p.GuardUntil))
            {
                var groups = b.Room.ActiveMembers.Select(m => m.Session).Where(s => !s.Dead && Member(s, b)
                    && MonsterService.Distance(s.Position.X, s.Position.Z, p.X, p.Z) <= 5)
                    .GroupBy(s => Own(s)!.Id).Select(g => (Guild: g.Key, Count: g.Count())).OrderByDescending(g => g.Count).ToArray();
                if (groups.Length == 0 || groups.Length > 1 && groups[0].Count == groups[1].Count) continue;
                var top = groups[0];
                if (top.Guild == p.Owner) { p.Progress = 0; p.Challenger = 0; continue; }
                if (p.Challenger != top.Guild) { p.Challenger = top.Guild; p.Progress = 0; }
                p.Progress += elapsed * Math.Min(Gvg("GVGFieldOccupyMaxSpeed", 3), top.Count * Gvg("GVGFieldOccupySpeed", 1));
                if (p.Progress >= Gvg("GVGFieldOccupyAmount", 30)) await CaptureAsync(b, p, top.Guild);
            }
            foreach (long guild in r.Guilds)
            {
                int count = r.Points.Values.Count(p => p.Owner == guild);
                if (count > 0) r.Scores[guild] += elapsed * count;
                long growth = (count * Gvg("GVGFieldResourceGrowNum", 14) + Gvg("GVGGuildResourceAutoGrowNum", 4))
                    * elapsed / Math.Max(1, Gvg("GVGFieldResourceGrowInterval", 1));
                r.Resources[guild] = Math.Min(Gvg("GVGResourceLimit", 48000), r.Resources[guild] + growth);
            }
            await RoleFanout.EachAsync(b.Room.ActiveMembers.Select(m => m.Session), async s =>
            { await SendObjectsAsync(s, b); await FatigueAsync(s, b); });
        }
        if (state.Battles.Count > 0) await SaveAsync(state);
    }
    private async Task UpdateFieldBuffsAsync(Battle b)
    {
        int buff = (int)Gvg("GVGFieldBuff", 10301);
        await RoleFanout.EachAsync(b.Room.ActiveMembers.Select(m => m.Session), async s =>
        {
            int count = b.State.Points.Values.Count(p => !p.Crystal && p.Owner == Own(s)?.Id);
            await RemoveBuffAsync(s, buff);
            if (count > 0) await _buffs.SetPileCountAsync(s, buff, (int)Math.Min(int.MaxValue, (b.State.End - _clock.Now) * 1000), s.Role!.Id, count);
        });
    }
    private async Task RemoveBuffAsync(GameSession s, int id)
    {
        foreach (var buff in s.Buffs.Where(b => b.ConfigId == id).ToArray()) await _buffs.RemoveAsync(s, buff);
    }
    private Task SyncSiegeAsync(GameSession s, RoleState? committedRole = null)
    {
        var r = s.TerritoryWars = committedRole ?? LoadRole(s.Role!.Id);
        var info = new DBRoleSiegeData
        {
            HonorPoints = r.Honor, ValorPoints = r.Valor, HistoryHonor = new(),
            IsRoyalQualification = Own(s) is { } g && state.Owners.Any(x => x.Value == g.Id && ZoneType(x.Key) == 1),
            IsSeniorQualification = Own(s) is { } g2 && state.Owners.Any(x => x.Value == g2.Id && ZoneType(x.Key) <= 2),
            FatigueInfo = new TerritoryFatigueInfo
            {
                FatigueBuffPile = r.Fatigue,
                FatigueKillPlayerCount = r.FatigueKills,
                FatigueEnterTime = s.TerritoryBattle != null ? r.LastFatigue * 1000 : 0,
                FatigueLeaveTime = s.TerritoryBattle == null ? r.LastFatigue * 1000 : 0,
                LastAddBuffPileTime = r.LastFatigue * 1000, LastDelBuffPileTime = r.LastFatigue * 1000
            }
        };
        if (Own(s) is { } guild) info.GuildValorPoints[guild.Id] = r.Valor;
        return CharacterService.SyncAsync(s, 5020, info);
    }
    private async Task FatigueAsync(GameSession s, Battle b)
    {
        var r = LoadRole(s.Role!.Id);
        if (r.Fatigue >= 20)
        { await FatigueReturnAsync(s); return; }
        long period = Math.Max(1, Param("TerritoryFatigueBuffPileAddTime", 60000000) / 1000);
        int buffId = (int)Param("TerritoryFatigueBuffId", 4920001);
        bool changed = _clock.Now >= r.LastFatigue + period;
        if (changed)
        {
            r.Fatigue = (int)Math.Min(20, r.Fatigue + (_clock.Now - r.LastFatigue) / period);
            r.LastFatigue = _clock.Now; await SaveRoleAsync(s.Role!.Id, r); s.TerritoryWars = r;
        }
        if (changed || s.Buffs.FirstOrDefault(b => b.ConfigId == buffId)?.PileCount != (r.Fatigue == 0 ? null : r.Fatigue))
        {
            await SyncSiegeAsync(s);
            await RemoveBuffAsync(s, buffId);
            if (r.Fatigue > 0) await _buffs.SetPileCountAsync(s, buffId, int.MaxValue, s.Role.Id, r.Fatigue);
        }
        if (r.Fatigue >= 20) await FatigueReturnAsync(s);
    }
    private async Task FatigueReturnAsync(GameSession s)
    {
        await Send(s, FatigueNotify, new TerritoryWarsFatigueTeleportNtf());
        await _scene.EnterSceneAsync(s, Param("TerritoryFatigueBuffReturnMapId", 10100));
    }
    private async Task ClearFatigueAsync()
    {
        await RoleFanout.EachAsync(_sessions.All().Where(s => s.Role != null && !RoleRequiresRecovery(s.Role.Id)
            && s.TerritoryWars != null && s.TerritoryBattle == null), async s =>
        {
            // Every fatigue write also replaces s.TerritoryWars, so a zero copy needs no per-second DB read.
            if (s.TerritoryWars!.Fatigue == 0) return;
            var r = LoadRole(s.Role!.Id);
            if (r.Fatigue == 0 || _clock.Now < r.LastFatigue + Math.Max(1, Param("TerritoryFatigueBuffPileDelTime", 1000) / 1000)) return;
            r.Fatigue = Math.Max(0, r.Fatigue - (int)Param("TerritoryFatigueBuffPileDelStacks", 20));
            r.LastFatigue = _clock.Now; await SaveRoleAsync(s.Role.Id, r); s.TerritoryWars = r;
            await RemoveBuffAsync(s, (int)Param("TerritoryFatigueBuffId", 4920001)); await SyncSiegeAsync(s);
        });
    }

    private Task SendQueueAsync(GameSession s, QueueEntry e) => Send(s, QueueNotify, new TerritoryWarQueueNotify
    {
        Channel = 1, AheadCount = _queue.Count(p => p.Value.Zone == e.Zone && p.Value.Joined < e.Joined),
        SceneEntryId = Zone(e.Zone) is { } z ? GameData.Int(z, "_iDungeonID") : 0
    });
    private async Task CancelQueueAsync(GameSession s, TerritoryWarQueueExitReason reason)
    {
        if (!_queue.Remove(s, out var e)) return;
        await Send(s, QueueNotify, new TerritoryWarQueueNotify
        {
            Channel = 1, ExitReason = reason,
            ExitPlayerId = s.Role?.Id ?? 0, SceneEntryId = Zone(e.Zone) is { } z ? GameData.Int(z, "_iDungeonID") : 0
        });
    }
    private async Task QueueClockAsync()
    {
        foreach (var (s, e) in _queue.OrderBy(x => x.Value.Joined).ToArray())
        {
            if (s.Role == null || s.Dead || s.SceneEntry != e.Scene || EntryError(s, e.Zone) != 0)
            { await CancelQueueAsync(s, s.Dead ? TerritoryWarQueueExitReason.Dead : TerritoryWarQueueExitReason.SceneDestroyed); continue; }
            bool away = MonsterService.Distance(s.Position.X, s.Position.Z, e.Position.X, e.Position.Z) > 5;
            if (away)
            {
                if (e.AwaySince == 0) { e.AwaySince = _clock.Now; await Send(s, QueueArea, new TerritoryWarQueueAreaStatusNotify { Status = 1, ExpireTime = (_clock.Now + 5) * 1000 }); }
                if (_clock.Now >= e.AwaySince + 5) await CancelQueueAsync(s, TerritoryWarQueueExitReason.LeaveArea);
                continue;
            }
            if (e.AwaySince != 0) { e.AwaySince = 0; await Send(s, QueueArea, new TerritoryWarQueueAreaStatusNotify()); }
            if (InRoom(e.Zone) >= Capacity(e.Zone)) { e.EnterAt = 0; await SendQueueAsync(s, e); continue; }
            if (e.EnterAt == 0) { e.EnterAt = _clock.Now + 3; await Send(s, QueueEnter, new TerritoryWarQueueEnterNotify { EnterTime = e.EnterAt * 1000 }); }
            else if (_clock.Now >= e.EnterAt)
            { await CancelQueueAsync(s, TerritoryWarQueueExitReason.EnterMatch); await AdmitAsync(s, e.Zone, false); }
        }
    }
}
