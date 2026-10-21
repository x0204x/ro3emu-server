using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Monster Hunt (AreaConcur, UI "MonsterCrusade", function 92).
/// A field kill on a Hunt map counts for that map (AreaConcurMapConfig, its caves in _kNextMap) and, for a listed
/// monster, for that monster (AreaConcurMonsterConfig: kills = curScore, "(Kills: N)"), and rolls the area's drop
/// (_iAreaDropAward: Hunt Medals and region points). The counters ride the kill's own transaction (RewardService
/// StageHuntKill). 83623 claims a monster's reached stages, 83622 one area stage once enough monster stages are
/// claimed in the area (the client's red-dot rule, LC_System_MonsterCrusadeRedDot.lua:40-110); claims are their own
/// cd13e/monsterhunt-award receipts. 5053 DBAreaConcurInfo shows it all; the role blob is "area_concur".
/// (inferred) The killer alone gets credit, as for the kill's loot; area and map scores have no reset.
/// </summary>
public sealed class MonsterHuntService
{
    public const uint Sync = 5053, GainAreaAward = 83622, GainMonsterAward = 83623;
    public const int Function = 92;

    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly SeasonService _seasons;
    private readonly AwardService _awards;
    private readonly RewardService _rewards;
    private readonly TaskService _tasks;
    private readonly RoleStore _roles;
    private readonly ILogger<MonsterHuntService> _log;

    private readonly Dictionary<long, JsonElement> _mapByScene = new();
    private readonly Dictionary<(long Map, int Npc), JsonElement> _monsterByMapNpc = new();
    private readonly Dictionary<long, long> _areaOfMap = new();

    public MonsterHuntService(GameData data, CharacterService character, FunctionService functions, SeasonService seasons,
        AwardService awards, RewardService rewards, TaskService tasks, RoleStore roles, ILogger<MonsterHuntService> log)
    {
        this._data = data; this._character = character; this._functions = functions; this._seasons = seasons;
        this._awards = awards; this._rewards = rewards; this._tasks = tasks; this._roles = roles; this._log = log;
        foreach (var map in Rows("AreaConcurMapConfig"))
        {
            long id = GameData.Int(map, "_iID");
            _areaOfMap[id] = GameData.Int(map, "_iArea");
            _mapByScene.TryAdd(GameData.Int(map, "_iMap"), map);
            // A cave of the map counts for it (_kNextMap, e.g. 10150 -> 10153; inferred attribution).
            if (map.TryGetProperty("_kNextMap", out var caves) && caves.ValueKind == JsonValueKind.Array)
                foreach (var cave in caves.EnumerateArray())
                    if (GameData.Number(cave) is { } scene) _mapByScene.TryAdd((long)scene, map);
        }
        foreach (var monster in Rows("AreaConcurMonsterConfig"))
            _monsterByMapNpc.TryAdd((GameData.Int(monster, "_iMap"), (int)GameData.Int(monster, "_iNpc")), monster);
        rewards.StageHuntKill = StageKill;
        rewards.ExtraKillDrops = AreaDrops;
        rewards.KillDropsGranted.Add(KilledAsync);
    }

    /// <summary>The rows of this playbook (all are _kMultiverseArray [101]).</summary>
    private IEnumerable<JsonElement> Rows(string table) => _data.Table(table).Values
        .SelectMany(v => v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToArray() : [v])
        .Where(r => TowerService.FitsPlayBook(r, _seasons.PlayBook));

    private JsonElement? Area(long areaId) => _data.Row("AreaConcurConfig", areaId);

    /// <summary>The Hunt area the role may count in: function open, area _iCondition met (base level).</summary>
    private bool AreaOpen(GameSession s, JsonElement area) =>
        _functions.IsOpen(s, Function) && _functions.ConditionMet(s, GameData.Int(area, "_iCondition"));

    /// <summary>A field kill on an open Hunt map: its map row and area row.</summary>
    private (JsonElement Map, JsonElement Area)? Counting(GameSession s)
    {
        if (s.Role == null || s.Progress == null || s.InstanceId != 0 || !_mapByScene.TryGetValue(s.SceneEntry, out var map)) return null;
        return Area(GameData.Int(map, "_iArea")) is { } area && AreaOpen(s, area) ? (map, area) : null;
    }

    // ------------------------------------------------------------------ state / 5053

    public void Load(GameSession s)
    {
        var bytes = _roles.LoadAreaConcur(s.Role!.Id);
        s.AreaConcur = bytes is { Length: > 0 } ? DBAreaConcurInfo.Parser.ParseFrom(bytes) : new DBAreaConcurInfo();
        s.AreaConcurPersisted = bytes ?? [];
    }

    public Task LoginAsync(GameSession s) => SyncAsync(s);

    private Task SyncAsync(GameSession s) => CharacterService.SyncAsync(s, Sync, s.AreaConcur!.Clone());

    // ------------------------------------------------------------------ kills

