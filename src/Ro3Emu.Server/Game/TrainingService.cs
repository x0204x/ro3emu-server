using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Activity119: 5069, 84061–84067 and LC_DAL_Piling's start/reset/acknowledgement events on30106.
/// DungeonConfig2100009/10/11, authored spawn points16/25, NPC3500000/1, CTrainFinishTime3500000 and
/// TrainingChallengeTimeLimit60 provide the encounter. DPS = authoritative total / full challenge duration
/// (inferred), never client damage reports. A normal dummy uses the requested race/element/size; challenge
/// stats stay fixed, so changing practice settings cannot qualify rewards.
/// </summary>
public sealed class TrainingService : IGameTicker
{
    public const uint Sync = 5069, ChallengeMode = 84061, AimAward = 84062, ExitChallengeMode = 84063, UpdateWoodNpc = 84064,
        Reconnect = 84065, ChallengeReconnect = 84066, ChallengeReset = 84067;
    private readonly GameData _data;
    private readonly RoleStore _roles;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly ActivityClock _clock;
    private readonly InstanceService _instances;
    private readonly MonsterService _monsters;
    private readonly AwardService _awards;
    private readonly GameLoop _loop;
    private readonly Dictionary<long, MapPoint> _points = new();
    private readonly Dictionary<GameSession, Attempt> _attempts = new();
    private sealed class Attempt
    {
        public required InstanceService.Room Room;
        public MonsterUnit? Dummy;
        public int Race = 2, Element = 1, Size = 2;
        public bool Challenge;
        public bool Running;
        public long End;
        public long Damage;
        public bool Settling;
    }
    private long Now => _clock.UtcNow.ToUnixTimeMilliseconds();
    private int Duration => _data.Table("PublicParamConfig").TryGetValue("TrainingChallengeTimeLimit", out var v)
        ? checked((int)(GameData.Number(v) ?? 60)) : 60;

    public TrainingService(GameData data, RoleStore roles, CharacterService character, FunctionService functions,
        ActivityClock clock, InstanceService instances, SceneService scene, MonsterService monsters,
        CombatService combat, AwardService awards, GameLoop loop, IOptions<ServerOptions> options)
    {
        this._data = data; this._roles = roles; this._character = character; this._functions = functions;
        this._clock = clock; this._instances = instances; this._monsters = monsters; this._awards = awards; this._loop = loop;
        foreach (long map in new long[] { 2100009, 2100010, 2100011 })
            if (data.Row("DungeonConfig", map) is { } config && config.TryGetProperty("_kResSceneEditorName", out var editor))
            {
                if (DataFiles.Find(Path.Combine(options.Value.GameDataDir ?? options.Value.DataDir, "sceneeditor"), editor.GetString() + ".json") is not { } path)
                    continue;
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                if (!doc.RootElement.TryGetProperty("m_kMonsterSpawnPoints", out var spawns)) continue;
                foreach (var point in spawns.EnumerateObject())
                {
                    var position = point.Value.GetProperty("position");
                    _points[map] = new(position.GetProperty("x").GetSingle(), position.GetProperty("y").GetSingle(),
                        position.GetProperty("z").GetSingle(), point.Value.GetProperty("direction").GetSingle());
                    break;
                }
            }
        scene.Loaded.Add(LoadedAsync);
        scene.Leaving.Add(s => { _attempts.Remove(s); return Task.CompletedTask; });
        combat.EventDamageRecorded.Add((s, m, amount) =>
        {
            if (!_attempts.TryGetValue(s, out var a) || a.Dummy != m || s.InstanceId != a.Room.Id || s.SceneLoading)
                return Task.CompletedTask;
            if (a.Running && Now < a.End && amount > 0) a.Damage = checked(a.Damage + amount);
            // NPCConfig's auto-recovery buffs describe an enduring target. Keep it alive after measured hits (inferred).
            m.Hp = m.Template.MaxHp; m.Target = null;
            return Task.CompletedTask;
        });
    }

    public async Task LoginAsync(GameSession s)
    {
        s.Training = _roles.LoadTraining(s.Role!.Id) is { } saved ? DBTrainingGroundInfo.Parser.ParseFrom(saved) : new();
        await CharacterService.SyncAsync(s, Sync, s.Training.Clone());
    }

    public Task LeaveRoleAsync(GameSession s)
    {
        _attempts.Remove(s);
        return Task.CompletedTask;
    }

