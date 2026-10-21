using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Guild Hunting Challenge: DailyActivityConfig 3303 "Hunting Challenge"
/// (level 30, function 1329, Thursday 20:30-21:00, _iTimeCtrlId 1074), GuildActivityConfig 3043. Matched guilds race in
/// their own guild home (GuildHomeService) against their own Baphomet (NPC 16320001, AI GuildBossRace). The client side
/// is lua-src Logic/Asset/LC_DAL_GuildHuntChallenge.lua (HC) and Logic/Level/LC_DunGeon_Guild.lua (LG). Phases are the
/// TimeLimitConfig 1074 rows 86-89: GveHuntCandidate (matched, "Preparation Phase"), GveHuntStart (combat), GveHuntEnd
/// (settled), GveHuntShow. The week is persisted as the original's DBGveHuntingActivityData (table guild_hunting), so a
/// restart keeps groups, scores and results, and 43516 answers the last results.
/// Not done: the GuildBossRace AI and the boss's own skills are left to the ordinary monster AI; the personal joystick
/// skills (SkillConfig 16320801-16321301, GveHunting_PersonalSkill1) have no server side here.
/// </summary>
public sealed class GuildHuntingService : IGameTicker
{
    public const uint Matched = 43510, LiveRank = 43511, SkillUsed = 43512, MatchResult = 43513,
        UseSkill = 43514, Skills = 43515, History = 43516;
    public const int Activity = 3303, TimeCtrl = 1074, Function = 1329, BossNpc = 16320001;
    /// <summary>GuildPermissionConfig 26 GuildHuntCommander (brief section 9).</summary>
    public const int CommanderRight = 26;
    /// <summary>sc_pm_unioncity_001 group 414 "狩猎挑战" (Hunting Challenge): its only monster spawn point is 413 (proven).</summary>
    private const int ArenaGroup = 414, BossPoint = 413;
    /// <summary>(inferred) Groups of four: GuildHuntRewardConfig group 1 (in-group rank) lists ranks 1-4 and mails 10200-10203.</summary>
    private const int GroupSize = 4;
    /// <summary>(inferred) The server top kept and rewarded: GuildHuntRewardConfig group 2 ends at rank 20 (row 1009).</summary>
    private const int TopSize = 20;
    /// <summary>(inferred) Commander skills reroll every 60 s; the client only shows nextSkillMs (HC:229-315).</summary>
    private const long SkillRefreshMs = 60_000;

    private readonly GameData _data;
    private readonly AccountStore _accounts;
    private readonly GuildStore _guilds;
    private readonly GuildService _guildService;
    private readonly SessionRegistry _sessions;
    private readonly FunctionService _functions;
    private readonly TimedTodoSchedule _schedule;
    private readonly TimeProvider _time;
    private readonly InstanceService _instances;
    private readonly WorldData _maps;
    private readonly BuffService _buffs;
    private readonly GameLoop _loop;
    private readonly GuildHomeService _home;
    private readonly MailService _mail;
    private readonly AwardService _awards;
    private readonly SeasonService _seasons;
    private readonly TaskService _tasks;
    private readonly ILogger<GuildHuntingService> _log;

    public GuildHuntingService(GameData data, AccountStore accounts, GuildStore guilds, GuildService guildService,
        SessionRegistry sessions, FunctionService functions, TimedTodoSchedule schedule, TimeProvider time,
        InstanceService instances, SceneService scene, WorldData maps, CombatService combat, BuffService buffs, GameLoop loop,
        GuildHomeService home, MailService mail, AwardService awards, SeasonService seasons, TaskService tasks,
        ILogger<GuildHuntingService> log)
    {
        this._data = data; this._accounts = accounts; this._guilds = guilds; this._guildService = guildService;
        this._sessions = sessions; this._functions = functions; this._schedule = schedule; this._time = time;
        this._instances = instances; this._maps = maps; this._buffs = buffs; this._loop = loop; this._home = home;
        this._mail = mail; this._awards = awards; this._seasons = seasons; this._tasks = tasks; this._log = log;
        scene.Loaded.Add(LoadedAsync);
        combat.Killed.Add(KilledAsync);
        combat.Dealt.Add(Dealt);
        guildService.HuntingBusy = Busy;
    }

    // ------------------------------------------------------------------ state

    /// <summary>This server's bookkeeping beside DBGveHuntingActivityData (column runtime, JSON).</summary>
    private sealed class Runtime
    {
        public Dictionary<long, Score> Scores { get; set; } = new();
        /// <summary>Guild: members who took part (in the home during combat, or hit their boss).</summary>
        public Dictionary<long, HashSet<long>> Participants { get; set; } = new();
        public bool Mailed { get; set; }
    }

    private sealed class Score
    {
        public long Damage { get; set; }
        /// <summary>When the guild reached its current score (ms), the tie-break (inferred from reachTs).</summary>
        public long ReachMs { get; set; }
        public bool Killed { get; set; }
        /// <summary>Commander skills the guild used, shown as commandSkillKill (inferred, see UseSkillAsync).</summary>
        public long SkillUses { get; set; }
    }

