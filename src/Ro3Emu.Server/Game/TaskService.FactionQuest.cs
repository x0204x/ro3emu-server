using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Faction Quest, the territory world's daily (function 7202 Function_TerritoryWarTask,
/// MG_Function.proto:148). The client only shows it: the HUD widget UI_Sys_Main_TeamUp_FactionTask_NewWidget
/// reads PublicParam TerritoryWorldTaskID 1101000 and TerritoryWarDailyRewardTask 1101001 from 5027
/// (UI_Sys_Main_TeamUp_FactionTask_NewWidgetBaseCtrl._refreshWidget), and lists every type 25 TerritoryWarsSubTask in
/// the task infos by name (UI_Sys_Main_TeamUp_FactionTaskWidgetCtrl._refreshSubTaskList) under "Points you can earn
/// today: x/y" (word 90353, x/y = LC_TerritoryWarsHelper.GetTerritoryWarsTaskProgress: completeNum[1] / the 101022
/// R of 1101000). Everything else is the server's:
/// <list type="bullet">
/// <item>1101000 "Faction Quest" (type 11, _kAcceptTask [0], Auto delivery, [101022,1000] "Earn @{1} points") is
/// offered each game day while function 7202 is open (inferred: no accept path exists in the client; NPC 200053 Guild
/// Services Staff greets with dialogue 10000105 "Did you finish today's task?", so the task is already running).
/// Its Auto follow-up 1101001 "Claim" ([104001,72,30307,10000106]: talk to 200053 at point 72 of 30307, the
/// "GotoGetTerritoryWarsTaskAward" path, LC_System_TerritoryWars_PathManager GreenZone_TaskAward) is turned in from
/// the UI (_kDeliverTask [3]) for _iAward 90404 and 100 base exp.</item>
/// <item>The nine type 25 sub-tasks come in three kinds of three tiers, the tier in the points of their _iName
/// ([text, "points"]): 107102 [3010,1] "Collect Magic Crystals" +1/+2/+3, 104002 [4940017,1] "Kill Monster A" (Orc
/// Warrior) +5/+7/+10, 101023 [1] "Defeat players from other guilds" +10/+15/+20. The server offers one of each kind,
/// the tier of the role's territory camp: Peace 1, Free 2, Guild 3 (inferred: the camps carry the territory world's
/// rising risk, PublicParam TerritoryWorldDeathDropRate [0, 0.2, 1] loses that share of the carried ore on a death,
/// and the tiers pay rising points). A changed camp swaps the offer. Each sub-task is one action: it is delivered
/// at once, its points are added and the same sub-task is offered again (inferred: the rows repeat nothing
/// themselves, _iIfRepeat 0).</item>
/// <item>107102: one Crystalline Ore cluster collected in the territory world (NPC 4910101-4910103 "Single/Double/
/// Multi-Cluster Crystalline Ore", TerritoryWarsService.World pickups; the dropped Crystallized Ore 4910104 is loot,
/// not a cluster). The 3010 is not read (inferred: no client table names it) and R is the count p3, not the default
/// formatter's p2; the HUD shows only the names. 101023: a kill of a WorldHostile player of another guild in the
/// territory world (TerritoryWarsService.WorldRivals). 104002 is the ordinary kill target.</item>
/// <item>PublicParam TerritoryWarzoneTaskPointsLimit 3000 caps the points counted in a game day, the 1000 of 1101000
/// and the "exceed" past it (the server enum TaskExceedKey_TerritoryWar, MG_DB.proto:36, which no client message
/// carries): the sub-tasks stay offered until the cap (inferred). The day is DailyActivityService's game day
/// (05:00): a new day drops the faction tasks and the count.</item>
/// </list>
/// TerritoryWarzoneTaskGuildAward 1021 names no MultiverseGroupConfig or AwardConfig row in this client; the guild's
/// share for a delivered 1101000 ("provide Guild Coins to the guild", its _iBeforeDescribe) is paid by
/// <see cref="FactionGuildAward"/> (GuildCaravanService.FactionAward.cs, inferred).
/// </summary>
public sealed partial class TaskService
{
    /// <summary>LC_ConditionDefine CheckTerritoryWarzoneTaskPoint; 107102 and 101023 have no client name.</summary>
    private const int TFactionPoints = 101022, TFactionCrystal = 107102, TRivalKill = 101023;