    private int Gate(GameSession s)
    {
        if (!s.ActivitiesReady || s.Role == null || s.Training == null || _data.Row("NewActivityConfig", 119) is not { } row) return ErrorCodes.ActivityClosed;
        if (!_functions.ConditionMet(s, GameData.Int(row, "_kOpenCondition"))) return ErrorCodes.FunctionLocked;
        return _clock.ResolveActivity(row, Now / 1000, s.Role.CreateTime, 101).Contains(Now / 1000) ? 0 : ErrorCodes.ActivityPeriod;
    }

    /// <summary>Only training's branches of the shared dungeon routes use economic/runtime admission.</summary>
    internal bool IsOwnerRequest(GameSession session, uint message, ReadOnlyMemory<byte> body)
    {
        if (session.Role == null) return false;
        if (message == DungeonService.Enter)
            return EnterDungeonReq.Parser.ParseFrom(body.Span).SceneEntry is 2100009 or 2100010 or 2100011;
        if (message == DungeonService.ClientEvent)
            return ClientDungeonEvent.Parser.ParseFrom(body.Span).StrCmd is
                "OnPileChallengeStart" or "OnPileChallengeReset" or "OnPileChallengeEnterOrExit";
        return false;
    }

    public async Task<bool> TryEnterAsync(GameSession s, EnterDungeonReq req)
    {
        if (req.SceneEntry is not (2100009 or 2100010 or 2100011)) return false;
        using var scope = s.Role != null && _loop.Owners is { } owners
            ? await owners.EnterAsync("training/enter", ReservationKey.Role(s.Role.Id)) : null;
        int result = Gate(s);
        if (result == 0 && (s.SceneLoading || s.Dead)) result = ErrorCodes.SwitchingScenes;
        if (result == 0 && s.InstanceId != 0 && !_attempts.ContainsKey(s)) result = ErrorCodes.DungeonBusy;
        if (result == 0 && !_points.ContainsKey(req.SceneEntry)) result = ErrorCodes.SceneMissing;
        await s.SendGameAsync(DungeonService.Enter, new EnterDungeonRsp { Result = result });
        if (result == 0) await EnterAsync(s, req.SceneEntry);
        return true;
    }

    private async Task EnterAsync(GameSession s, long map)
    {
        var room = _instances.Create(map, [s]);
        var old = _attempts.GetValueOrDefault(s);
        if (!await _instances.EnterAsync(room, s, 3)) return;
        _attempts[s] = new Attempt
        {
            Room = room, Challenge = map == 2100011,
            Race = old?.Race ?? 2, Element = old?.Element ?? 1, Size = old?.Size ?? 2
        };
    }

    private async Task LoadedAsync(GameSession s)
    {
        if (!_attempts.TryGetValue(s, out var a) || s.InstanceId != a.Room.Id) return;
        await SpawnAsync(s, a);
        await SnapshotAsync(s, a);
    }

    private async Task SpawnAsync(GameSession s, Attempt a)
    {
        await _instances.ClearMonstersAsync(a.Room);
        int npc = a.Challenge ? 3500001 : 3500000;
        var template = _monsters.Template(npc);
        if (template == null) return;
        // These NPCs have speed 0 and training AI3500000/1; they remain stationary and never retaliate (inferred).
        template = template with { SpeedCm = 0, AttackRange = 0, Aggressive = false, RoamDistance = 0 };
        if (!a.Challenge) template = template with { Race = a.Race, Element = a.Element, Size = a.Size };
        a.Dummy = await _instances.SpawnAsync(a.Room, npc, _points[a.Room.SceneEntry], customTemplate: template);
    }

    private Task SnapshotAsync(GameSession s, Attempt a)
    {
        var snapshot = new DungeonBaseInfoNotify
        {
            DungeonEntry = a.Room.SceneEntry, DungeonId = a.Room.Id,
            Level = 1, Stage = a.Running ? 105 : 104, ChangeType = (DungeonBaseInfoNotify.Types.eChangeType)2
        };
        snapshot.Vars.Add(new DungeonVar { VarId = 3500000, Vi64 = { a.Running ? a.End : 0 } });
        return s.SendGameAsync(DungeonService.BaseInfo, snapshot);
    }