    private sealed class Week(long cycle, DBGveHuntingActivityData data, Runtime runtime)
    {
        public long Cycle { get; } = cycle;
        public DBGveHuntingActivityData Data { get; } = data;
        public Runtime Runtime { get; } = runtime;
        public bool Dirty { get; set; }
        /// <summary>The settled result was committed and then announced (43513, mails). A week loaded settled is.</summary>
        public bool Published { get; set; } = data.IsSettled;
        /// <summary>No retry of a failed save (or settlement) before this (unix s).</summary>
        public long RetryAt { get; set; }

        // One writer per week (loop only): every SaveAsync takes a ticket; a write covers every ticket taken before
        // its snapshot, so a caller learns whether the state it asked to save is committed.
        public bool Saving { get; set; }
        public long Requested { get; set; }
        public long Committed { get; set; }
        public List<(long Ticket, TaskCompletionSource<bool> Done)> Waiting { get; } = new();
    }

    /// <summary>The event's phase bounds (unix s), from TimeLimitConfig 1074 offsets on the 3303 occurrence.</summary>
    private sealed record Phases(long Cycle, long CandidateEnd, long StartEnd, long SettleEnd, long ShowEnd);

    private sealed class CommandSlots
    {
        public long[] Skills { get; } = new long[3];
        public long NextMs { get; set; }
    }

    private bool _loaded;
    private Week? _current;
    private Week? _lastSettled;
    private long _lastSecond = -1, _lastSave, _mailRetryAt;
    private bool _settling, _mailing, _broadcasting;
    /// <summary>A failed save is retried this many seconds later (the periodic save's own interval).</summary>
    private const int RetrySeconds = 5;
    private readonly Dictionary<long, (InstanceService.Room Room, MonsterUnit Boss)> _bosses = new();
    private readonly HashSet<long> _spawning = new();
    private readonly Dictionary<long, CommandSlots> _commands = new();

    private long Now => _time.GetUtcNow().ToUnixTimeSeconds();
    private long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private void Load()
    {
        _loaded = true;
        var rows = _accounts.ReadSync<List<(long Cycle, byte[] Data, byte[] Runtime)>>(new GuildHuntingReadPlan(8));
        var weeks = rows.Select(r => new Week(r.Cycle, DBGveHuntingActivityData.Parser.ParseFrom(r.Data),
            JsonSerializer.Deserialize<Runtime>(r.Runtime) ?? new Runtime())).ToArray();
        _current = weeks.FirstOrDefault();
        _lastSettled = weeks.FirstOrDefault(w => w.Data.IsSettled);
    }

    /// <summary>
    /// Saves the week as it is now (or newer); true once that is committed, false if the write failed (the week stays
    /// Dirty and the tick retries it, settled or not) or the loop refused the work at shutdown.
    /// </summary>
    private async Task<bool> SaveAsync(Week week)
    {
        try { return await _loop.TrackAsync("guild hunt save", () => SaveCoreAsync(week)); }
        catch (OperationCanceledException)
        {
            week.Dirty = true;
            return false;
        }
    }

    private async Task<bool> SaveCoreAsync(Week week)
    {
        long ticket = ++week.Requested;
        if (week.Saving)
        {
            // The running writer's next write takes the newest bytes, so an older snapshot never lands after a newer one.
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            week.Waiting.Add((ticket, done));
            return await done.Task;
        }
        week.Saving = true;
        try
        {
            while (week.Committed < week.Requested)
            {
                long covers = week.Requested;
                week.Dirty = false;
                _lastSave = Now;
                try
                {
                    await _accounts.WriteAsync<int>(new GuildHuntingSavePlan(week.Cycle, week.Data.ToByteArray(),
                        JsonSerializer.SerializeToUtf8Bytes(week.Runtime)));
                }
                catch (Exception e)
                {
                    week.Dirty = true;
                    week.RetryAt = Now + RetrySeconds;
                    _log.LogError(e, "Guild hunt week {Cycle} save failed", week.Cycle);
                    break;
                }
                week.Committed = covers;
            }
        }
        finally
        {
            week.Saving = false;
            // Tickets after the last committed write failed with it; their callers see false and the tick retries.
            foreach (var (waiting, done) in week.Waiting) done.TrySetResult(waiting <= week.Committed);
            week.Waiting.Clear();
        }
        return ticket <= week.Committed;
    }

    // ------------------------------------------------------------------ schedule