    private const int FactionSubTaskType = 25;
    public const int FactionFunction = 7202;

    /// <summary>The role's territory camp (EPvpRoleCampNum), set by the host from TerritoryWarsService.</summary>
    public Func<GameSession, int>? FactionCamp { get; set; }

    /// <summary>The killer earns 101023 for this victim (TerritoryWarsService.WorldRivals), set by the host.</summary>
    public Func<GameSession, GameSession, bool>? FactionRivals { get; set; }

    /// <summary>The guild's share of a delivered 1101000: (role, game day); the owner posts its own work. Set by the host.</summary>
    public Action<GameSession, long>? FactionGuildAward { get; set; }

    private Dictionary<long, long>? _factionPoints;
    private Dictionary<long, int>? _factionTiers;

    private long FactionQuest => FactionParam("TerritoryWorldTaskID", 1101000);
    private long FactionClaim => FactionParam("TerritoryWarDailyRewardTask", 1101001);

    private long FactionParam(string key, long fallback) =>
        _data.Table("PublicParamConfig").TryGetValue(key, out var v) ? (long)(GameData.Number(v) ?? fallback) : fallback;

    // ---- events ----

    /// <summary>107102: a Crystalline Ore cluster collected (TerritoryWarsService, after its commit).</summary>
    public void OnFactionCrystalCollected(GameSession s)
    {
        if (Holds(s, TFactionCrystal))
            _loop.PostOwned(s, () => ProgressAsync(s, TFactionCrystal, t => t is [TFactionCrystal, _, _] ? 1 : 0),
                "quest faction crystal");
    }

    /// <summary>101023: CombatService.PlayerKilled of a player of another guild in the territory world.</summary>
    internal Task OnFactionPlayerKilledAsync(GameSession killer, GameSession victim)
    {
        if (Holds(killer, TRivalKill) && FactionRivals?.Invoke(killer, victim) == true)
            _loop.PostOwned(killer, () => ProgressAsync(killer, TRivalKill, t => t is [TRivalKill, _] ? 1 : 0),
                "quest faction kill");
        return Task.CompletedTask;
    }

    /// <summary>The territory camp is set or loaded: a running sub-task of another tier is swapped for the camp's.</summary>
    public void OnFactionCampChanged(GameSession s)
    {
        int tier = FactionTier(s);
        if (s.Tasks is { } tasks && FactionTiers().Any(t => t.Value != tier && tasks.Infos.ContainsKey(t.Key))) QueueRefresh(s);
    }

    /// <summary>
    /// 101022: a delivered sub-task adds its points to the running faction quest and to the day's count, up to the day's
    /// cap (QuestDelivered, inside the quest lock; the settlement that delivered it saves and sends the change).
    /// </summary>
    private void AddFactionPoints(GameSession s, long taskId, AwardPlan _)
    {
        if (taskId == FactionQuest) FactionGuildAward?.Invoke(s, s.Daily?.DailyEpoch ?? 0);
        if (s.Tasks == null || !FactionPoints().TryGetValue(taskId, out long points)) return;
        TakeFactionDay(s);
        var runtime = s.TaskRuntime;
        points = Math.Min(points, Math.Max(0, FactionParam("TerritoryWarzoneTaskPointsLimit", 3000) - runtime.FactionPoints));
        if (points <= 0) return;
        runtime.FactionPoints += points;
        foreach (var info in s.Tasks.Infos.Values.Where(i => i.Status == Running))
        {
            var def = Defs()[info.TaskId];
            for (int i = 0; i < def.Targets.Length && i < info.CompleteNum.Count; i++)
                if (def.Targets[i] is [TFactionPoints, var required] && required > 0)
                    info.CompleteNum[i] = Math.Min(required, Math.Max(0, info.CompleteNum[i]) + points);
        }
    }

