using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Micro;

/// <summary>
/// "RankService": the leaderboards. The client asks three methods
/// (LC_DAL_Rank.lua:38-40,60-72; sends 400-567):
/// <list type="bullet">
/// <item>rangeUserRank [rankId, 1, count] (SendLeaderBoardSyncByRealLeaderBoardId) -> _proessLeaderBoardSync 856-913,
/// which replaces RankInfoMap[board] by RankList keyed by Rank and then asks queryUserRank for the local player;</item>
/// <item>queryUserRank [rankId, [uid string]] (SendForGetUserRank) -> _proessLeaderUserInfo 913-977, ignored unless
/// Count is non-zero, merges RankList by Rank;</item>
/// <item>getUserRankSurrounding {RankId, UserId, Before, After} -> _processPlayerNearRank 693-832, which splits the rows
/// around the row whose UserId is the requested one.</item>
/// </list>
/// rankId is "%d:%d" season area and board id, optionally ":%d" sub key; replies are keyed by split(rankId)[2]. A row is
/// {UserId (string), Rank (>= 0), Score{Values:[v1, v2]} (non-empty), GameData (romsg.LeaderBoardUserDada bytes)}
/// (ParseUserRankInfo 1100-1141). Ranks are 1-based and contiguous.
/// <para>
/// Boards are computed from committed role and guild state (offline roles included) and cached for
/// PublicParamConfig.LeaderBoardRefreshIntervalTs seconds, the client's own refresh interval (inferred reuse).
/// Only entries with a positive score are listed (inferred: the original fed boards on progress events).
/// _iSortingRules [[1,1],[2,2]] is read as Values[1] descending, then the board's tie-break ascending (inferred).
/// Reward settlement (_iRefresh, _kPrizesTimeNew, _iRankRewards, LeaderBoardsGiftConfig) is not implemented.
/// </para>
/// </summary>
public sealed class RankService(GameData data, AccountStore accounts, GuildService guilds, CharacterService character,
    EmblemService emblems, TimeProvider clock, ILogger<RankService> log) : IMicroService
{
    // Board ids: LeaderBoardsConfig _iID / enum_LeaderBoardType (Lua_GameDefine.lua:10440-10552).
    public const int SpiritTower = 1011, GuildPrestige = 1030, GuildActivity = 1031, SurvivorOverall = 1052, SurvivorWeeklyKills = 1053,
        SurvivorWeeklyWins = 1054, HeroAll = 1101, DummyOverall = 1201, Fashion = 1301, Equipment = 1401,
        DragonOverall = 1601, DragonWeeklyMvp = 1602;

    /// <summary>
    /// UI_Sys_RankCopyMainPanelModel.lua:24-45: job -1 (all) -> 1101, root jobs -> 1103-1108. 1101 has no
    /// LeaderBoardsConfig row; 1103-1108 carry the same mapping in _kJobID.
    /// </summary>
    private static readonly IReadOnlyDictionary<long, int> HeroBoards = new Dictionary<long, int>
    {
        [1100] = 1103, [4100] = 1104, [2100] = 1105, [3100] = 1106, [5100] = 1107, [6100] = 1108,
    };

    private readonly object _gate = new();
    private Task<Snapshot>? _snapshot;
    private DateTimeOffset _builtAt;

    public string Name => "RankService";

    public async ValueTask<object?> InvokeAsync(MicroSession session, string method, object? args)
    {
        switch (method)
        {
            case "rangeUserRank":
                {
                    if (!TryParseRankId(Arg(args, 0, "RankId"), out int boardId, out bool sub)) return MicroServiceRouter.Fail("bad rank id");
                    long start = Math.Max(1, Number(Arg(args, 1, "Start")));
                    long end = Number(Arg(args, 2, "End"));
                    var board = sub ? null : (await BoardsAsync()).Boards.GetValueOrDefault(boardId);
                    var rows = board == null || end < start ? []
                        : board.Rows.Skip((int)Math.Min(start - 1, board.Rows.Count)).Take((int)Math.Min(end - start + 1, board.Rows.Count))
                            .Select((row, i) => Row(row, (int)start + i)).ToArray();
                    return Reply(rows, board);
                }
            case "queryUserRank":
                {
                    if (!TryParseRankId(Arg(args, 0, "RankId"), out int boardId, out bool sub)) return MicroServiceRouter.Fail("bad rank id");
                    var boards = await BoardsAsync();
                    var board = sub ? null : boards.Boards.GetValueOrDefault(boardId);
                    var rows = new List<object>();
                    if (board != null && Arg(args, 1, "UserIds") is object?[] ids)
                        foreach (var value in ids)
                        {
                            if (!long.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out long id)) continue;
                            // Guild boards: the client asks for the local player (_proessLeaderBoardSync 905-911); its own
                            // guild row is the one the footer can show (inferred).
                            if (!board.RankOf.ContainsKey(id) && board.Guild && boards.GuildOf.TryGetValue(id, out long guild)) id = guild;
                            if (board.RankOf.TryGetValue(id, out int rank)) rows.Add(Row(board.Rows[rank - 1], rank));
                        }
                    return Reply(rows.ToArray(), board);
                }
            case "getUserRankSurrounding":
                {
                    if (!TryParseRankId(Arg(args, -1, "RankId"), out int boardId, out bool sub)) return MicroServiceRouter.Fail("bad rank id");
                    if (!long.TryParse(Convert.ToString(Arg(args, -1, "UserId"), System.Globalization.CultureInfo.InvariantCulture), out long id))
                        return MicroServiceRouter.Fail("bad user id");
                    // (inferred) a bounded window; the client asks a few rows each side.
                    int before = (int)Math.Clamp(Number(Arg(args, -1, "Before")), 0, 100);
                    int after = (int)Math.Clamp(Number(Arg(args, -1, "After")), 0, 100);
                    var board = sub ? null : (await BoardsAsync()).Boards.GetValueOrDefault(boardId);
                    object[] rows = [];
                    if (board != null && board.RankOf.TryGetValue(id, out int rank))
                    {
                        int first = Math.Max(1, rank - before), last = Math.Min(board.Rows.Count, rank + after);
                        rows = Enumerable.Range(first, last - first + 1).Select(r => Row(board.Rows[r - 1], r)).ToArray();
                    }
                    return Reply(rows, board);
                }
            default:
                return null;
        }
    }

    /// <summary>31470 EQUIP_GET_RANK: the item's place on the equipment board (enum_LeaderBoardID.EquipRank), 0 unranked.</summary>
    public async Task<int> EquipRankAsync(int boardId, long itemGuid) =>
        boardId == Equipment && (await BoardsAsync()).Boards.TryGetValue(Equipment, out var board)
            && board.RankOf.TryGetValue(itemGuid, out int rank) ? rank : 0;

    /// <summary>Drops the cached boards so the next request recomputes them (tests, GM tools).</summary>
    internal void Invalidate()
    {
        lock (_gate) _snapshot = null;
    }

    private static Dictionary<string, object?> Reply(object[] rows, Board? board) => new()
    {
        ["Ret"] = MicroServiceRouter.Ret(true),
        ["RankList"] = rows,
        ["Count"] = board?.Rows.Count ?? 0,
    };

    private static Dictionary<string, object?> Row(Entry entry, int rank) => new()
    {
        ["UserId"] = entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Rank"] = rank,
        ["Score"] = new Dictionary<string, object?> { ["Values"] = new object[] { entry.Score, entry.Value2 } },
        ["GameData"] = entry.GameData,
    };

    /// <summary>"&lt;seasonArea&gt;:&lt;board&gt;[:&lt;sub&gt;]" (LC_DAL_Rank.lua:418-436,486-507,530-545).</summary>
    internal static bool TryParseRankId(object? value, out int board, out bool sub)
    {
        board = 0;
        var parts = (value as string ?? "").Split(':');
        sub = parts.Length > 2;
        return parts.Length is 2 or 3 && long.TryParse(parts[0], out _) && int.TryParse(parts[1], out board)
            && (!sub || long.TryParse(parts[2], out _));
    }

    /// <summary>Positional Lua arrays decode as object[]; a map is read by key.</summary>
    private static object? Arg(object? args, int index, string key) => args switch
    {
        object?[] list => index >= 0 && index < list.Length ? list[index] : null,
        IDictionary<object, object?> map => map.TryGetValue(key, out var v) ? v
            : index >= 0 && map.TryGetValue(index + 1, out var p) ? p : null,
        _ => null,
    };

    private static long Number(object? value) =>
        long.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out long n) ? n : 0;

    private sealed record Entry(long Id, long Score, long Value2, long Tie1, long Tie2, byte[] GameData);

    private sealed record Candidate(long Id, long Score, long Value2, long Tie1, long Tie2, Func<byte[]> GameData);

    private sealed class Board(List<Entry> rows, bool guild)
    {
        public List<Entry> Rows { get; } = rows;
        public bool Guild { get; } = guild;
        public Dictionary<long, int> RankOf { get; } = rows.Select((r, i) => (r.Id, Rank: i + 1)).ToDictionary(p => p.Id, p => p.Rank);
    }

    private sealed record Snapshot(IReadOnlyDictionary<int, Board> Boards, IReadOnlyDictionary<long, long> GuildOf);

    private Task<Snapshot> BoardsAsync()
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (_snapshot == null || _snapshot.IsFaulted || _snapshot.IsCompleted && now - _builtAt >= Ttl)
            {
                _snapshot = Task.Run(BuildAsync);
                _builtAt = now;
            }
            return _snapshot;
        }
    }

    private TimeSpan Ttl => TimeSpan.FromSeconds(data.Table("PublicParamConfig").TryGetValue("LeaderBoardRefreshIntervalTs", out var v)
        && GameData.Number(v) is { } seconds && seconds > 0 ? seconds : 30);

    private async Task<Snapshot> BuildAsync()
    {
        var source = await accounts.ReadAsync<RankSource>(new RankSourceRead());
        var guildList = await accounts.ReadAsync<List<GuildStore.State>>(new GuildReadPlan(GuildReadKind.List));
        var boards = new Dictionary<int, List<Candidate>>();
        void Add(int board, Candidate candidate)
        {
            if (!boards.TryGetValue(board, out var list)) boards[board] = list = new();
            list.Add(candidate);
        }
        var dummyBoards = DummyBoards();
        foreach (var role in source.Roles)
        {
            var items = source.Items.GetValueOrDefault(role.Id) ?? [];
            long rootJob = TrainingService.RootJob(data, role.Career);
            Tower(role, Add);
            if (role.Training is { Length: > 0 } && DBTrainingGroundInfo.Parser.ParseFrom(role.Training) is { MaxDps: > 0 } training)
            {
                // Piling DPS (UI_Sys_Piling_ChallengeCompletePanelCtrl.lua:116-157): 1201 overall plus the root job's
                // board from PublicParamConfig.TrainingRootJob2LdId. DBTrainingGroundInfo keeps one maxDps, so the
                // role is ranked under its current job line (inferred). UserDefineData keys: LeaderBoardSelfDefineDataType.
                var common = new Commondata();
                common.UserDefineData[(int)LeaderBoardSelfDefineDataType.LdUserId] = role.Id;
                common.UserDefineData[(int)LeaderBoardSelfDefineDataType.LdCarrer] = role.Career;
                var candidate = new Candidate(role.Id, training.MaxDps, 0, 0, 0,
                    () => new LeaderBoardUserDada { UserCommonData = common }.ToByteArray());
                Add(DummyOverall, candidate);
                if (dummyBoards.TryGetValue(rootJob, out int jobBoard)) Add(jobBoard, candidate);
            }
            if (role.Wardrobe is { Length: > 0 })
            {
                long fashion = WardrobeService.FashionScore(data, DBWardRobeInfo.Parser.ParseFrom(role.Wardrobe));
                if (fashion > 0) Add(Fashion, new Candidate(role.Id, fashion, 0, 0, 0, () => []));
            }
            if (role.TimedBattle is { Length: > 0 } && JsonSerializer.Deserialize<TimedBattleService.State>(role.TimedBattle) is { } battle)
            {
                // _refreshChickenTierShow / _refreshDragonIslandRankShow read the score as total rank points
                // (Lua_CFG_EatChickenLevelConfig.GetBindRank, Lua_CFG_DragonIslandLevelConfig.GetRankIdByPoint).
                if (battle.ChickenMatches > 0 && battle.ChickenScore > 0)
                    Add(SurvivorOverall, new Candidate(role.Id, battle.ChickenScore, 0, 0, 0, () => []));
                if (battle.DragonSettled.Count > 0 && DragonPoints(battle) is > 0 and var points)
                    Add(DragonOverall, new Candidate(role.Id, points, 0, 0, 0, () => []));
                // 1053/1054 (weekly Survivor kills/wins) and 1602 (weekly Dragon MVPs) have no persisted weekly
                // counters on this server; those boards stay empty until the battles record them.
            }
            foreach (var item in items)
            {
                // Equipment board rows are item instances: GetRankItemInfo(m_iGuid) is keyed by the instance id and
                // equipMetaData.userId names the owner (ParseUserRankInfo 1110-1125, cell _refreshEquipName/Owner).
                // Every owned appraised piece counts (inferred). The crafter is not recorded, so craftName stays empty.
                long score = EquipmentRating.Calculate(item, data);
                if (score <= 0) continue;
                var owned = item;
                Add(Equipment, new Candidate(item.Guid, score, 0, 0, 0, () => new LeaderBoardUserDada
                {
                    EquipMetaData = new EquipMetaData { UserId = role.Id, CraftName = "", ItemBaseInfo = character.ItemInfoOf(owned).ToByteString() },
                }.ToByteArray()));
            }
            // Hero Registry (UI_Sys_RankCopyMainPanel): ranked by the role's displayed power, normalPower +
            // seasonPower: the total PowerService last published and saved with the Soul Echo
            // state. A role that never logged in since has only its Soul Echo plan to rank by.
            long power = PowerService.StoredTotal(role.Emblems) ?? emblems.StoredPower(role.Emblems, items);
            if (power > 0)
            {
                Add(HeroAll, new Candidate(role.Id, power, 0, 0, 0, () => []));
                if (HeroBoards.TryGetValue(rootJob, out int heroBoard)) Add(heroBoard, new Candidate(role.Id, power, 0, 0, 0, () => []));
            }
        }
        var guildOf = new Dictionary<long, long>();
        foreach (var guild in guildList)
        {
            foreach (long member in guild.Members.Keys) guildOf[member] = guild.Id;
            if (guild.Status != (int)GuildStatus.Normal) continue;
            var g = guild;
            if (guild.Reputation > 0)
                Add(GuildPrestige, new Candidate(guild.Id, guild.Reputation, 0, 0, 0, () => new LeaderBoardUserDada { GveData = Gve(g) }.ToByteArray()));
            // 1031 Guild Activity (_iRefresh [2], weekly): this week's Guild Activity, which resets Monday 05:00 (UITips 17019).
            if (guilds.WeekActivityOf(guild) is > 0 and var activity)
                Add(GuildActivity, new Candidate(guild.Id, activity, 0, 0, 0, () => new LeaderBoardUserDada { GveData = Gve(g) }.ToByteArray()));
        }
        var result = new Dictionary<int, Board>();
        foreach (var (id, candidates) in boards)
        {
            var rows = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Tie1).ThenBy(c => c.Tie2).ThenBy(c => c.Id)
                .Take(FullRankMax(id)).Select(c => new Entry(c.Id, c.Score, c.Value2, c.Tie1, c.Tie2, c.GameData())).ToList();
            result[id] = new Board(rows, id is GuildPrestige or GuildActivity);
        }
        log.LogDebug("Leaderboards rebuilt: {Boards}", string.Join(", ", result.Select(b => $"{b.Key}={b.Value.Rows.Count}")));
        return new Snapshot(result, guildOf);
    }

    /// <summary>
    /// 1011 Spirit Tower: the highest cleared floor (PetFloorConfig._iFloor of DBClimTower.layerConfID), its clear time
    /// (_iTimeLimit - remainDuration, the 31805 DurationTime rule of TowerService.InfoAsync) and the saved formation
    /// (cell _refreshClimbTowerSquad shows petDatas). Ties: faster clear, then earlier completion (inferred).
    /// </summary>
    private void Tower(RankSourceRole role, Action<int, Candidate> add)
    {
        if (role.Tower is not { Length: > 0 }) return;
        var tower = DBClimTower.Parser.ParseFrom(role.Tower);
        if (data.Row("PetFloorConfig", tower.LayerConfID) is not { } floor) return;
        long layer = GameData.Int(floor, "_iFloor");
        if (layer <= 0) return;
        int duration = (int)Math.Max(0, GameData.Int(floor, "_iTimeLimit") - tower.RemainDuration);
        var pets = role.Pets is { Length: > 0 } ? DBPetInfo.Parser.ParseFrom(role.Pets).ListPet : null;
        add(SpiritTower, new Candidate(role.Id, layer, tower.CompleteTime, duration, tower.CompleteTime, () =>
        {
            var climb = new ClimbTowerLeaderBoardUserData { TowerLayer = (int)layer, DurationTime = duration, CompleteTime = tower.CompleteTime };
            foreach (int pet in tower.PetIds.Where(p => p != 0))
            {
                climb.PetIds.Add(pet);
                if (pets != null && pets.TryGetValue(pet, out var petData)) climb.PetDatas.Add(petData);
            }
            return new LeaderBoardUserDada { ClimbTowerData = climb }.ToByteArray();
        }));
    }

    /// <summary>
    /// Total Dragon Coffin rank points as GetRankIdByPoint expects them: the _iPointLimit of every passed level
    /// plus the current level's points. Our settlement keeps the remainder in DragonRankScore (inferred inverse).
    /// </summary>
    private long DragonPoints(TimedBattleService.State battle)
    {
        long points = battle.DragonRankScore;
        for (int level = 1; level < battle.DragonRank; level++)
            points += data.Row("DragonIslandLevelConfig", level) is { } row ? GameData.Int(row, "_iPointLimit") : 0;
        return points;
    }

    /// <summary>Guild board GameData (ParseUserRankInfo 1066-1095); names and icons also come from GuildSnap by guild id.</summary>
    private GuildLeaderBoardGveUserData Gve(GuildStore.State g)
    {
        var leader = g.Members.Values.FirstOrDefault(m => m.Official == 1);
        return new GuildLeaderBoardGveUserData
        {
            GuildId = g.Id, GuildName = g.Name, HeadIcon = GuildService.Icon(g), GuildOwnerId = leader?.RoleId ?? 0,
            GuildOwnerName = leader?.Name ?? "", GuildOwnerJob = leader?.Career ?? 0, MaxMemberCount = guilds.MaxMembers(g),
            MemberCount = g.Members.Count, GuildLevel = g.Level,
            // (inferred) the recruit line is the guild announcement; LeaderOfficial and Activity have no source here.
            RecruitInfo = g.Announce,
        };
    }

    /// <summary>PublicParamConfig.TrainingRootJob2LdId {"1100":1202,...}: root job -> Dummy Challenge class board.</summary>
    private Dictionary<long, int> DummyBoards()
    {
        var map = new Dictionary<long, int>();
        if (data.Table("PublicParamConfig").TryGetValue("TrainingRootJob2LdId", out var value) && value.ValueKind == JsonValueKind.Object)
            foreach (var pair in value.EnumerateObject())
                if (long.TryParse(pair.Name, out long job) && GameData.Number(pair.Value) is { } board) map[job] = (int)board;
        return map;
    }

    /// <summary>LeaderBoardsConfig._iFullRankMax; 1101 has no row and takes its siblings' 3000 (inferred).</summary>
    private int FullRankMax(int board) => data.Row("LeaderBoardsConfig", board) is { } row
        ? (int)GameData.Int(row, "_iFullRankMax") : board == HeroAll ? 3000 : 0;
}