    /// <summary>
    /// The event of a 3303 occurrence (TimedTodoSchedule: _kDay [4] with the merged 1074 phases, or a GM test window).
    /// Phase bounds keep the 1074 rows' offsets from the GveHuntCandidate start (proven: 86 Candidate 20:30-20:32,
    /// 87 Start -20:37, 88 End -20:40, 89 Show -21:00).
    /// </summary>
    private Phases? PhasesAt(long now)
    {
        if (_schedule.CurrentOrNext(Activity, now) is not { } occurrence || !occurrence.Contains(now)) return null;
        var rows = _data.Table("TimeLimitConfig").Values.Where(r => GameData.Int(r, "_iTimeLimitId") == TimeCtrl)
            .ToDictionary(r => r.TryGetProperty("_strStateName", out var name) ? name.GetString() ?? "" : "", r => r);
        long Offset(string state, string side) => rows.TryGetValue(state, out var row)
            ? GameData.Int(row, "_iWeek" + side) - GameData.Int(rows["GveHuntCandidate"], "_iWeekStart") : -1;
        if (!rows.ContainsKey("GveHuntCandidate")) return null;
        long candidate = Offset("GveHuntCandidate", "End"), start = Offset("GveHuntStart", "End"),
            settle = Offset("GveHuntEnd", "End"), show = Offset("GveHuntShow", "End");
        if (candidate <= 0 || start <= candidate || settle <= start || show < settle) return null;
        long cycle = occurrence.Start;
        return new Phases(cycle, cycle + candidate, cycle + start, cycle + settle, cycle + show);
    }

    /// <summary>GM test aid only: the 3303 occurrence starts now (process-local, like !guildchallenge).</summary>
    public void TestWindow(bool open)
    {
        if (open) _schedule.TestWindows[Activity] = new(Now, Now + 1800);
        else _schedule.TestWindows.Remove(Activity);
    }