    public async Task<bool> TryEventAsync(GameSession s, ClientDungeonEvent req)
    {
        if (req.StrCmd is not ("OnPileChallengeStart" or "OnPileChallengeReset" or "OnPileChallengeEnterOrExit")) return false;
        using var held = await CharacterService.Lock(s);
        if (Gate(s) == 0 && _attempts.TryGetValue(s, out var a) && a.Challenge && !s.SceneLoading && !s.Dead)
        {
            if (req.StrCmd == "OnPileChallengeStart" && !a.Running)
            {
                a.Damage = 0; a.End = Now + Duration * 1000L; a.Running = true;
                await SnapshotAsync(s, a);
            }
            else if (req.StrCmd == "OnPileChallengeReset") await ResetAttemptAsync(s, a);
            // LC_DunGeon_Pile sends EnterOrExit on timer/result changes. It is an acknowledgement,
            // not a map transfer or a second start; accepting it must never restart the timer (inferred).
        }
        await s.SendGameAsync(DungeonService.ClientEvent, new NullArg());
        return true;
    }

    public async Task ModeAsync(GameSession s, bool challenge)
    {
        using var scope = s.Role != null && _loop.Owners is { } owners
            ? await owners.EnterAsync("training/mode", ReservationKey.Role(s.Role.Id)) : null;
        int result = Gate(s);
        if (result == 0 && (!_attempts.ContainsKey(s) || s.SceneLoading || s.Dead)) result = ErrorCodes.SceneMissing;
        if (challenge) await s.SendGameAsync(ChallengeMode, new TrainingChallengeModeResp { Result = result });
        else await s.SendGameAsync(ExitChallengeMode, new TrainingExitChallengeModeResp { Result = result });
        if (result == 0) await EnterAsync(s, challenge ? 2100011 : 2100009);
    }

    public async Task UpdateAsync(GameSession s, TrainingUpdateWoodNpcAttrReq req)
    {
        using var scope = s.Role != null && _loop.Owners is { } owners
            ? await owners.EnterAsync("training/update", ReservationKey.Role(s.Role.Id)) : null;
        int result = Gate(s);
        if (result == 0 && (!_attempts.TryGetValue(s, out var a) || a.Challenge || s.SceneLoading)) result = ErrorCodes.SceneMissing;
        if (result == 0 && (req.Race is < 1 or > 12 || req.Element is < 1 or > 10 || req.BodyType is < 1 or > 3)) result = ErrorCodes.Parameter;
        if (result == 0)
        {
            var attempt = _attempts[s]; attempt.Race = req.Race; attempt.Element = req.Element; attempt.Size = req.BodyType;
            await SpawnAsync(s, attempt);
        }
        await s.SendGameAsync(UpdateWoodNpc, new TrainingUpdateWoodNpcAttrResp { Result = result });
    }

    public async Task ReconnectAsync(GameSession s, bool challenge)
    {
        var a = _attempts.GetValueOrDefault(s);
        if (challenge) await s.SendGameAsync(ChallengeReconnect, new TrainingChallengeReconnectRes
        { EndTime = a is { Running: true } ? a.End : 0, TotalDamage = a?.Damage ?? 0 });
        else await s.SendGameAsync(Reconnect, new TrainingReconnectRes
        { Race = a?.Race ?? 2, Ele = a?.Element ?? 1, Body = a?.Size ?? 2, IsChallenge = a is { Challenge: true } ? 1 : 0 });
    }

    public async Task ResetAsync(GameSession s)
    {
        using var held = await CharacterService.Lock(s);
        int result = Gate(s);
        if (result == 0 && (!_attempts.TryGetValue(s, out var a) || !a.Challenge || s.SceneLoading)) result = ErrorCodes.SceneMissing;
        if (result == 0)
            await ResetAttemptAsync(s, _attempts[s]);
        await s.SendGameAsync(ChallengeReset, new TrainingChallengeResetRes { Result = result });
    }

    private async Task ResetAttemptAsync(GameSession s, Attempt a)
    {
        a.Running = false; a.Damage = 0; a.End = 0; a.Settling = false;
        await SpawnAsync(s, a); await SnapshotAsync(s, a);
    }

