using Google.Protobuf;
using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    public const uint Statistics = 30110, StatisticsHistory = 30114, StatisticsModule = 30116,
        StatisticsCurrent = 30117, PlayerStatistics = 30123, TaskScore = 30126, QueryResult = 30128, ResultNotify = 30132;

    /// <summary>Real shared combat counters; zero task score until a recovered task-scoring rule exists.</summary>
    private static DungeonStatisticsInfoWithDungeon StatisticsOf(Attempt a)
    {
        var result = new DungeonStatisticsInfoWithDungeon
        {
            StatisticsId = a.Room.Id,
            DungeonId = (int)a.Plan.SceneEntry, StatisticsTime = MonsterService.NowMs
        };
        var scene = new SceneStatisticsInfo { SceneId = a.Room.Id, DungeonId = (int)a.Plan.SceneEntry };
        foreach (var (id, stats) in a.Stats) scene.RoleStatistics[id] = stats.Clone();
        result.SceneStatistics[a.Room.Id] = scene;
        return result;
    }

    public Task StatisticsAsync(GameSession s)
    {
        var response = new DungeonStatisticsRes { Result = s.DungeonAttempt == null ? ErrorCodes.SceneMissing : 0 };
        if (s.DungeonAttempt is { } a)
        {
            var map = new DungeonStatisticsInfo { DungeonMapId = a.Plan.SceneEntry };
            foreach (var value in a.Stats.Values)
                map.DungeonRolesInfo.Add(new DungeonRoleInfo
                {
                    RoleBase = new ObjScoreBaseInfo { Id = value.BaseInfo.Id, Name = value.BaseInfo.Name, Job = value.BaseInfo.Job, Area = 1 },
                    TotalDamage = value.TotalDamage, TotalBeDamage = value.TotalBeDamage, TotalCure = value.TotalCure, Totalscore = value.Totalscore,
                });
            response.DungeonRolesInfo[a.Room.Id] = map;
        }
        return s.SendGameAsync(Statistics, response);
    }

    /// <summary>Another mode's live report for 30117 (CasualBattleService: Mock Battle), or null.</summary>
    internal Func<GameSession, DungeonStatisticsInfoWithDungeon?>? CurrentStatisticsOwner { get; set; }

    public Task StatisticsCurrentAsync(GameSession s)
    {
        var other = s.DungeonAttempt == null ? CurrentStatisticsOwner?.Invoke(s) : null;
        return s.SendGameAsync(StatisticsCurrent, new GetDungeonStatisticsInDungeonResp
        {
            Result = s.DungeonAttempt == null && other == null ? ErrorCodes.SceneMissing : 0,
            DungeonStatistics = s.DungeonAttempt is { } a ? StatisticsOf(a) : other
        });
    }

    public Task StatisticsModuleAsync(GameSession s)
    {
        var msg = new DungeonStatisticsModuleRsp { Result = s.DungeonAttempt == null ? ErrorCodes.SceneMissing : 0 };
        if (s.DungeonAttempt is { } a)
        {
            var stats = StatisticsOf(a);
            msg.StatisticsId = stats.StatisticsId; msg.DungeonId = stats.DungeonId; msg.StatisticsTime = stats.StatisticsTime;
            msg.SceneStatistics.Add(stats.SceneStatistics);
        }
        return s.SendGameAsync(StatisticsModule, msg);
    }

    /// <summary>30114 only exposes the requesting role's persisted history or its own current instance.</summary>
    public Task StatisticsHistoryAsync(GameSession s, DungeonStatisticsInfoWithDungeonReq req)
    {
        var msg = new DungeonStatisticsInfoWithDungeonResp();
        if (req.StaisticsIds.Count is 0 or > 20) msg.Result = ErrorCodes.Parameter;
        else foreach (long id in req.StaisticsIds.Distinct())
        {
            if (s.DungeonAttempt is { } a && a.Room.Id == id) msg.DungeonStatistics.Add(StatisticsOf(a));
            else if (s.Dungeons?.Statistics.TryGetValue(id, out var bytes) == true)
                msg.DungeonStatistics.Add(DungeonStatisticsInfoWithDungeon.Parser.ParseFrom(bytes));
            else { msg.Result = ErrorCodes.NoPermission; msg.DungeonStatistics.Clear(); break; }
        }
        return s.SendGameAsync(StatisticsHistory, msg);
    }

    public Task QueryResultAsync(GameSession s, QueryDungeonResultReq req)
    {
        var response = new QueryDungeonResultResp(); // Schema has no error field. Unknown/foreign result ids stay absent.
        if (req.ResultIds.Count <= 20 && s.Dungeons != null)
            foreach (var id in req.ResultIds.Distinct())
                if (s.Dungeons.Results.TryGetValue(id, out var bytes)) response.Results.Add(DBDungeonResultData.Parser.ParseFrom(bytes));
        return s.SendGameAsync(QueryResult, response);
    }

    private static string ResultId(Attempt a) => a.Room.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void StoreHistory(State state, Attempt a)
    {
        state.Statistics[a.Room.Id] = StatisticsOf(a).ToByteArray();
        var result = new DBDungeonResultData
        {
            ResultId = ResultId(a), ServerId = 1, DungeonId = a.Room.Id,
            DungeonEntryId = (int)a.Plan.SceneEntry, CreateTime = MonsterService.NowMs
        };
        // Mode-specific SecondResultType/ResultParam have no recovered ordinary-dungeon mapping.
        state.Results[result.ResultId] = result.ToByteArray();
        // Twenty owned reports retained across login (inferred retention policy, no globally queryable role data).
        foreach (long id in state.Statistics.Keys.Order().Take(Math.Max(0, state.Statistics.Count - 20)).ToArray())
        { state.Statistics.Remove(id); state.Results.Remove(id.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
    }

    // LC_AssetDatasetDefine routes 30123 to Dragon Island and 30132 to Dragon Island/LeisureForty.
    // Those modes are not ordinary boss dungeons. Their pushes are intentionally not produced here.
    // 30126's task-score alias and server scoring formula remain unverified.
}
