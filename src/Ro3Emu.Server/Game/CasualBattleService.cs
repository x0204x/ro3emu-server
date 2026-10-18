using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Mock Battle, the "40v40" Leisure Forty (internally CasualBattle; DailyActivityConfig 3304, function 5903, dungeon
/// 2220104, gameplay 28 LeisureForty). The client:
/// <list type="bullet">
/// <item>signs up with 43504 CommonDailyActivityReq{activityId 3304, casualBattleSignUp} from
/// UI_Sys_ActivityEveningLeisureFortyPanelCtrl._startMatch (the party leader, every member online, base level 50
/// (_iiLv), only in TimeLimitConfig 1084's "Matching" state: Sundays 16:00-16:05, 16:20-16:25, 16:40-16:45, each followed
/// by its 10-minute "Battling");</item>
/// <item>plays the arena script Config/Dungeon/DungeonLua_2220104.lua (not exported, values here): stages 0 preparation
/// 60 s, 1 main 540 s, 2 death fight 60 s (BattleMaxTime 660000), AfterResultExitTime 30 s, the HUD score bar's
/// System_BatteData_LeisureScore_Limit 4500 and DungeonAwardModule 1001-1004;</item>
/// <item>shows the camps' score from dungeon var 304 CCampScoreList [camp, score, camp, score] and the preparation
/// countdown from var 921 CLeasureFortyPrepareTimestamp (UI_Sys_Dungeon_TopActivityEveningLeisureFortyWidgetCtrl);</item>
/// <item>reads 30117 for the live report and 30132 DungeonResultNotify for the result (LC_DAL_LeisureForty: result 1 Win
/// else Lose, winCamp, sceneStatistics[0] roles with campId and extraData TotalScore 1021005 / KillPlayer 1001004 /
/// AssistKill 1001005 / Damage 1001001 / TakeDamage 1001002 / heal 1001003, campStatistics CampScore 1041003,
/// metaData mvpPlayerId);</item>
/// <item>keeps its profile in 5070 DBCasualBattleInfo (MMR from RelaxGVGStartMMR 1000, +RelaxGVGWinMMR 5 /
/// RelaxGVGLoseMMR -3, recent records, best and lifetime numbers).</item>
/// </list>
/// Scoring: BattleEventPointConfig game type 28, a kill 41006 [4,20] for the camp and 41003 [2,20] for the killer, an
/// assist 41019 [2,6]. (inferred) The groups that signed up in a Matching window are split at its Battling start into
/// two camps (whole groups, each to the smaller camp, at most 40 a camp; the others wait for the next window); the
/// match ends at BattleMaxTime or when a camp reaches the 4500 bar; the higher camp score wins, a tie has no winner; a
/// personal score of at least 25 (DungeonAwardModule ScoreCondition [2,3,25]) earns DropPool 85201, the winners also
/// 85202 (the losers' 85203 is not in DropPoolConfig); assists are the hits on the victim in its last 10 s.
/// Not built: the arena's monsters and boss (their spawn script is not in the client), the 40-player virtual team
/// (30441 with a team id, 30442-30451), the history details of 30128 and the pre-start notices 5711/5712.
/// </summary>
public sealed class CasualBattleService : IGameTicker
{
    public const uint Sync = 5070, ResultNotify = 30132;
    public const int ActivityId = 3304, Function = 5903, Scene = 2220104, TimeLimit = 1084;
    private const long PrepareSeconds = 60, MainSeconds = 540, BattleSeconds = 660, ExitSeconds = 30;
    private const long ScoreBar = 4500;
    private const int KillCamp = 41006, KillPersonal = 41003, AssistPersonal = 41019;
    private const int CampScoreVar = 304, PrepareVar = 921;
    // Scene 2220104's two born points (scene editor m_kAllPoints 23 and 34), one a camp (inferred: camp 1 at 23).
    private static readonly long[] CampPoints = [23, 34];

    private readonly GameData data;
    private readonly ActivityClock activityClock;
    private readonly TimeProvider time;
    private readonly FunctionService functions;
    private readonly TeamService teams;
    private readonly SessionRegistry sessions;
    private readonly InstanceService instances;
    private readonly SceneService scene;
    private readonly WorldData worldData;
    private readonly CharacterService character;
    private readonly RewardService rewards;
    private readonly TimedBattleService timedBattles;
    private readonly PraiseService praise;
    private readonly GameLoop loop;
    private readonly RoleStore roles;
    private readonly AccountStore accounts;
    private readonly ILogger<CasualBattleService> log;

    private sealed record Group(GameSession[] Members, long Window);

    private sealed class Player
    {
        public required GameSession Session;
        public required long Id;
        public required string Name;
        public required int Career;
        public required int Camp;
        public long Score, Kills, Assists, Damage, Taken, Healing, Deaths;
        public bool Left;
        /// <summary>Who hit this player when, for assists.</summary>
        public readonly Dictionary<GameSession, long> HitBy = new();
    }

    private sealed class Match
    {
        public required InstanceService.Room Room;
        public required long Open;
        public readonly Dictionary<GameSession, Player> Players = new();
        public readonly Dictionary<int, long> CampScore = new() { [1] = 0, [2] = 0 };
        public readonly HashSet<GameSession> Loaded = new();
        public int Stage;
        /// <summary>The battle is over and its result frozen in memory (<see cref="Frozen"/>); no more fighting.</summary>
        public bool Ending;
        /// <summary>Every stayed player's obligation is durable; payouts may now run.</summary>
        public bool Settled;
        public bool Announced, Persisting;
        public int Failures;
        public long RetryAt;
        public CasualBattleObligationRow[]? Frozen;
        public readonly Dictionary<long, DungeonResultNotify> Notices = new();
        public long Deadline => Open + BattleSeconds;
    }

    private readonly List<Group> queue = new();
    private readonly Dictionary<GameSession, Match> active = new();
    private readonly List<Match> matches = new();
    private long lastSecond = -1, formedWindow = -1;

    /// <summary>TEST AID set by GM "!casual open" (not original behaviour): sign-ups outside the Matching window, and
    /// "!casual start" forms the match at once. In memory only.</summary>
    internal bool OpenForTest { get; set; }

    public CasualBattleService(GameData data, ActivityClock activityClock, TimeProvider time, FunctionService functions,
        TeamService teams, SessionRegistry sessions, InstanceService instances, SceneService scene, WorldData worldData,
        CharacterService character, RewardService rewards, TimedBattleService timedBattles, PraiseService praise,
        CombatService combat, DungeonService dungeons, GameLoop loop, GmCommands gm, RoleStore roles, AccountStore accounts,
        ILogger<CasualBattleService> log)
    {
        this.data = data; this.activityClock = activityClock; this.time = time; this.functions = functions; this.teams = teams;
        this.sessions = sessions; this.instances = instances; this.scene = scene; this.worldData = worldData;
        this.character = character; this.rewards = rewards; this.timedBattles = timedBattles; this.praise = praise;
        this.loop = loop; this.roles = roles; this.accounts = accounts; this.log = log;
        obligations = new RoleObligations<CasualBattleObligationRow>("Mock Battle payout", accounts, loop, time, Online,
            s => s.TimedBattle != null && s.Progress != null, PayAsync, ObligationRetry.TickWithBackoff, log);
        scene.Loaded.Add(LoadedAsync);
        scene.Leaving.Add(LeavingAsync);
        combat.PvpDealt.Add(Dealt);
        combat.Healed.Add((s, n) => { if (Live(s) is { } m) m.Players[s].Healing += n; });
        dungeons.CurrentStatisticsOwner = s => Live(s) is { } m ? Statistics(m) : null;
        gm.Register("casual", "open|close|start (Mock Battle test aid: sign-ups outside the window, start the battle now)", GmAsync);
    }

    private long Now => time.GetUtcNow().ToUnixTimeSeconds();
    private long Param(string key, long fallback) => data.Table("PublicParamConfig").TryGetValue(key, out var v)
        ? (long)(GameData.Number(v) ?? fallback) : fallback;

    private Match? Live(GameSession s) => active.TryGetValue(s, out var m) && !m.Ending && s.InstanceId == m.Room.Id ? m : null;

    /// <summary>The TimeLimitConfig 1084 window of a state ("Matching" / "Battling") open now, or null.</summary>
    private ActivityClock.Window? Window(string state) => activityClock.Segments(TimeLimit, Now)
        .FirstOrDefault(w => w.State == state && w.Contains(Now));

    private bool Queued(GameSession s) => queue.Any(g => g.Members.Contains(s));

    private int Admission(GameSession s)
    {
        if (s.Role == null || s.Progress == null || s.TimedBattle == null) return ErrorCodes.Parameter;
        if (s.Progress.Level < GameData.Int(data.Row("DailyActivityConfig", ActivityId)!.Value, "_iiLv") || !functions.IsOpen(s, Function))
            return ErrorCodes.FeatureUnavailable;
        if (s.SceneLoading || s.InstanceId != 0 || s.Dead || active.ContainsKey(s) || Queued(s)) return ErrorCodes.DungeonBusy;
        return 0;
    }

    /// <summary>43504 casualBattleSignUp: the leader signs up the whole party (or a role alone) for this window.</summary>
    public async Task SignUpAsync(GameSession s, CommonDailyActivityReq req)
    {
        var window = Window("Matching");
        int error = req.ActivityId != ActivityId ? ErrorCodes.Parameter
            : window == null && !OpenForTest ? ErrorCodes.ActivityPeriod : Admission(s);
        var members = new List<GameSession> { s };
        if (error == 0 && s.Role != null && teams.TeamOf(s.Role.Id) is { TeamId: > 0 } team)
        {
            if (team.Leader != s.Role.Id) error = ErrorCodes.NoPermission;
            var online = sessions.All().Where(p => p.Role != null).ToDictionary(p => p.Role!.Id);
            foreach (long id in teams.MembersOf(s.Role.Id).Where(id => id != s.Role.Id))
            {
                if (error != 0) break;
                if (!online.TryGetValue(id, out var member)) error = ErrorCodes.PlayerOffline;
                else { error = Admission(member); members.Add(member); }
            }
        }
        if (error == 0) queue.Add(new Group(members.ToArray(), window?.Start ?? -1));
        await s.SendGameAsync(DailyActivityService.RingAccept, new CommonDailyActivityRes
        { Result = error, ActivityId = req.ActivityId, RequestId = req.RequestId, CasualBattleSignUp = new CasualBattleRes() });
    }

    public void Tick(GameTime _)
    {
        long now = Now;
        if (now == lastSecond) return;
        lastSecond = now;
        if (queue.Count > 0 && Window("Battling") is { } battling && formedWindow != battling.Start)
        {
            formedWindow = battling.Start;
            loop.PostLegacy(FormAsync, "casual battle start");
        }
        else if (queue.Count > 0 && !OpenForTest && Window("Matching") == null && Window("Battling") == null) queue.Clear();
        foreach (var match in matches.ToArray())
            loop.PostLegacy(() => AdvanceAsync(match), "casual battle timeline");
        obligations.Tick();
    }

    /// <summary>The queued groups become two camps (whole groups to the smaller camp, at most 40 each) and enter.</summary>
    private async Task FormAsync()
    {
        queue.RemoveAll(g => g.Members.Any(m => !sessions.All().Contains(m) || m.InstanceId != 0 || m.Dead || m.SceneLoading));
        var camps = new Dictionary<int, List<GameSession>> { [1] = [], [2] = [] };
        foreach (var group in queue.OrderByDescending(g => g.Members.Length).ToArray())
        {
            int camp = camps[1].Count <= camps[2].Count ? 1 : 2;
            if (camps[camp].Count + group.Members.Length > 40) continue;
            camps[camp].AddRange(group.Members);
            queue.Remove(group);
        }
        if (camps[1].Count == 0 || camps[2].Count == 0)
        {
            // One camp only: nobody to fight; the groups wait for the next window (inferred).
            foreach (var camp in camps.Values.Where(c => c.Count > 0)) queue.Add(new Group(camp.ToArray(), -1));
            return;
        }
        var everyone = camps[1].Concat(camps[2]).ToArray();
        var room = instances.Create(Scene, everyone);
        var match = new Match { Room = room, Open = Now };
        foreach (var (camp, members) in camps)
            foreach (var member in members)
            {
                match.Players[member] = new Player { Session = member, Id = member.Role!.Id, Name = member.Role.Name,
                    Career = member.Progress!.Career, Camp = camp };
                active[member] = match;
                member.EventFaction = camp;
            }
        matches.Add(match);
        praise.Remember(room.Id, Scene, everyone.Select(m => m.Role!.Id)); // likes on the report
        foreach (var member in everyone)
        {
            bool entered = false;
            try { entered = await instances.EnterAsync(room, member, 5); }
            catch (Exception e) { log.LogWarning(e, "Mock Battle entry failed for {Role}", member.Role?.Id); }
            if (!entered) { active.Remove(member); member.EventFaction = 0; match.Players[member].Left = true; }
        }
    }

    private DungeonBaseInfoNotify Info(Match match)
    {
        long prepareEnd = match.Open + PrepareSeconds, mainEnd = prepareEnd + MainSeconds;
        var (start, end, nextEnd) = match.Stage switch
        {
            0 => (match.Open, prepareEnd, mainEnd),
            1 => (prepareEnd, mainEnd, match.Deadline),
            _ => (mainEnd, match.Deadline, 0L),
        };
        var info = new DungeonBaseInfoNotify
        {
            DungeonEntry = Scene, DungeonId = match.Room.Id, Stage = match.Stage, ChangeType = (DungeonBaseInfoNotify.Types.eChangeType)2,
            StageModuleData = new DungeonStageModuleData { MapState = match.Ending ? 2 : 1, Stage = match.Stage,
                MapStateStartTime = match.Open * 1000, MapStateEndTime = match.Deadline * 1000,
                StageStartTime = start * 1000, StageEndTime = end * 1000,
                NextStageStartTime = nextEnd == 0 ? 0 : end * 1000, NextStageEndTime = nextEnd * 1000 },
        };
        var camps = new DungeonVar { VarId = CampScoreVar };
        camps.Vi64.Add([1, match.CampScore[1], 2, match.CampScore[2]]);
        info.Vars.Add(camps);
        var prepare = new DungeonVar { VarId = PrepareVar };
        prepare.Vi64.Add(prepareEnd * 1000); // a countdown timestamp in ms (DungeonCountDownTimeStamp)
        info.Vars.Add(prepare);
        return info;
    }

    private Task BroadcastAsync(Match match)
    {
        var info = Info(match);
        return RoleFanout.EachAsync(match.Players.Values.Where(p => !p.Left && match.Loaded.Contains(p.Session)
            && p.Session.InstanceId == match.Room.Id).Select(p => p.Session), s => s.SendGameAsync(DungeonService.BaseInfo, info));
    }

    private async Task LoadedAsync(GameSession s)
    {
        if (!active.TryGetValue(s, out var match) || match.Room.Id != s.InstanceId || match.Ending) return;
        match.Loaded.Add(s);
        await s.SendGameAsync(DungeonService.BaseInfo, Info(match));
        if (worldData.Map(Scene)?.Points.TryGetValue(CampPoints[match.Players[s].Camp - 1], out var start) == true)
            await scene.TeleportInMapAsync(s, start);
    }

    private Task LeavingAsync(GameSession s)
    {
        // Leaving the public map on the way in is not leaving the match.
        if (!active.TryGetValue(s, out var match) || match.Ending || s.InstanceId != match.Room.Id) return Task.CompletedTask;
        active.Remove(s);
        match.Players[s].Left = true;
        s.EventFaction = 0;
        // A camp with nobody left loses (inferred).
        if (match.Stage > 0 && match.Players.Values.Where(p => !p.Left).Select(p => p.Camp).Distinct().Count() < 2)
            RequestSettle(match);
        return Task.CompletedTask;
    }

    public bool CanFight(GameSession attacker, GameSession target) => Live(attacker) is { } m && Live(target) == m && m.Stage > 0
        && m.Loaded.Contains(attacker) && m.Loaded.Contains(target)
        && m.Players[attacker].Camp != m.Players[target].Camp && !attacker.Dead && Now < m.Deadline;

    private void Dealt(GameSession attacker, GameSession target, long damage, bool lethal)
    {
        if (Live(attacker) is not { } m || Live(target) != m || damage <= 0) return;
        m.Players[attacker].Damage += damage;
        m.Players[target].Taken += damage;
        m.Players[target].HitBy[attacker] = Now;
    }

    /// <summary>CombatService's confirmed kill of one Mock Battle player by another.</summary>
    public async Task PlayerKilledAsync(GameSession killer, GameSession victim)
    {
        if (Live(killer) is not { } m || !m.Players.TryGetValue(victim, out var dead) || m.Players[killer].Camp == dead.Camp) return;
        var me = m.Players[killer];
        me.Kills++;
        dead.Deaths++;
        Score(m, killer, KillCamp);
        Score(m, killer, KillPersonal);
        foreach (var (helper, at) in dead.HitBy)
            if (helper != killer && Now - at <= 10 && m.Players.TryGetValue(helper, out var assist) && assist.Camp == me.Camp)
            { assist.Assists++; Score(m, helper, AssistPersonal); }
        dead.HitBy.Clear();
        await BroadcastAsync(m);
        if (m.CampScore.Values.Any(score => score >= ScoreBar)) RequestSettle(m);
    }

    private void Score(Match m, GameSession player, int row)
    {
        if (data.Row("BattleEventPointConfig", row) is not { } config || !config.TryGetProperty("_kScore", out var pairs)
            || pairs.ValueKind != JsonValueKind.Array) return;
        var values = pairs.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
        for (int i = 0; i + 1 < values.Length; i += 2)
            if (values[i] == 4) m.CampScore[m.Players[player].Camp] += values[i + 1];
            else if (values[i] == 2) m.Players[player].Score += values[i + 1];
    }

    private async Task AdvanceAsync(Match match)
    {
        if (match.Settled) return;
        // Over, but its result is not durable yet: the save is retried with backoff.
        if (match.Ending) { if (Now >= match.RetryAt) await SettleAsync(match); return; }
        long elapsed = Now - match.Open;
        int stage = elapsed < PrepareSeconds ? 0 : elapsed < PrepareSeconds + MainSeconds ? 1 : 2;
        if (Now >= match.Deadline) { await SettleAsync(match); return; }
        if (match.Loaded.Count == 0 && elapsed >= 60) { await SettleAsync(match); return; } // nobody arrived
        if (stage != match.Stage) { match.Stage = stage; await BroadcastAsync(match); }
    }

    private DungeonStatisticsInfoWithDungeon Statistics(Match m)
    {
        var scene = new SceneStatisticsInfo { SceneId = 0, DungeonId = Scene };
        foreach (var p in m.Players.Values)
        {
            var role = new RoleStatisticsInfo { BaseInfo = new StatisticsRoleBaseInfo { Id = p.Id, Name = p.Name, Job = p.Career,
                CampId = p.Camp, TeamId = p.Camp }, Totalscore = p.Score, TotalDamage = p.Damage, TotalBeDamage = p.Taken, TotalCure = p.Healing };
            foreach (var (key, value) in new (int, long)[] { (1021005, p.Score), (1001004, p.Kills), (1001005, p.Assists),
                (1001001, p.Damage), (1001002, p.Taken), (1001003, p.Healing), (1001006, p.Deaths) })
                role.ExtraData[key] = new ExtraValueData { LongValue = value };
            scene.RoleStatistics[p.Id] = role;
        }
        foreach (var (camp, score) in m.CampScore)
            scene.CampStatistics[camp] = new CampStatisticsInfo { CampId = camp, ExtraData = { [1041003] = new ExtraValueData { LongValue = score } } };
        // LC_DAL_LeisureForty reads the personal strip from sceneStatistics[0].
        var result = new DungeonStatisticsInfoWithDungeon { StatisticsId = m.Room.Id, DungeonId = Scene, StatisticsTime = Now * 1000 };
        result.SceneStatistics[0] = scene;
        return result;
    }

    /// <summary>
    /// One role's frozen Mock Battle result (a casual_battle_obligation row): what its payment applies to the role's
    /// timed_battle state and which DropPools it rolls. No goods until the event/casual-battle receipt pays them.
    /// </summary>
    public sealed class Payout
    {
        public long MatchId { get; set; }
        public long RoleId { get; set; }
        public long BattleTime { get; set; }
        public string ResultId { get; set; } = "";
        public bool Won { get; set; }
        public int Outcome { get; set; }
        public long StartMmr { get; set; }
        public long MmrDelta { get; set; }
        public TimedBattleService.CasualStats Stats { get; set; } = new();
        public List<long> Pools { get; set; } = new();
    }

    /// <summary>The frozen payouts: saved before the match counts as settled, paid once each, retried by the tick
    /// while the role is online and paid at login after a disconnect or a restart.</summary>
    private readonly RoleObligations<CasualBattleObligationRow> obligations;

    internal static string SourceKey(long match, long role) =>
        string.Create(CultureInfo.InvariantCulture, $"casual-battle/{match}/{role}");
    private static long Backoff(int failures) => RoleObligations<CasualBattleObligationRow>.Backoff(failures);

    /// <summary>The battle is over: freeze its result in memory (once) and stop all fighting and scoring.</summary>
    private void End(Match match)
    {
        if (match.Ending) return;
        match.Ending = true;
        match.Stage = 2;
        var stayed = match.Players.Values.Where(p => !p.Left).Select(p => p.Camp).Distinct().ToArray();
        int winCamp = stayed.Length == 1 ? stayed[0] : match.CampScore[1] == match.CampScore[2] ? 0
            : match.CampScore[1] > match.CampScore[2] ? 1 : 2;
        var stats = Statistics(match);
        var mvp = match.Players.Values.Where(p => p.Camp == winCamp).OrderByDescending(p => p.Score)
            .ThenByDescending(p => p.Kills).ThenBy(p => p.Id).FirstOrDefault();
        string resultId = match.Room.Id.ToString(CultureInfo.InvariantCulture);
        long start = Param("RelaxGVGStartMMR", 1000), win = Param("RelaxGVGWinMMR", 5), lose = Param("RelaxGVGLoseMMR", -3);
        var rows = new List<CasualBattleObligationRow>();
        foreach (var p in match.Players.Values.Where(p => !p.Left).OrderBy(p => p.Id))
        {
            active.Remove(p.Session);
            p.Session.EventFaction = 0;
            bool won = winCamp != 0 && p.Camp == winCamp;
            var payout = new Payout
            {
                MatchId = match.Room.Id, RoleId = p.Id, BattleTime = match.Open, ResultId = resultId, Won = won,
                Outcome = winCamp == 0 ? 3 : won ? 1 : 2, // 3 draw (inferred: the achievements count win/lose/draw)
                StartMmr = start, MmrDelta = winCamp == 0 ? 0 : won ? win : lose,
                Stats = new TimedBattleService.CasualStats { PersonalScore = p.Score, Damage = p.Damage, Kills = p.Kills,
                    Assists = p.Assists, DamageTaken = p.Taken, Healing = p.Healing },
                // DungeonAwardModule ScoreCondition [2,3,25] (inferred: personal score >= 25): 85201, the winners also 85202.
                Pools = p.Score < 25 ? [] : won ? [85201, 85202] : [85201],
            };
            rows.Add(new CasualBattleObligationRow(SourceKey(match.Room.Id, p.Id), match.Room.Id, p.Id,
                JsonSerializer.SerializeToUtf8Bytes(payout)));
            var info = new DungeonResultInfo { Result = won ? 1 : 2, WinCamp = winCamp, DungeonId = Scene,
                StatisticsId = match.Room.Id, StatisticsTime = Now * 1000, DungeonStartTimestamp = match.Open * 1000 };
            info.SceneStatistics.Add(stats.SceneStatistics);
            if (mvp != null) info.MetaData["mvpPlayerId"] = mvp.Id.ToString(CultureInfo.InvariantCulture);
            match.Notices[p.Id] = new DungeonResultNotify { ResultInfo = info, ResultId = resultId };
        }
        match.Frozen = rows.ToArray();
    }

    /// <summary>A kill or a departure ended the battle: stop it now, persist on its own realm-scoped turn.</summary>
    private void RequestSettle(Match match)
    {
        End(match);
        loop.PostLegacy(() => SettleAsync(match), "casual battle settlement");
    }

    /// <summary>
    /// The end: every stayed player's payout obligation is saved in one transaction before the match counts as
    /// settled (a refused save is retried by the timeline with backoff), then each is paid on its own, 30132 is sent
    /// (best-effort) and everyone leaves after 30 s. A payout that fails stays pending: the tick retries it while the
    /// role is online, and the next login pays it after a disconnect or a restart.
    /// </summary>
    private async Task SettleAsync(Match match)
    {
        End(match);
        if (match.Settled || match.Persisting) return;
        if (!match.Announced)
        {
            match.Announced = true;
            await BroadcastAsync(match);
            loop.ScheduleLegacy(TimeSpan.FromSeconds(ExitSeconds), async () =>
            {
                foreach (var p in match.Players.Values)
                    if (p.Session.InstanceId == match.Room.Id) await instances.LeaveAsync(p.Session);
            });
        }
        match.Persisting = true;
        try
        {
            if (!await obligations.FreezeAsync(match.Frozen!, "casual-battle/freeze", match.Room.Id))
            {
                match.Failures++;
                match.RetryAt = Now + Backoff(match.Failures);
                return;
            }
        }
        finally { match.Persisting = false; }
        match.Settled = true;
        matches.Remove(match);
        // Pending (not yet due for the tick) until the first attempt below sets its retry.
        foreach (var row in match.Frozen!) obligations.Track(row);
        foreach (var row in match.Frozen!)
        {
            var player = match.Players.Values.First(p => p.Id == row.RoleId);
            var s = player.Session.Role?.Id == row.RoleId ? player.Session : Online(row.RoleId);
            if (s?.TimedBattle == null || s.Progress == null) { obligations.Forget(row.SourceKey); continue; } // paid at login
            await obligations.TryPayAsync(s, row);
            if (s.Role?.Id == row.RoleId && match.Notices.TryGetValue(row.RoleId, out var notice))
                try { await s.SendGameAsync(ResultNotify, notice); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
    }

    private GameSession? Online(long roleId) => sessions.All().FirstOrDefault(s => s.Role?.Id == roleId);

    /// <summary>
    /// One player's rating, record, best/lifetime numbers and DungeonAwardModule reward in one receipt
    /// (event/casual-battle, source casual-battle/{match}/{role}), with the role's timed_battle state and the
    /// obligation's delivered mark; then 5070. CasualSettled makes a second payment of the match a no-op.
    /// </summary>
    private async Task<bool> PayAsync(GameSession s, CasualBattleObligationRow row)
    {
        var payout = JsonSerializer.Deserialize<Payout>(row.Payout)
            ?? throw new InvalidDataException("Invalid Mock Battle payout " + row.SourceKey);
        bool paid = false;
        using (await CharacterService.Lock(s, [RoleObligations<CasualBattleObligationRow>.Key(row.SourceKey)]))
        {
            if (s.Role?.Id != row.RoleId || s.TimedBattle == null || s.Progress == null)
                throw new OperationCanceledException("Mock Battle recipient left the game.");
            var state = s.TimedBattle;
            if (!state.CasualSettled.Contains(row.MatchId))
            {
                var next = JsonSerializer.Deserialize<TimedBattleService.State>(JsonSerializer.SerializeToUtf8Bytes(state))!;
                next.CasualSettled.Add(row.MatchId);
                next.CasualMmr = (int)Math.Max(0, (next.CasualMmr == 0 ? payout.StartMmr : next.CasualMmr) + payout.MmrDelta);
                next.CasualRecords.Insert(0, new TimedBattleService.CasualRecord
                { ResultId = payout.ResultId, BattleTime = payout.BattleTime, Outcome = payout.Outcome });
                if (next.CasualRecords.Count > 20) next.CasualRecords.RemoveRange(20, next.CasualRecords.Count - 20); // (inferred) twenty kept
                var b = next.CasualBest; var t = next.CasualTotal; var p = payout.Stats;
                b.PersonalScore = Math.Max(b.PersonalScore, p.PersonalScore); b.Damage = Math.Max(b.Damage, p.Damage);
                b.Kills = Math.Max(b.Kills, p.Kills); b.Assists = Math.Max(b.Assists, p.Assists);
                b.DamageTaken = Math.Max(b.DamageTaken, p.DamageTaken); b.Healing = Math.Max(b.Healing, p.Healing);
                t.PersonalScore += p.PersonalScore; t.Damage += p.Damage; t.Kills += p.Kills; t.Assists += p.Assists;
                t.DamageTaken += p.DamageTaken; t.Healing += p.Healing;
                var grants = new List<AwardGrant>();
                foreach (long pool in payout.Pools)
                    grants.AddRange(rewards.RollPoolBound(pool, s.Progress).Select(g => new AwardGrant(g.Entry, g.Count, g.Bind)));
                var bytes = JsonSerializer.SerializeToUtf8Bytes(next);
                if (!await character.ApplyAwardAsync(s, new AwardPlan(0, 0, grants), persist: tx =>
                    {
                        tx.RoleBlob(row.RoleId, "timed_battle", bytes);
                        tx.Add(RoleObligations<CasualBattleObligationRow>.DeliverPlan(row));
                        if (grants.Any(g => g.Entry is not (100 or 101)))
                            tx.WithEventEconomy(Economy.EventEconomy.ForRole(data, s, "casual-battle", row.SourceKey, [], grants,
                                new { Match = row.MatchId, payout.Won, Score = payout.Stats.PersonalScore }));
                    }, stateLockHeld: true, committed: () => s.TimedBattle = next))
                    throw new InvalidOperationException("Mock Battle settlement refused.");
                paid = true;
            }
        }
        if (paid) await timedBattles.CasualSettledAsync(s);
        return true;
    }

    /// <summary>C/D 15: the role's pending payouts, read before the role select admits so it can reserve them.</summary>
    internal Task<ReservationKey[]> LoginKeysAsync(long roleId) => obligations.LoginKeysAsync(roleId);

    /// <summary>Login pays what a failure, a disconnect or a restart left pending (after the timed-battle state).</summary>
    public async Task LoginAsync(GameSession s)
    {
        if (s.Role == null || s.TimedBattle == null) return;
        long roleId = s.Role.Id;
        await obligations.DeliverPendingAsync(s, roleId, () => s.Role?.Id == roleId);
    }

    private async Task GmAsync(GameSession s, string[] args)
    {
        switch (args.FirstOrDefault()?.ToLowerInvariant())
        {
            case "open": OpenForTest = true; await GmCommands.ReplyAsync(s, "Mock Battle sign-ups open (test aid)."); break;
            case "close": OpenForTest = false; await GmCommands.ReplyAsync(s, "Mock Battle back on its schedule."); break;
            case "start": await FormAsync(); await GmCommands.ReplyAsync(s, $"Mock Battle started, {matches.Count} match(es) running (test aid)."); break;
            default: throw new ArgumentException("open|close|start");
        }
    }
}