    /// <summary>Type 25 task → the points its _iName names ([text, "points"]).</summary>
    private Dictionary<long, long> FactionPoints() => _factionPoints ??= TaskConfig.All(_data)
        .Where(r => r.TaskType == FactionSubTaskType)
        .Select(r => (r.TaskId, Points: r.Name is { ValueKind: JsonValueKind.Array } name && name.GetArrayLength() >= 2
            && name[1].ValueKind == JsonValueKind.String
            && long.TryParse(name[1].GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long p) ? p : 0))
        .Where(r => r.Points > 0).ToDictionary(r => r.TaskId, r => r.Points);

    /// <summary>Type 25 task → its tier 1-3: its place by points among the sub-tasks of its target type.</summary>
    private Dictionary<long, int> FactionTiers() => _factionTiers ??= FactionPoints()
        .Where(kv => Defs().TryGetValue(kv.Key, out var d) && d.Targets.Length > 0)
        .GroupBy(kv => Defs()[kv.Key].Targets[0][0])
        .SelectMany(g => g.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key).Select((kv, i) => (kv.Key, Tier: i + 1)))
        .ToDictionary(p => p.Key, p => p.Tier);

    /// <summary>The camp's tier: Peace 1, Free 2, Guild 3; the battle camps AtkFree / AtkGuild as Free / Guild.</summary>
    private int FactionTier(GameSession s) => (FactionCamp?.Invoke(s) ?? (int)EPvpRoleCampNum.Peace) switch
    {
        (int)EPvpRoleCampNum.Free or (int)EPvpRoleCampNum.AtkFree => 2,
        (int)EPvpRoleCampNum.Guild or (int)EPvpRoleCampNum.AtkGuild => 3,
        _ => 1,
    };

    // ---- acceptance ----

    /// <summary>
    /// The day's faction tasks: a new game day starts over; with function 7202 open, 1101000 once a day and, below the
    /// day's cap, the camp tier's sub-tasks (a delivered one again). Returns how many tasks were accepted or dropped.
    /// </summary>
    private int AcceptFactionTasks(GameSession session)
    {
        if (!Defs().ContainsKey(FactionQuest)) return 0;
        int moved = StartFactionDay(session);
        if (!_functions.IsOpen(session, FactionFunction)) return moved;
        TakeFactionDay(session);
        if (!Active(session, FactionQuest) && !IsDelivered(session, FactionQuest) && Ready(session, Defs()[FactionQuest]))
        {
            Accept(session, FactionQuest);
            moved++;
        }
        bool open = session.TaskRuntime.FactionPoints < FactionParam("TerritoryWarzoneTaskPointsLimit", 3000);
        int tier = FactionTier(session);
        foreach (var (id, subTier) in FactionTiers())
        {
            if (!open || subTier != tier)
            {
                if (session.Tasks!.Infos.Remove(id)) moved++;
                continue;
            }
            if (Active(session, id)) continue;
            Accept(session, id);
            moved++;
        }
        return moved;
    }

    /// <summary>
    /// A new game day (DailyActivityService's epoch) drops the faction tasks and points of the day they were taken on;
    /// returns 1 + the dropped tasks when it did. A role that never had them keeps no faction day.
    /// </summary>
    private int StartFactionDay(GameSession session)
    {
        long today = session.Daily?.DailyEpoch ?? 0;
        var runtime = session.TaskRuntime;
        if (today <= 0 || runtime.FactionDay == 0 || runtime.FactionDay == today) return 0;
        int dropped = 1;
        foreach (long id in FactionPoints().Keys.Append(FactionQuest).Append(FactionClaim))
        {
            if (session.Tasks!.Infos.Remove(id)) dropped++;
            session.Tasks.CompleteTaskNum.Remove(id);
        }
        runtime.FactionDay = 0;
        runtime.FactionPoints = 0;
        return dropped;
    }

    /// <summary>The faction tasks and points from now on belong to the current game day.</summary>
    private static void TakeFactionDay(GameSession session)
    {
        if (session.TaskRuntime.FactionDay == 0) session.TaskRuntime.FactionDay = session.Daily?.DailyEpoch ?? 0;
    }
}