    private bool Unsettled([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Week? week)
    {
        week = _current;
        return week is { Data.IsSettled: false };
    }

    private bool InCombat(Week week) => !week.Data.IsSettled && NowMs >= week.Data.CandidateEndMs
        && NowMs < week.Data.StartPhaseEndMs;

    private GveHuntingMatchGroup? GroupOf(Week week, long guild) =>
        week.Data.GroupMap.Values.FirstOrDefault(g => g.GuildIds.Contains(guild));

    /// <summary>5840 while the guild races and the result is not settled (GuildService leave/kick).</summary>
    private bool Busy(long guild) => Unsettled(out var week) && NowMs < week.Data.StartPhaseEndMs && GroupOf(week, guild) != null;

    /// <summary>A guild member who may take part in DailyActivity 3303 (_iiLv 30, function 1329 = condition 2000116).</summary>
    private bool Eligible(GameSession s) => s.ActivitiesReady && s.Progress != null && _home.GuildId(s) != null
        && _data.Row("DailyActivityConfig", Activity) is { } daily && s.Progress.Level >= GameData.Int(daily, "_iiLv")
        && _functions.IsOpen(s, Function);

    private Score ScoreOf(Week week, long guild) =>
        week.Runtime.Scores.TryGetValue(guild, out var score) ? score : week.Runtime.Scores[guild] = new Score();

    // ------------------------------------------------------------------ tick

    public void Tick(GameTime gameTime)
    {
        long now = Now;
        if (now == _lastSecond) return;
        _lastSecond = now;
        if (!_loaded) Load();
        if (Unsettled(out var week) && NowMs >= week.Data.StartPhaseEndMs)
        {
            if (!_settling) { _settling = true; _loop.TrackRuntimeDetached("guild hunt settle", () => SettleAsync(week)); }
            return;
        }
        // Settled in memory but not yet committed: retry the save; nothing is announced or mailed before it lands.
        if (_current is { Data.IsSettled: true, Published: false } held)
        {
            if (!_settling && now >= held.RetryAt)
            {
                _settling = true;
                _loop.TrackRuntimeDetached("guild hunt settle", () => SettleAsync(held));
            }
            return;
        }
        // Finished weeks too: a failed save of the mailed flag (or of anything after the result) is retried.
        foreach (var finished in new[] { _current, _lastSettled }.Distinct())
            if (finished is { Data.IsSettled: true, Dirty: true, Saving: false } && now >= finished.RetryAt) _ = SaveAsync(finished);
        if (_current is { Data.IsSettled: true, Runtime.Mailed: false } done && !_mailing && now >= _mailRetryAt)
        {
            _mailing = true;
            _loop.TrackRuntimeDetached("guild hunt mail", () => MailAsync(done));
        }
        if ((_current == null || _current.Data.IsSettled) && PhasesAt(now) is { } phases && now < phases.StartEnd
            && _current?.Cycle != phases.Cycle)
            Begin(phases);
        if (!Unsettled(out week)) return;
        if (NowMs < week.Data.CandidateEndMs) { if (now % 5 == 0) MatchLate(week); }
        else Combat(week);
        if (week.Dirty && now - _lastSave >= 5) _ = SaveAsync(week);
    }

    /// <summary>
    /// (inferred) The candidates are the guilds with an eligible member online when the event opens, matched in groups of
    /// four by guild level (then id). The client only proves the group (ordered guildIds) and the phase ends (HC:144-227).
    /// </summary>
    private void Begin(Phases phases)
    {
        if (_current is { Data.IsSettled: true }) _lastSettled = _current;
        var week = new Week(phases.Cycle, new DBGveHuntingActivityData
        {
            CandidateEndMs = phases.CandidateEnd * 1000, StartPhaseEndMs = phases.StartEnd * 1000,
            SettlementPhaseEndMs = phases.SettleEnd * 1000,
        }, new Runtime());
        _current = week;
        _bosses.Clear(); _spawning.Clear(); _commands.Clear();
        var ordered = Candidates().Select(id => _guilds.Get(id)).OfType<GuildStore.State>()
            .OrderByDescending(g => g.Level).ThenBy(g => g.Id).Select(g => g.Id).ToArray();
        for (int i = 0; i < ordered.Length; i += GroupSize)
        {
            var group = new GveHuntingMatchGroup { GroupId = i / GroupSize + 1 };
            group.GuildIds.AddRange(ordered.Skip(i).Take(GroupSize));
            week.Data.GroupMap[group.GroupId] = group;
        }
        _log.LogInformation("Guild hunt {Cycle}: {Guilds} guilds in {Groups} groups", week.Cycle, ordered.Length,
            week.Data.GroupMap.Count);
        _ = SaveAsync(week);
        var groups = week.Data.GroupMap.Values.ToArray();
        _loop.TrackRuntimeDetached("guild hunt matched", () => NotifyMatchedAsync(week, groups));
    }

    private IEnumerable<long> Candidates() => _sessions.All().Where(Eligible).Select(s => _home.GuildId(s)!.Value).Distinct();

    /// <summary>(inferred) A guild whose first eligible member comes online during the candidate phase joins the last
    /// group with room, or a new one; the whole group gets the new list.</summary>
    private void MatchLate(Week week)
    {
        var joined = new List<GveHuntingMatchGroup>();
        foreach (long guild in Candidates().Where(g => GroupOf(week, g) == null).ToArray())
        {
            var group = week.Data.GroupMap.Values.OrderBy(g => g.GroupId).LastOrDefault();
            if (group == null || group.GuildIds.Count >= GroupSize)
            {
                group = new GveHuntingMatchGroup { GroupId = week.Data.GroupMap.Count + 1 };
                week.Data.GroupMap[group.GroupId] = group;
            }
            group.GuildIds.Add(guild);
            if (!joined.Contains(group)) joined.Add(group);
        }
        if (joined.Count == 0) return;
        week.Dirty = true;
        _loop.TrackRuntimeDetached("guild hunt matched", () => NotifyMatchedAsync(week, joined));
    }

    private void Combat(Week week)
    {
        long nowMs = NowMs;
        var sends = new List<(GameSession Session, uint Id, IMessage Message)>();
        var rolled = new List<(GameSession Session, uint Id, IMessage Message)>();
        foreach (var group in week.Data.GroupMap.Values)
        {
            foreach (long guild in group.GuildIds)
            {
                var score = ScoreOf(week, guild);
                var room = _home.RoomOf(guild);
                if (_bosses.TryGetValue(guild, out var held) && (held.Room.Closed || room?.Id != held.Room.Id
                    || !held.Room.Monsters.Contains(held.Boss)))
                {
                    _bosses.Remove(guild); // the room closed with its last member; a reopened home gets the boss back
                    held = default;
                }
                if (held.Boss is { } boss && !boss.Dead)
                {
                    // Score = damage dealt to the guild's own boss (brief: score = damage; the HUD shows it as DMG, 16320011).
                    long dealt = Math.Clamp(boss.Template.MaxHp - boss.Hp, 0, boss.Template.MaxHp);
                    if (dealt > score.Damage) { score.Damage = dealt; score.ReachMs = nowMs; week.Dirty = true; }
                }
                if (room == null) continue;
                if (held.Boss == null) EnsureBoss(week, guild, room);
                foreach (var session in Loaded(room))
                    if (_home.GuildId(session) == guild) Participate(week, guild, session);
                if (!_commands.TryGetValue(guild, out var slots) || nowMs >= slots.NextMs)
                {
                    _commands[guild] = slots = Roll(nowMs);
                    var notice = SkillsNotify(group, slots);
                    foreach (var session in Loaded(room)) rolled.Add((session, Skills, notice));
                }
            }
            var rank = new GveHuntingLiveRankNotify { Guilds = { RankOf(week, group) } };
            foreach (long guild in group.GuildIds)
                if (_home.RoomOf(guild) is { } room)
                    foreach (var session in Loaded(room)) sends.Add((session, LiveRank, rank));
        }
        if (rolled.Count > 0) _loop.TrackRuntimeDetached("guild hunt skills", async () =>
        {
            foreach (var (session, id, message) in rolled) await session.SendGameAsync(id, message);
        });
        // 43511 every second (brief: live rank every 1-2 s); one batch at a time, a busy second is skipped.
        if (sends.Count == 0 || _broadcasting) return;
        _broadcasting = true;
        _loop.TrackRuntimeDetached("guild hunt live rank", async () =>
        {
            try { foreach (var (session, id, message) in sends) await session.SendGameAsync(id, message); }
            finally { _broadcasting = false; }
        });
    }

    private IEnumerable<GameSession> Loaded(InstanceService.Room room) => room.ActiveMembers.Select(m => m.Session)
        .Where(s => s.Role != null && s.InstanceId == room.Id && s.SceneEntry == _home.Scene && !s.SceneLoading);

    /// <summary>Task target 210011 (TaskConfig 1602120) once per member and event.</summary>
    private void Participate(Week week, long guild, GameSession session)
    {
        if (!week.Runtime.Participants.TryGetValue(guild, out var roles)) week.Runtime.Participants[guild] = roles = new();
        if (!roles.Add(session.Role!.Id)) return;
        week.Dirty = true;
        _loop.PostOwned(session, () => _tasks.OnGuildHuntAsync(session), "guild hunt task");
    }

    /// <summary>The guild's boss in its live home room during combat, unless it was already felled.</summary>
    private void EnsureBoss(Week week, long guild, InstanceService.Room room)
    {
        if (_bosses.TryGetValue(guild, out var held) && (held.Room.Closed || held.Room.Id != room.Id)) _bosses.Remove(guild);
        if (_bosses.ContainsKey(guild) || ScoreOf(week, guild).Killed || !_spawning.Add(guild)) return;
        _loop.TrackLegacyDetached("guild hunt boss", () => SpawnBossAsync(week, guild, room));
    }

    private async Task SpawnBossAsync(Week week, long guild, InstanceService.Room room)
    {
        try
        {
            if (_current != week || !InCombat(week) || room.Closed || _bosses.ContainsKey(guild)
                || _maps.Map(_home.Scene)?.DungeonGroups.GetValueOrDefault(ArenaGroup)?.FirstOrDefault(p => p.Id == BossPoint)
                    is not { } point) return;
            var boss = await _instances.SpawnAsync(room, BossNpc, new MapPoint(point.X, point.Y, point.Z, point.Face));
            if (boss == null) return;
            if (_current != week || !InCombat(week))
            {
                await _instances.RemoveMonsterAsync(room, boss);
                return;
            }
            // (inferred) A boss spawned again (restart, or a home reopened mid-combat) keeps the damage already scored.
            long scored = ScoreOf(week, guild).Damage;
            if (scored > 0) boss.Hp = Math.Max(1, boss.Template.MaxHp - scored);
            _bosses[guild] = (room, boss);
        }
        finally { _spawning.Remove(guild); }
    }

    private void Dealt(GameSession session, MonsterUnit monster, long damage)
    {
        if (damage <= 0 || !Unsettled(out var week) || !InCombat(week)) return;
        foreach (var (guild, held) in _bosses)
            if (held.Boss == monster && _home.GuildId(session) == guild) { Participate(week, guild, session); return; }
    }

    private Task KilledAsync(GameSession killer, MonsterUnit monster)
    {
        if (!Unsettled(out var week)) return Task.CompletedTask;
        foreach (var (guild, held) in _bosses.ToArray())
        {
            if (held.Boss != monster) continue;
            _bosses.Remove(guild);
            var score = ScoreOf(week, guild);
            // (inferred) A felled boss is the full score; reachTs then orders the guilds by how fast they got there.
            if (!score.Killed) { score.Killed = true; score.Damage = monster.Template.MaxHp; score.ReachMs = NowMs; }
            if (_home.GuildId(killer) == guild) Participate(week, guild, killer);
            week.Dirty = true;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Ranks 1..N, contiguous: HC:55-141 keys the HUD rows by rank. Order: score down, then the earlier reachTs, then guild
    /// id (inferred tie-breaks; reachTs is never read by the client).
    /// </summary>
    private List<GveHuntingGuildResult> RankOf(Week week, IEnumerable<long> guildIds, int limit = int.MaxValue)
    {
        var ordered = guildIds.Select(g => (Guild: g, Score: ScoreOf(week, g)))
            .OrderByDescending(x => x.Score.Damage).ThenBy(x => x.Score.ReachMs == 0 ? long.MaxValue : x.Score.ReachMs)
            .ThenBy(x => x.Guild).Take(limit).ToArray();
        return ordered.Select((x, i) => new GveHuntingGuildResult
        {
            GuildId = x.Guild, Score = x.Score.Damage, ReachTs = x.Score.ReachMs, Rank = i + 1,
            CommandSkillKill = x.Score.SkillUses,
        }).ToList();
    }

    private List<GveHuntingGuildResult> RankOf(Week week, GveHuntingMatchGroup group) => RankOf(week, group.GuildIds);

    // ------------------------------------------------------------------ notifies

    private GveHuntingMatchedNotify MatchedNotify(Week week, GveHuntingMatchGroup group) => new()
    {
        GuildIds = { group.GuildIds }, CandidateEndMs = week.Data.CandidateEndMs,
        StartPhaseEndMs = week.Data.StartPhaseEndMs, SettlementPhaseEndMs = week.Data.SettlementPhaseEndMs,
    };

    /// <summary>The DAL keeps 43510 wherever the member is (HC:144-227); the home HUD reads it on entry (LG:2368-2438).</summary>
    private async Task NotifyMatchedAsync(Week week, IReadOnlyList<GveHuntingMatchGroup> groups)
    {
        var byGuild = OnlineByGuild();
        foreach (var group in groups)
        {
            var notice = MatchedNotify(week, group);
            foreach (long guild in group.GuildIds)
                foreach (var session in byGuild.GetValueOrDefault(guild) ?? [])
                    await session.SendGameAsync(Matched, notice);
        }
    }

    private Dictionary<long, List<GameSession>> OnlineByGuild()
    {
        var result = new Dictionary<long, List<GameSession>>();
        foreach (var session in _sessions.All())
            if (_home.GuildId(session) is { } guild)
                (result.TryGetValue(guild, out var list) ? list : result[guild] = new()).Add(session);
        return result;
    }

    /// <summary>A member loading into the home mid-event gets the HUD back (LG Init calls InitGuildHunt).</summary>
    private async Task LoadedAsync(GameSession session)
    {
        if (!Unsettled(out var week) || !_home.InHome(session, out long guild) || GroupOf(week, guild) is not { } group) return;
        await session.SendGameAsync(Matched, MatchedNotify(week, group));
        if (!InCombat(week)) return;
        if (_home.RoomOf(guild) is { } room) EnsureBoss(week, guild, room); // the first member opened the home mid-combat
        if (_commands.TryGetValue(guild, out var slots)) await session.SendGameAsync(Skills, SkillsNotify(group, slots));
        await session.SendGameAsync(LiveRank, new GveHuntingLiveRankNotify { Guilds = { RankOf(week, group) } });
    }

    // ------------------------------------------------------------------ settlement

    /// <summary>
    /// GveHuntEnd: the bosses leave, each group's ranks and the server top are frozen in DBGveHuntingActivityData, 43513
    /// goes to the matched guilds' online members (HC:317-410, LG:2556-2602 opens the podium), then the mails follow.
    /// The frozen result is committed before anything is announced: if the save fails the week stays unpublished and
    /// the tick retries the save (this method again, past the freeze), and a restart before then settles the saved
    /// scores afresh, so no player ever saw or was mailed a result the database does not hold.
    /// </summary>
    private async Task SettleAsync(Week week)
    {
        try
        {
            var all = week.Data.GroupMap.Values.SelectMany(g => g.GuildIds).ToArray();
            if (!week.Data.IsSettled)
            {
                foreach (var (guild, held) in _bosses.ToArray())
                {
                    if (!held.Boss.Dead)
                    {
                        var score = ScoreOf(week, guild);
                        long dealt = Math.Clamp(held.Boss.Template.MaxHp - held.Boss.Hp, 0, held.Boss.Template.MaxHp);
                        if (dealt > score.Damage) { score.Damage = dealt; score.ReachMs = Math.Min(NowMs, week.Data.StartPhaseEndMs); }
                    }
                    _bosses.Remove(guild);
                    await _instances.RemoveMonsterAsync(held.Room, held.Boss);
                }
                _commands.Clear();
                foreach (var group in week.Data.GroupMap.Values)
                {
                    var result = new GveHuntingGroupMatchResult { GuildResults = { RankOf(week, group) } };
                    foreach (long guild in group.GuildIds) week.Data.GuildMatchResults[guild] = result;
                    group.IsSettled = true;
                }
                week.Data.TopResults.Clear();
                week.Data.TopResults.AddRange(RankOf(week, all.Where(g => ScoreOf(week, g).Damage > 0), TopSize));
                week.Data.IsSettled = true;
            }
            if (!await SaveAsync(week))
            {
                week.RetryAt = Now + RetrySeconds;
                _log.LogWarning("Guild hunt {Cycle}: the result is held back until it is saved", week.Cycle);
                return;
            }
            week.Published = true;
            _lastSettled = week;
            var byGuild = OnlineByGuild();
            foreach (long guild in all)
            {
                var notice = ResultOf(week, guild);
                foreach (var session in byGuild.GetValueOrDefault(guild) ?? [])
                    await session.SendGameAsync(MatchResult, notice);
            }
            if (!_mailing)
            {
                _mailing = true;
                await MailAsync(week);
            }
        }
        finally { _settling = false; }
    }

    private static GveHuntingMatchResultNotify ResultOf(Week? week, long guild)
    {
        var notice = new GveHuntingMatchResultNotify();
        if (week == null) return notice;
        if (week.Data.GuildMatchResults.TryGetValue(guild, out var group)) notice.MatchResults.AddRange(group.GuildResults);
        notice.TopResults.AddRange(week.Data.TopResults);
        return notice;
    }

    /// <summary>
    /// Mails per participant, once (delivery keys survive a restart). In-group rank r: MailConfig 10199+r with
    /// GuildHuntRewardConfig group 1 rank r (85301-85304). Server rank t: 10204-10206 for 1-3, 10207 "top 10" for 4-10,
    /// 10210 "top 20" for 11-20, with the group 2 row of the first rank threshold at or above t (85401-85405). The mails'
    /// own _iAward for top 10/20 (85405/85406) disagrees with GuildHuntRewardConfig (85404/85405); the attachments follow
    /// the reward table the Hunt panel shows (UI_Sys_ActivityEvening_HuntPanelCtrl, inferred). (inferred) Only guilds that
    /// scored are rewarded, and only their members who took part; group 3 (85601-85604) has no consumer and is unused.
    /// </summary>
    private async Task MailAsync(Week week)
    {
        try
        {
            var top = week.Data.TopResults.ToDictionary(r => r.GuildId, r => r.Rank);
            foreach (var (guild, result) in week.Data.GuildMatchResults)
            {
                var own = result.GuildResults.FirstOrDefault(r => r.GuildId == guild);
                if (own == null || own.Score <= 0) continue;
                var letters = new List<(string Kind, int Template, IReadOnlyList<MailService.Attachment> Attachments)>();
                if (Reward(1, own.Rank) is { } groupAward && own.Rank is >= 1 and <= 4)
                    letters.Add(("group", 10199 + own.Rank, groupAward));
                if (top.TryGetValue(guild, out int rank) && Reward(2, rank) is { } topAward)
                    letters.Add(("server", rank switch { 1 => 10204, 2 => 10205, 3 => 10206, <= 10 => 10207, _ => 10210 }, topAward));
                foreach (long role in week.Runtime.Participants.GetValueOrDefault(guild) ?? [])
                    foreach (var letter in letters)
                    {
                        try
                        {
                            var owners = _loop.Owners;
                            using var scope = owners == null ? null
                                : await owners.EnterAsync("guild-hunt/reward", ReservationKey.Role(role));
                            await _mail.SendTemplateOnceAsync(role, letter.Template, letter.Attachments,
                                $"guild-hunt:{week.Cycle}:{guild}:{letter.Kind}", _ => { }, scope?.Context);
                        }
                        catch (Exception e)
                        {
                            _log.LogError(e, "Guild hunt {Cycle} {Kind} mail to role {Role} failed", week.Cycle, letter.Kind, role);
                            _mailRetryAt = Now + 60; // retried later; delivered letters are skipped by their keys
                            return;
                        }
                    }
            }
            week.Runtime.Mailed = true;
            await SaveAsync(week);
        }
        finally { _mailing = false; }
    }

    /// <summary>GuildHuntRewardConfig: the row of the group whose _iRank is the first threshold at or above rank.</summary>
    private IReadOnlyList<MailService.Attachment>? Reward(int group, int rank)
    {
        var row = _data.Table("GuildHuntRewardConfig").Values
            .Where(r => GameData.Int(r, "_iGroup") == group && GameData.Int(r, "_iRank") >= rank)
            .OrderBy(r => GameData.Int(r, "_iRank")).FirstOrDefault();
        long award = row.ValueKind == JsonValueKind.Object ? GameData.Int(row, "_iAward") : 0;
        if (award <= 0) return null;
        // Fixed AwardConfig rows (Zeny / Guild Contribution): no recipient state is read.
        return _awards.Resolve(null!, award, _seasons.PlayBook).Grants
            .Select(g => new MailService.Attachment(g.Entry, g.Count, g.BindType)).ToArray();
    }

    // ------------------------------------------------------------------ commander skills

    /// <summary>SkillConfig rows whose _kScriptLogic is GveHunting_CommandSkill* (16321401-16322201, proven names).</summary>
    private long[] CommandPool() => _commandPool ??= _data.Table("SkillConfig").Values
        .Where(r => r.TryGetProperty("_kScriptLogic", out var logic) && logic.ValueKind == JsonValueKind.String
            && logic.GetString()!.StartsWith("GveHunting_CommandSkill", StringComparison.Ordinal))
        .Select(r => GameData.Int(r, "_iID")).Order().ToArray();
    private long[]? _commandPool;

    /// <summary>(inferred) Three distinct random skills per refresh (the widget has slots 1-3).</summary>
    private CommandSlots Roll(long nowMs)
    {
        var slots = new CommandSlots { NextMs = nowMs + SkillRefreshMs };
        var pool = CommandPool().OrderBy(_ => Random.Shared.Next()).Take(3).ToArray();
        for (int i = 0; i < pool.Length; i++) slots.Skills[i] = pool[i];
        return slots;
    }

    private static GveHuntingSkillsNotify SkillsNotify(GveHuntingMatchGroup group, CommandSlots slots)
    {
        var notice = new GveHuntingSkillsNotify { GuildIds = { group.GuildIds }, NextSkillMs = slots.NextMs };
        for (int i = 0; i < slots.Skills.Length; i++)
            if (slots.Skills[i] > 0) notice.HuntingSkills.Add(new HuntingSkillInfo { SkillConfigId = slots.Skills[i], SlotID = i + 1 });
        return notice;
    }

    /// <summary>The skill's visual template selects the caster's own guild (m_kSelectSubType MySelf = 2, only 16322201's
    /// 163222 "all super armor"); every other commander skill picks another guild of the group (HuntCommandWidget:292-326).</summary>
    private bool OwnGuildSkill(JsonElement row) =>
        _data.SkillTemplate(GameData.Int(row, "_iScriptShow")) is { } template
        && template.TryGetProperty("m_kSelectInfo", out var select) && select.ValueKind == JsonValueKind.Array
        && select.EnumerateArray().Any(s => (GameData.Int(s, "m_kSelectSubType") & 2) != 0);

    /// <summary>
    /// 43514 (no response message; failures answer the framework error envelope). The commander needs right 26 and a slot
    /// holding the skill. Effects (inferred from SkillConfig): _kBuff2 [buff, chance, ms] lands on the target guild's
    /// members in their home (e.g. 16321501 "all immobilized"), _kBuff1 on the target guild's Baphomet (16321801/16321901
    /// "Baphomet defense/attack up"); skills without buffs (magnet, wave) are client script shows only here. 43512 goes to
    /// both guilds' homes (LG:2652-2733 shouts it). commandSkillKill counts the guild's skill uses (inferred: the report
    /// rates it C..SSS from 0/6/11/21/31/41, HuntReportListWidget).
    /// </summary>
    public async Task UseSkillAsync(GameSession s, UseGveHuntingSkillReq req)
    {
        int error = UseSkillError(s, req, out long guild, out var row);
        if (error != 0)
        {
            await WorldCoreService.ErrorAsync(s, UseSkill, error);
            return;
        }
        var week = _current!;
        var slots = _commands[guild];
        slots.Skills[Array.IndexOf(slots.Skills, req.SkillConfigId)] = 0;
        ScoreOf(week, guild).SkillUses++;
        week.Dirty = true;
        long source = s.Role!.Id;
        var group = GroupOf(week, guild)!;
        var targetRoom = _home.RoomOf(req.ToGuildId);
        if (targetRoom != null)
        {
            foreach (var (buff, ms) in Buffs(row, "_kBuff2"))
                foreach (var member in Loaded(targetRoom).ToArray())
                    if (_home.GuildId(member) == req.ToGuildId) await _buffs.AddAsync(member, buff, ms, source);
            if (_bosses.TryGetValue(req.ToGuildId, out var held) && !held.Boss.Dead)
                foreach (var (buff, ms) in Buffs(row, "_kBuff1")) await _buffs.AddSupportAsync(held.Boss, buff, ms, source);
        }
        var used = new UseHuntingSkillNtf { SourceGuildId = guild, TargetGuildId = req.ToGuildId, SkillConfigId = req.SkillConfigId };
        foreach (long side in new[] { guild, req.ToGuildId }.Distinct())
            if (_home.RoomOf(side) is { } room)
                foreach (var member in Loaded(room).ToArray()) await member.SendGameAsync(SkillUsed, used);
        if (_home.RoomOf(guild) is { } own)
        {
            var notice = SkillsNotify(group, slots);
            foreach (var member in Loaded(own).ToArray()) await member.SendGameAsync(Skills, notice);
        }
    }

    private int UseSkillError(GameSession s, UseGveHuntingSkillReq req, out long guild, out JsonElement row)
    {
        guild = 0;
        row = default;
        if (s.Role == null || _home.GuildId(s) is not { } own) return ErrorCodes.NotInGuild;
        guild = own;
        if (!Unsettled(out var week) || !InCombat(week) || GroupOf(week, own) is not { } group
            || !_home.InHome(s, out _)) return ErrorCodes.FeatureUnavailable;
        if (!_guildService.HasRight(own, s.Role.Id, CommanderRight)) return ErrorCodes.GuildPermission;
        if (!_commands.TryGetValue(own, out var slots) || req.SkillConfigId <= 0 || !slots.Skills.Contains(req.SkillConfigId)
            || _data.Row("SkillConfig", req.SkillConfigId) is not { } skill) return ErrorCodes.Parameter;
        row = skill;
        bool self = OwnGuildSkill(skill);
        if (!group.GuildIds.Contains(req.ToGuildId) || self != (req.ToGuildId == own)) return ErrorCodes.Parameter;
        return 0;
    }

    private static IEnumerable<(int Buff, int Ms)> Buffs(JsonElement row, string field) =>
        row.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Array && t.GetArrayLength() >= 3)
                .Select(t => ((int)(GameData.Number(t[0]) ?? 0), (int)(GameData.Number(t[2]) ?? 0)))
                .Where(t => t.Item1 > 0).ToArray()
            : [];

    // ------------------------------------------------------------------ queries

    /// <summary>43516 GveHuntingHistoryQueryReq: the last settled event's group and server top (HuntPanel Rank button,
    /// HC UpdateHuntResult keeps the panel's data). An unsettled event answers the previous week (inferred).</summary>
    public async Task HistoryAsync(GameSession s)
    {
        if (!_loaded) Load();
        await s.SendGameAsync(History, ResultOf(_lastSettled, _home.GuildId(s) ?? 0));
    }

    /// <summary>43504 branch 200 gveHuntingQueryResult: the same results in CommonDailyActivityRes (HC UpdateDailyActivityResult).
    /// The client never sends it in this build; activity ids other than 3303 are refused (inferred).</summary>
    public async Task QueryResultAsync(GameSession s, CommonDailyActivityReq req)
    {
        if (!_loaded) Load();
        var rsp = new CommonDailyActivityRes { ActivityId = req.ActivityId, RequestId = req.RequestId };
        if (req.ActivityId != Activity || s.Role == null) rsp.Result = ErrorCodes.Parameter;
        else rsp.GveHuntingQueryResult = ResultOf(_lastSettled, _home.GuildId(s) ?? 0);
        await s.SendGameAsync(DailyActivityService.RingAccept, rsp);
    }
}