    /// <summary>Hunt Medals and region points: the area's drop, rolled with the kill's own loot (word 74005).</summary>
    private IEnumerable<(int Entry, int Count)> AreaDrops(GameSession s, MonsterUnit monster) =>
        Counting(s) is { } hunt && GameData.Int(hunt.Area, "_iAreaDropAward") is var drop and > 0
            ? _rewards.RollAward(drop, s) : [];

    /// <summary>
    /// The kill's counters, staged into its transaction like the AFK record: the stored blob must still be the one this
    /// session holds, and the session takes the new state only when the kill commits. Re-staging undoes a prior stage.
    /// </summary>
    private void StageKill(GameSession s, MonsterUnit monster, RewardSettlement settlement)
    {
        settlement.ResetHuntStage?.Invoke();
        if (s.AreaConcur is not { } state || Counting(s) is not { } hunt) return;
        long mapId = GameData.Int(hunt.Map, "_iID");
        var next = state.Clone();
        if (!next.MapInfo.TryGetValue(mapId, out var mapInfo)) next.MapInfo[mapId] = mapInfo = new AreaConcurMapInfo();
        if (mapInfo.CurScore < GameData.Int(hunt.Map, "_iMapLimit")) mapInfo.CurScore++;
        if (_monsterByMapNpc.TryGetValue((mapId, monster.Template.NpcId), out var row))
        {
            long id = GameData.Int(row, "_iID");
            if (!next.MonsterInfo.TryGetValue(id, out var info)) next.MonsterInfo[id] = info = new AreaConcurMonsterInfo();
            if (info.CurScore < GameData.Int(row, "_iScoreLimit")) info.CurScore++;
        }
        if (next.Equals(state)) return;
        byte[] before = s.AreaConcurPersisted, after = next.ToByteArray();
        long role = s.Role!.Id;
        var priorEnlist = settlement.Enlist;
        var priorApplied = settlement.Applied;
        settlement.Enlist = builder =>
        {
            priorEnlist?.Invoke(builder);
            if (!(_roles.ReadAreaConcur(builder, role) ?? []).AsSpan().SequenceEqual(before))
                throw new InvalidOperationException("Monster Hunt state changed before the kill's admission.");
            builder.RoleBlob(role, "area_concur", after);
        };
        settlement.Applied = () =>
        {
            priorApplied?.Invoke();
            if (!ReferenceEquals(s.AreaConcur, state)) return;
            s.AreaConcur = next;
            s.AreaConcurPersisted = after;
            s.AreaConcurChanged = true;
        };
        settlement.HuntStaged = true;
        settlement.ResetHuntStage = () =>
        {
            settlement.Enlist = priorEnlist;
            settlement.Applied = priorApplied;
            settlement.HuntStaged = false;
            settlement.ResetHuntStage = null;
        };
    }

    /// <summary>After the kill committed: the new counts reach the panel ("(Kills: N)").</summary>
    private async Task KilledAsync(GameSession s, MonsterUnit monster, IReadOnlyList<(int Entry, long Count)> granted)
    {
        if (!s.AreaConcurChanged || s.AreaConcur == null) return;
        s.AreaConcurChanged = false;
        await SyncAsync(s);
    }

    // ------------------------------------------------------------------ claims

    private int? PlayBook(GameSession s) => s.Synced.GetValueOrDefault(SeasonService.SeasonSync) is DBSeasonInfo { Current: { } current }
        ? (int)current.PlayBookId : null;