    public async Task ClaimAsync(GameSession s, TrainingAimAwardReq req)
    {
        using var held = await CharacterService.Lock(s,
            [ReservationKey.Entity("training-claims", s.Role!.Id.ToString())]);
        int result = Gate(s);
        if (result == 0 && (req.AimAwardIds.Count == 0 || req.AimAwardIds.Distinct().Count() != req.AimAwardIds.Count)) result = ErrorCodes.Parameter;
        long rootJob = RootJob(_data, s.Progress?.Career ?? 0);
        var grants = new List<AwardGrant>(); var next = s.Training?.Clone() ?? new DBTrainingGroundInfo();
        if (result == 0)
            foreach (long id in req.AimAwardIds)
            {
                if (_data.Row("TrainingAimAwardConfig", id) is not { } row) { result = ErrorCodes.ActivityReward; break; }
                if (next.ClaimedAwardIds.Contains(id)) { result = ErrorCodes.RewardClaimed; break; }
                if (GameData.Int(row, "_iRootJob") != rootJob || next.MaxDps < GameData.Int(row, "_iDps"))
                { result = ErrorCodes.TaskRequirements; break; }
                long award = GameData.Int(row, "_iAward");
                grants.AddRange((_data.Row("MultiverseGroupConfig", award) != null ? _awards.Resolve(s, award, 101) : _awards.ResolveDirect(s, award)).Grants);
                next.ClaimedAwardIds.Add(id);
            }
        if (result == 0)
        {
            byte[] claimed = next.ToByteArray();
            string source = $"training/claim/{s.Role!.Id}/{string.Join('-', req.AimAwardIds.Order())}";
            var economy = EventEconomy.ForRole(_data, s, "training-claim", source,
                Array.Empty<(int Entry, long Count)>(), grants,
                new
                {
                    Claimed = req.AimAwardIds.Order().ToArray(), MaxDps = next.MaxDps,
                    Grants = grants.Select(g => new { g.Entry, g.Count, g.BindType, g.ExpireTimeStamp }).ToArray()
                });
            if (!await _character.ApplyAwardAsync(s, new AwardPlan(0, 0, grants), persist: tx =>
                {
                    tx.RoleBlob(s.Role.Id, "training", claimed);
                    tx.WithEventEconomy(economy);
                }, stateLockHeld: true, committed: () => s.Training = next))
                result = ErrorCodes.BagFull;
            else await CharacterService.SyncAsync(s, Sync, next.Clone());
        }
        await s.SendGameAsync(AimAward, new TrainingAimAwardResp { Result = result });
    }

    internal static long RootJob(GameData data, long job)
    {
        // Lua_CFG_JobConfig.GetRootJob stops at the direct child of novice1000, at most ten ancestors.
        for (int depth = 0; depth < 10; depth++)
        {
            if (job == 1000) return job;
            if (data.Row("JobConfig", job) is not { } row) return 0;
            long parent = GameData.Int(row, "_iParentId");
            if (parent == 1000) return job;
            job = parent;
        }
        return 0;
    }

    private async Task FinishAsync(GameSession s, Attempt a)
    {
        try
        {
            if (!a.Running || Now < a.End || !_attempts.TryGetValue(s, out var active) || active != a || s.InstanceId != a.Room.Id) return;
            long dps = a.Damage / Math.Max(1, Duration);
            using var held = await CharacterService.Lock(s);
            if (!a.Running || Now < a.End || !_attempts.TryGetValue(s, out var current)
                || current != a || s.InstanceId != a.Room.Id) return;
            var next = s.Training!.Clone(); next.MaxDps = Math.Max(next.MaxDps, dps);
            var builder = _roles.NewTransaction();
            builder.RoleBlob(s.Role!.Id, "training", next.ToByteArray());
            await _roles.CommitAsync(builder, "training/result", _ =>
            { s.Training = next; a.Running = false; });
            await s.SendGameAsync(DungeonService.Result, new DungeonResNotify
            {
                DungeonEntry = a.Room.SceneEntry,
                Res = (DungeonRes)1, Data = new() { TrainingGroundData = new() { MaxDps = dps, SumDamage = a.Damage } }
            });
            await CharacterService.SyncAsync(s, Sync, next.Clone()); await SnapshotAsync(s, a);
        }
        finally { a.Settling = false; }
    }

    public void Tick(GameTime time)
    {
        foreach (var (s, a) in _attempts.ToArray())
            if (a.Running && !a.Settling && Now >= a.End)
            {
                a.Settling = true; _loop.PostOwned(s, () => FinishAsync(s, a), "training finish",
                discarded: () => a.Settling = false);
            }
    }
}