    /// <summary>[stage, threshold, MultiverseGroup] tuples of a row's stage list.</summary>
    private static IEnumerable<(long Stage, long Need, long Award)> Stages(JsonElement row, string field) =>
        row.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Array && t.GetArrayLength() >= 3)
                .Select(t => ((long)(GameData.Number(t[0]) ?? 0), (long)(GameData.Number(t[1]) ?? 0), (long)(GameData.Number(t[2]) ?? 0)))
            : [];

    /// <summary>The area's claimed monster stages, the progress its own stages need (capped at _iAreaLimit; inferred).</summary>
    private long ClaimedInArea(DBAreaConcurInfo state, long areaId, JsonElement area)
    {
        long count = 0;
        foreach (var (id, info) in state.MonsterInfo)
            if (_data.Row("AreaConcurMonsterConfig", id) is { } row && _areaOfMap.GetValueOrDefault(GameData.Int(row, "_iMap")) == areaId)
                count += info.ClaimedAward.Count(c => c.Value);
        return Math.Min(count, GameData.Int(area, "_iAreaLimit"));
    }

    /// <summary>83623: every requested stage the monster's kills reach and that is not claimed yet (the client sends them all).</summary>
    public async Task ClaimMonsterAsync(GameSession s, AreaConcurGainMonsterAwardReq req)
    {
        using (await CharacterService.Lock(s))
        {
            var state = s.AreaConcur;
            var row = _data.Row("AreaConcurMonsterConfig", req.MonsterId);
            long areaId = row is { } r ? _areaOfMap.GetValueOrDefault(GameData.Int(r, "_iMap")) : 0;
            if (state != null && row is { } monster && Area(areaId) is { } area && AreaOpen(s, area))
            {
                long kills = state.MonsterInfo.GetValueOrDefault(req.MonsterId)?.CurScore ?? 0;
                var claimed = state.MonsterInfo.GetValueOrDefault(req.MonsterId)?.ClaimedAward;
                var stages = Stages(monster, "_kMonsterAward")
                    .Where(t => req.StageIds.GetValueOrDefault(t.Stage) && kills >= t.Need && claimed?.GetValueOrDefault(t.Stage) != true).ToArray();
                if (stages.Length > 0)
                {
                    var next = state.Clone();
                    if (!next.MonsterInfo.TryGetValue(req.MonsterId, out var info)) next.MonsterInfo[req.MonsterId] = info = new AreaConcurMonsterInfo();
                    foreach (var t in stages) info.ClaimedAward[t.Stage] = true;
                    if (!next.AreaInfo.TryGetValue(areaId, out var areaInfo)) next.AreaInfo[areaId] = areaInfo = new AreaConcurAreaInfo();
                    areaInfo.CurScore = ClaimedInArea(next, areaId, area);
                    await CommitClaimAsync(s, next, stages.Select(t => t.Award).ToArray(),
                        $"monster/{req.MonsterId}/{string.Join(",", stages.Select(t => t.Stage))}");
                }
            }
            await s.SendGameAsync(GainMonsterAward, new AreaConcurGainMonsterAwardResp { Req = req });
        }
        await SyncAsync(s);
        await _tasks.RefreshAsync(s); // 318004: the area's collection progress
    }

    /// <summary>83622: an area stage once the area's claimed monster stages reach its threshold (one stage per request).</summary>
    public async Task ClaimAreaAsync(GameSession s, AreaConcurGainAreaAwardReq req)
    {
        using (await CharacterService.Lock(s))
        {
            var state = s.AreaConcur;
            if (state != null && Area(req.AreaId) is { } area && AreaOpen(s, area))
            {
                long progress = ClaimedInArea(state, req.AreaId, area);
                var claimed = state.AreaInfo.GetValueOrDefault(req.AreaId)?.ClaimedAward;
                var stages = Stages(area, "_kAreaStageAward")
                    .Where(t => req.StageIds.GetValueOrDefault(t.Stage) && progress >= t.Need && claimed?.GetValueOrDefault(t.Stage) != true).ToArray();
                if (stages.Length > 0)
                {
                    var next = state.Clone();
                    if (!next.AreaInfo.TryGetValue(req.AreaId, out var info)) next.AreaInfo[req.AreaId] = info = new AreaConcurAreaInfo();
                    info.CurScore = progress;
                    foreach (var t in stages) info.ClaimedAward[t.Stage] = true;
                    await CommitClaimAsync(s, next, stages.Select(t => t.Award).ToArray(),
                        $"area/{req.AreaId}/{string.Join(",", stages.Select(t => t.Stage))}");
                }
            }
            await s.SendGameAsync(GainAreaAward, new AreaConcurGainAreaAwardResp { Req = req });
        }
        await SyncAsync(s);
    }

    /// <summary>The stages' awards and the claimed markers in one owned-award receipt; on any failure nothing changed.</summary>
    private async Task CommitClaimAsync(GameSession s, DBAreaConcurInfo next, long[] groups, string claim)
    {
        long role = s.Role!.Id;
        AwardPlan[] plans;
        try { plans = groups.Select(g => _awards.Resolve(s, g, PlayBook(s))).ToArray(); }
        catch (AwardResolutionException e)
        {
            _log.LogWarning("Monster Hunt {Claim} reward unresolved for {Role}: {Reason}", claim, role, e.Message);
            return;
        }
        byte[] bytes = next.ToByteArray();
        try
        {
            bool ok = await _character.ApplyAwardAsync(s, new AwardPlan(0, 0, plans.SelectMany(p => p.Grants)),
                persist: tx => _roles.SaveAreaConcur(role, bytes, tx), stateLockHeld: true,
                committed: () => { s.AreaConcur = next; s.AreaConcurPersisted = bytes; },
                ownedAward: new CharacterService.OwnedAwardSource("cd13e/monsterhunt-award", $"monsterhunt/{role}/{claim}",
                    new { Version = 1, Claim = claim, Awards = plans.Select(p => p.AwardId).ToArray() }));
            if (!ok) _log.LogWarning("Monster Hunt {Claim} not paid for {Role}", claim, role);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError(e, "Monster Hunt {Claim} transaction failed for {Role}", claim, role);
        }
    }

    /// <summary>TaskConfig target 318004 [318004, area, n]: the area's collection progress (claimed monster stages).</summary>
    public static long AreaProgress(GameSession s, long areaId) => s.AreaConcur?.AreaInfo.GetValueOrDefault(areaId)?.CurScore ?? 0;
}
