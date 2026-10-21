using Google.Protobuf;
using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Field MVP bosses. One cycle per active public line and MVPConfig row (cfg_id): BORN → (killed) REBORN with
/// state_time = death ms → BORN again after _iRefreshTime (3600 s). Line 1 phase/history and latest death across lines
/// are persisted; extra-line cycles expire with their runtime (inferred policy). The boss is an
/// ordinary monster entity (NPCConfig subtype 4) spawned at a random monster point of its map (the client data has no
/// MVP spawn point, §1.2, inferred). Messages: 43009 brief list, 43011 daily counters, 43012 battle report, 43013 death,
/// 43014 personal result, 43008 pre-respawn notice; marquees 603 / 604. 43010 MVP_CLIENT_AWARD_DROP_NTF is not sent: its
/// client handler only reads drop_id and shows nothing (LC_DAL_MVP.lua _proessGetAwardDropNtf). Rewards per kill (§4.2): First strike, Final
/// blow, Ranking (top 10 parties), Participation, one Rare winner, each gated by the shared daily counters (reset 05:00)
/// and rolled through MultiverseGroup → AwardConfig → DropPool. The score is MVPScoreConfig by rank in damage dealt /
/// taken / healing minus deaths (formula inferred, §4.3), team scores decayed by members out of reward counts. Boss
/// skills the client data settles (summons, ground fields, Frenzy, born and out-of-combat buffs): MvpService.Skills.cs.
/// </summary>
public sealed partial class MvpService : IGameTicker
{
    public const uint PreNotice = 43008, BriefInfo = 43009, AwardNum = 43011, ReportRank = 43012, DeadNotify = 43013, ResultNtf = 43014;
    private const uint SystemBroadcast = 1022;

    /// <summary>After a committed kill settlement, once per online participant (achievement 103011). Runs under the
    /// settlement's owner scope: listeners post their own work.</summary>
    public List<Action<GameSession>> ParticipantSettled { get; } = new();
    private const int RefreshMessage = 603, DieMessage = 604; // PublicParamConfig.MVPRefreshMessage / MVPDieMessage
    private const int TeamCap = 3, JoinCap = 5, FirstCap = 3, EndCap = 3; // TeamAwardNum / PartAwardNum / FirstDamageAward / LastDamage
    private const int ResetHour = 5;                                        // AwardNumRefreshTime
    private const int RankedTeams = 10;                                     // TeamAwardLeaderboardLimit
    private const long NoticeBeforeMs = 120_000;                            // RefreshUIShowTime

    private sealed record Def(int CfgId, int NpcId, long MapId, int Level, long RefreshMs, long First, long Last, long Team, long Part, long Rare, int RareChance);

    private sealed class Stat
    {
        public long Attack, BeAttack, Dead;
        public long Cure = 0; // No healing event source yet; retained in the existing score formula.
        public long TeamId, Leader;
    }

    private sealed class State
    {
        public required Def Def;
        public required RuntimeMapKey Runtime;
        public bool Retired;
        public int Pending;
        public readonly List<(TimeSpan Due, Func<Task> Work, MonsterUnit? Actor)> Deadlines = new();
        public readonly TaskCompletionSource Quiesced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MVP_STATE Phase = MVP_STATE.Born;
        public long StateTimeMs;
        public MonsterUnit? Unit;
        public bool Noticed;
        public bool Spawning;
        public bool Transitioning;
        public long FirstPlayer;
        public readonly Dictionary<long, Stat> Stats = new();
        public readonly List<long> DeadMs = new();
        public readonly Dictionary<(long Version, MVPRankType Tp), MVPReportRankInfo> Reports = new();
        public long NextCastMs;
        public bool SkillsStopped;
        public long LastFightMs;
        public bool UnFightApplied;
        public readonly Dictionary<int, long> SkillReadyMs = new();
        public readonly List<MonsterUnit> Minions = new();
        public readonly List<MvpFieldUnit> Fields = new();
    }

    private readonly GameData _data;
    private readonly AccountStore _accounts;
    private readonly MonsterService _monsters;
    private readonly WorldData _worldData;
    private readonly CharacterService _character;
    private readonly RewardService _rewards;
    private readonly TaskService _tasks;
    private readonly TeamService _teams;
    private readonly SessionRegistry _sessions;
    private readonly ILogger<MvpService> _log;
    private readonly World _world;
    private readonly GameLoop _loop;
    private readonly CombatService _combat;
    private readonly BuffService _buffs;
    private readonly Random _random = new();
    private Dictionary<int, State>? _states;
    private readonly Dictionary<RuntimeMapKey, Dictionary<int, State>> _lineStates = new();
    private readonly Dictionary<int, long> _lastDeath = new();
    private sealed class PreparedKill
    {
        public required State State;
        public required MvpObligationRow[] Obligations;
        public required string KillerName;
        public bool Finalizing;
        public bool DeathBroadcastSent;
        public bool DeliveryAttempted;
        public bool DeadNoticeSent;
    }
    private readonly Dictionary<MonsterUnit, PreparedKill> _preparedKills = new();
    private readonly Dictionary<MonsterUnit, (long RoleId, long Damage, long PreviousFirst, bool NewStat)> _unpublishedLethalHits = new();
    private sealed class UnknownKill
    {
        public required State State;
        public required string Source;
        public required GameSession Killer;
        public required Role? KillerRole;
        public required AttachmentLease? KillerAttachment;
        public required long KillerSceneGeneration;
        public required long KillerWorldScene;
        public required string KillerName;
        public required long BeforeHp;
        public bool Resolving;
    }
    private readonly Dictionary<MonsterUnit, UnknownKill> _unknownKills = new();
    internal Func<string, Task>? BeforeSettlementProbeForTest { get; set; }
    internal Func<string, Task>? AfterSettlementCommitForTest { get; set; }
    internal Func<string, Task>? BeforeRetainedPublicationForTest { get; set; }

    private TimeSpan _sinceCheck;
    private readonly RoleObligations<MvpObligationRow> _awardObligations;

    public MvpService(GameData data, AccountStore accounts, MonsterService monsters, WorldData worldData, CharacterService character,
        RewardService rewards, TaskService tasks, TeamService teams, CombatService combat, BuffService buffs, SessionRegistry sessions,
        World world, GameLoop loop, SceneService scene, PublicLineManager lines, ILogger<MvpService> log)
    {
        this._buffs = buffs;
        this._data = data;
        this._accounts = accounts;
        this._monsters = monsters;
        this._worldData = worldData;
        this._character = character;
        this._rewards = rewards;
        this._tasks = tasks;
        this._teams = teams;
        this._sessions = sessions;
        this._log = log;
        this._world = world;
        this._loop = loop;
        this._combat = combat;
        // No tick retry (as before the shared component): an undelivered award waits for the next kill's delivery
        // pass or the next login, and a refused delivery stops that pass.
        _awardObligations = new RoleObligations<MvpObligationRow>("MVP award", accounts, loop, TimeProvider.System,
            role => sessions.All().FirstOrDefault(s => s.Role?.Id == role), s => s.Progress != null, PayAsync,
            ObligationRetry.NextRecovery, log);
        combat.EventDamageRecorded.Add((s, m, damage) => { OnDealt(s, m, damage); return Task.CompletedTask; });
        combat.Taken.Add(OnTaken);
        combat.BeforeLethalPublication.Add(PrepareKilledAsync);
        combat.AfterLethalPublication.Add(FinalizePreparedAsync);
        combat.LethalHitRolledBack.Add(RollbackUnpublishedLethal);
        combat.Killed.Add(OnKilledAsync);
        combat.IncomingDamageAdjusters.Add((m, _, _, damage) => Task.FromResult(FrenzyDealt(m, damage)));
        combat.DamageAdjusters.Add((_, m, _, _, damage) => FrenzyTaken(m, damage));
        scene.PublicLineOpened.Add(ActivateLine);
        lines.HasPendingWork.Add(HasPendingWork);
        lines.Retiring.Insert(0, RetireLineAsync);
    }

    // ---- client requests ----

    /// <summary>43009: all MVPs every time (the client replaces per cfg_id); REBORN carries the death time (+3600 s client-side).</summary>
    public Task BriefAsync(GameSession s)
    {
        var rsp = new MVPBriefInfoRsp { Result = 0 };
        foreach (var original in States().Values.OrderBy(x => x.Def.CfgId))
        {
            var st = original;
            if (s.RuntimeMap.Kind == RuntimeMapKind.PublicLine && s.LineNumber > 1 && s.SceneEntry == st.Def.MapId
                && _lineStates.TryGetValue(s.RuntimeMap, out var line) && line.TryGetValue(st.Def.CfgId, out var local)) st = local;
            rsp.Infos.Add(new MVPBriefInfo { CfgId = st.Def.CfgId, State = st.Phase, StateTime = st.StateTimeMs, Level = st.Def.Level });
        }
        return s.SendGameAsync(BriefInfo, rsp);
    }

    /// <summary>43011: today's used reward counts (shared by all MVPs).</summary>
    public async Task AwardNumAsync(GameSession s)
    {
        long role = s.Role!.Id;
        using var scope = _loop.Owners is { } owners
            ? await owners.EnterAsync("mvp/daily-counter", ReservationKey.Role(role)) : null;
        var (info, reset) = await LoadCountersAsync(role);
        if (reset)
        {
            var builder = new RoleItemPlanBuilder(_accounts);
            builder.Add(new MvpWritePlan(role, CounterRow(info)));
            await builder.CommitAsync(_accounts, "mvp/daily-counter-reset");
        }
        await s.SendGameAsync(AwardNum, new MVPAwardNumRsp { Result = 0, Info = info });
    }

    /// <summary>43012: one tab of a stored battle report (version = the kill's dead_ms; 0 = latest); no info = "No Records".</summary>
    public Task ReportAsync(GameSession s, MVPReportRankReq req)
    {
        var rsp = new MVPReportRankRsp { Result = 0 };
        if (States().TryGetValue((int)req.CfgId, out var st))
        {
            if (s.RuntimeMap.Kind == RuntimeMapKind.PublicLine && s.LineNumber > 1 && s.SceneEntry == st.Def.MapId
                && _lineStates.TryGetValue(s.RuntimeMap, out var line) && line.TryGetValue(st.Def.CfgId, out var local)) st = local;
            rsp.VersionList.Add(st.DeadMs.AsEnumerable().Reverse());
            long version = req.ReportVersion != 0 ? req.ReportVersion : st.DeadMs.LastOrDefault();
            if (st.Reports.TryGetValue((version, req.Tp), out var info)) rsp.Info = info;
        }
        return s.SendGameAsync(ReportRank, rsp);
    }

    // ---- GM ----

    /// <summary>GM: list, "spawn &lt;cfg&gt;" (respawn now), "where &lt;cfg&gt;" (map and position), "weaken &lt;cfg&gt;" (1 HP, for tests).</summary>
    public async Task<string> GmAsync(string[] args)
    {
        var states = States();
        if (args.Length >= 3 && int.TryParse(args[2], out var line) && line > 1)
        {
            var candidate = states.GetValueOrDefault(int.TryParse(args[1], out var selected) ? selected : 0);
            if (candidate != null && _lineStates.TryGetValue(RuntimeMapKey.Public(candidate.Def.MapId, line), out var local)) states = local;
        }
        if (args.Length >= 2 && int.TryParse(args[1], out int cfg) && states.TryGetValue(cfg, out var st))
        {
            // A GM mutation cannot race an unpublished settlement. Spawn also declares the
            // state row that SaveStateAsync writes inside this same admitted operation.
            var cfgKey = cfg.ToString(CultureInfo.InvariantCulture);
            ReservationKey[] mutationKeys = args[0] is "spawn" or "weaken"
                ? args[0] == "spawn" && st.Runtime.LineNumber == 1
                    ? [ReservationKey.Entity("mvp_cooldown", cfgKey), ReservationKey.Entity("mvp_state", cfgKey)]
                    : [ReservationKey.Entity("mvp_cooldown", cfgKey)]
                : [];
            using var mutation = mutationKeys.Length > 0 && _loop.Owners is { } owners
                ? await owners.EnterAsync("mvp/gm", mutationKeys) : null;
            switch (args[0])
            {
                case "spawn":
                    if (st.Unit is { Dead: false }) return $"MVP {cfg} is alive on {st.Def.MapId}.";
                    await SaveStateAsync(st, MVP_STATE.Born, MonsterService.NowMs);
                    return $"MVP {cfg} {NpcName(st.Def.NpcId)} respawns on {st.Def.MapId} {_data.MapName(st.Def.MapId)}.";
                case "weaken":
                    if (st.Unit is not { Dead: false } w) return $"MVP {cfg} is not alive.";
                    if (w.Hp <= 0 || st.Phase != MVP_STATE.Born || _preparedKills.ContainsKey(w) || _unknownKills.ContainsKey(w))
                        return $"MVP {cfg} is settling its death.";
                    w.Hp = 1;
                    return $"MVP {cfg} has 1 HP.";
                case "where":
                    return st.Unit is { } u ? FormattableString.Invariant($"{st.Def.MapId} {u.X:0.0} {u.Z:0.0}") : $"MVP {cfg} is not alive ({st.Phase}).";
            }
        }
        return string.Join("; ", states.Values.OrderBy(x => x.Def.CfgId).Select(x =>
            $"{x.Def.CfgId} {NpcName(x.Def.NpcId)} @{x.Def.MapId} {x.Phase}{(x.Phase == MVP_STATE.Reborn ? $" in {(x.StateTimeMs + x.Def.RefreshMs - MonsterService.NowMs) / 60000} min" : "")}"));
    }

    // ---- life cycle ----

    /// <summary>One boss cycle for each MVP configured on this active public line (inferred).</summary>
    public void ActivateLine(RuntimeMapKey runtime)
    {
        if (runtime.Kind != RuntimeMapKind.PublicLine || runtime.Id == 1 || _lineStates.ContainsKey(runtime)) return;
        var states = new Dictionary<int, State>();
        long now = MonsterService.NowMs;
        foreach (var basis in States().Values.Where(s => s.Def.MapId == runtime.SceneEntry))
        {
            long death = Math.Max(_lastDeath.GetValueOrDefault(basis.Def.CfgId),
                basis.Phase == MVP_STATE.Reborn ? basis.StateTimeMs : 0);
            states[basis.Def.CfgId] = new State
            {
                Def = basis.Def, Runtime = runtime,
                Phase = death > 0 && now < death + basis.Def.RefreshMs ? MVP_STATE.Reborn : MVP_STATE.Born,
                StateTimeMs = death > 0 && now < death + basis.Def.RefreshMs ? death : now,
            };
        }
        _lineStates[runtime] = states;
    }

    public bool HasPendingWork(RuntimeMapKey runtime) => _lineStates.TryGetValue(runtime, out var states)
        && states.Values.Any(s => s.Pending > 0 || s.Deadlines.Count > 0);

    /// <summary>Fence MVP callbacks; MonsterService removes the owned boss/minions immediately afterwards.</summary>
    public async Task RetireLineAsync(RuntimeMapKey runtime)
    {
        if (!_lineStates.Remove(runtime, out var states)) return;
        foreach (var key in _preparedKills.Where(kv => states.Values.Contains(kv.Value.State)).Select(kv => kv.Key).ToArray())
            _preparedKills.Remove(key);
        foreach (var key in _unknownKills.Where(kv => states.Values.Contains(kv.Value.State)).Select(kv => kv.Key).ToArray())
            _unknownKills.Remove(key);
        foreach (var key in _unpublishedLethalHits.Where(kv => states.Values.Any(s => s.Unit == kv.Key)).Select(kv => kv.Key).ToArray())
            _unpublishedLethalHits.Remove(key);
        foreach (var st in states.Values) { st.Retired = true; st.Deadlines.Clear(); }
        await Task.WhenAll(states.Values.Where(s => s.Pending > 0).Select(s => s.Quiesced.Task));
        foreach (var st in states.Values)
        {
            st.Unit = null;
            st.Minions.Clear();
            foreach (var field in st.Fields.ToList()) await RemoveFieldAsync(st, field);
            st.Stats.Clear();
        }
    }

    private static void EndPending(State st)
    {
        if (--st.Pending == 0 && st.Retired) st.Quiesced.TrySetResult();
    }

    private void Schedule(State st, TimeSpan delay, Func<Task> work, MonsterUnit? actor = null)
    {
        if (!st.Retired) st.Deadlines.Add((_loop.Now + delay, work, actor));
    }

    private async Task RunDeadlineAsync(State st, Func<Task> work)
    {
        try { if (!st.Retired) await work(); }
        catch (Exception e) { _log.LogError(e, "MVP deadline failed for {Cfg} on {Runtime}", st.Def.CfgId, st.Runtime); }
        finally { EndPending(st); }
    }

    public void Tick(GameTime time)
    {
        foreach (var st in States().Values.Concat(_lineStates.Values.SelectMany(v => v.Values)).ToArray())
        {
            if (st.Retired || st.Deadlines.Count == 0) continue;
            var due = st.Deadlines.Where(d => d.Due <= time.Now).ToArray();
            st.Deadlines.RemoveAll(d => d.Due <= time.Now);
            foreach (var deadline in due)
            {
                st.Pending++;
                _loop.TrackRuntimeDetached("MVP deadline", () => RunDeadlineAsync(st, deadline.Work));
            }
        }
        _sinceCheck += time.Delta;
        if (_sinceCheck < TimeSpan.FromSeconds(1)) return;
        _sinceCheck = TimeSpan.Zero;
        long now = MonsterService.NowMs;
        foreach (var (monster, prepared) in _preparedKills.ToArray())
            if (monster.Dead && !prepared.State.Retired && !prepared.Finalizing)
                _loop.TrackRuntimeDetached("MVP finalize committed death", () => FinalizePreparedAsync(monster));
        foreach (var (monster, unknown) in _unknownKills.ToArray())
            if (!unknown.Resolving && !unknown.State.Retired)
            {
                unknown.Resolving = true;
                _loop.TrackRuntimeDetached("MVP resolve uncertain death", () => ResolveUnknownDeathAsync(monster, unknown));
            }
        foreach (var st in States().Values.Concat(_lineStates.Values.SelectMany(v => v.Values)))
        {
            if (st.Retired) continue;
            if (st.Phase == MVP_STATE.Born && st.Unit == null && !st.Spawning) _loop.TrackRuntimeDetached("MVP spawn", () => SpawnAsync(st));
            else if (st.Phase == MVP_STATE.Born && st.Unit is { Dead: false, Hp: > 0 } boss
                && !_preparedKills.ContainsKey(boss) && !_unknownKills.ContainsKey(boss)) TickBossSkills(st, boss, now);
            else if (st.Phase == MVP_STATE.Reborn)
            {
                long respawn = st.StateTimeMs + st.Def.RefreshMs;
                if (!st.Noticed && now >= respawn - NoticeBeforeMs)
                {
                    st.Noticed = true;
                    foreach (var p in _sessions.All().Where(p => p.Role != null && p.WorldScene == st.Runtime.WorldScene))
                        _loop.PostOwned(p, () => p.SendGameAsync(PreNotice, new MVPP2SNotice { CfgId = st.Def.CfgId, SceneId = st.Def.MapId }), "MVP notice");
                }
                // The event source may be durable while E04 has restored an unpublished
                // boss to 1 HP. Do not replace that actor until both barriers publish it.
                if (now >= respawn && !st.Transitioning && st.Unit is not { Dead: false })
                {
                    st.Transitioning = true;
                    _loop.TrackRuntimeDetached("MVP respawn", async () =>
                    {
                        try { await SaveStateAsync(st, MVP_STATE.Born, MonsterService.NowMs, onlyIfReborn: true); }
                        finally { st.Transitioning = false; }
                    });
                }
            }
        }
    }

    private async Task SpawnAsync(State st)
    {
        if (st.Retired || _worldData.Map(st.Def.MapId) is not { } map) return;
        st.Spawning = true;
        st.Pending++;
        try
        {
            await SpawnCoreAsync(st, map);
        }
        finally
        {
            st.Spawning = false;
            EndPending(st);
        }
    }

    private async Task SpawnCoreAsync(State st, MapWorld map)
    {
        var points = map.SpawnGroups.SelectMany(g => g.Points).ToList();
        var at = points.Count > 0 ? points[_random.Next(points.Count)] is var p ? new MapPoint(p.X, p.Y, p.Z, p.Face) : default
            : map.Born ?? default;
        st.Unit = await _monsters.SpawnSingleAsync(st.Runtime, st.Def.NpcId, at);
        if (st.Unit != null) st.Unit.SpecialsOwnedByMvp = true; // TickBossSkills paces all of its specials
        if (st.Retired) { st.Unit = null; return; }
        if (st.Unit == null) return;
        st.Stats.Clear();
        st.FirstPlayer = 0;
        st.Noticed = false;
        st.SkillReadyMs.Clear();
        st.NextCastMs = st.LastFightMs = 0;
        st.UnFightApplied = false;
        await ApplyBornBuffsAsync(st.Unit);
        _log.LogInformation("MVP {Cfg} ({Npc} {Name}) spawned on {Map} at {X:F0},{Z:F0}", st.Def.CfgId, st.Def.NpcId, NpcName(st.Def.NpcId), st.Def.MapId, st.Unit.X, st.Unit.Z);
        await BroadcastAsync(st.Runtime, RefreshMessage, ClientNpcName(st.Def.NpcId), _data.ClientMapName(st.Def.MapId));
    }

    internal void StopSummonsAfterObservationForTest(MonsterUnit boss)
    {
        if (Owner(boss) is { } st) st.SkillsStopped = true;
    }

    private void OnDealt(GameSession s, MonsterUnit m, long damage)
    {
        // A committed MVP source may await the separate ordinary-kill reward barrier.
        // A retry of that same actor must not count another event hit or reroll its outcome.
        if (_preparedKills.ContainsKey(m) || _unknownKills.ContainsKey(m)
            || Owner(m) is not { } st || s.Role is not { } role) return;
        bool newStat = !st.Stats.ContainsKey(role.Id);
        long previousFirst = st.FirstPlayer;
        var stat = StatOf(st, role.Id);
        stat.Attack += damage;
        // First strike: the first attacker; lost when the owner logs out or leaves the map (rules text 10960000072).
        if (st.FirstPlayer == 0 || _sessions.All().FirstOrDefault(p => p.Role?.Id == st.FirstPlayer) is not { } owner
            || owner.WorldScene != st.Runtime.WorldScene)
            st.FirstPlayer = role.Id;
        if (m.Hp == 0) _unpublishedLethalHits[m] = (role.Id, damage, previousFirst, newStat);
    }

    private void OnTaken(MonsterUnit m, GameSession p, long damage, bool lethal)
    {
        if (Owner(m) is not { } st || p.Role is not { } role) return;
        var stat = StatOf(st, role.Id);
        stat.BeAttack += damage;
        if (lethal) stat.Dead++;
    }

    private void RollbackUnpublishedLethal(MonsterUnit m)
    {
        if (!_unpublishedLethalHits.Remove(m, out var hit) || Owner(m) is not { } st
            || !st.Stats.TryGetValue(hit.RoleId, out var stat)) return;
        stat.Attack -= hit.Damage;
        st.FirstPlayer = hit.PreviousFirst;
        if (hit.NewStat && stat.Attack == 0 && stat.BeAttack == 0 && stat.Cure == 0 && stat.Dead == 0)
            st.Stats.Remove(hit.RoleId);
    }

    /// <summary>Durably freeze the MVP source, participants, allowances and resolved rewards before lethal packets.</summary>
    private async Task PrepareKilledAsync(GameSession killer, MonsterUnit m, long beforeHp)
    {
        // These identify the cast that earned the kill, before SettleAsync can await another role
        // or a writer. The same role id on a later attachment is a different caster.
        var killerRole = killer.Role;
        var killerAttachment = killer.Attachment;
        long killerSceneGeneration = killer.SceneGeneration, killerWorldScene = killer.WorldScene;
        if (Owner(m) is not { } st) return;
        if (_preparedKills.ContainsKey(m)) return;
        if (_unknownKills.ContainsKey(m)) throw new EventSettlementOutcomeUnknownException(
            "MVP settlement source is awaiting resolution.", new InvalidOperationException("Frozen source is unresolved."));
        long now = MonsterService.NowMs;
        string parentSource = $"mvp/kill/{st.Def.CfgId}/{st.Runtime.LineNumber}/{m.Uid}/{now}";
        string killerName = killerRole?.Name ?? "";
        st.Pending++;
        try
        {
            var obligations = await SettleAsync(st, killerRole?.Id ?? 0, now, parentSource);
            if (AfterSettlementCommitForTest is { } afterCommit) await afterCommit(parentSource);
            _preparedKills.Add(m, new PreparedKill { State = st, Obligations = obligations, KillerName = killerName });
            _unpublishedLethalHits.Remove(m);
        }
        catch (Exception failure)
        {
            // A writer/apply acknowledgement can fail after commit. The source row is in that same
            // transaction, so its presence proves that the frozen obligations must be published,
            // without running the award resolver again.
            MvpSettlementSnapshot? committed;
            try { committed = await ProbeSettlementAsync(parentSource); }
            catch (Exception probeFailure)
            {
                _unknownKills[m] = new UnknownKill
                {
                    State = st, Source = parentSource, Killer = killer, KillerRole = killerRole,
                    KillerAttachment = killerAttachment, KillerSceneGeneration = killerSceneGeneration,
                    KillerWorldScene = killerWorldScene,
                    KillerName = killerName, BeforeHp = beforeHp,
                };
                _log.LogError(probeFailure, "MVP {Cfg} source probe failed; retaining frozen source {Source}", st.Def.CfgId, parentSource);
                throw new EventSettlementOutcomeUnknownException("MVP settlement outcome is unresolved.",
                    new AggregateException(failure, probeFailure));
            }
            if (committed != null)
            {
                AdoptCommittedSettlement(m, st, committed, killerName);
                _log.LogWarning(failure, "MVP {Cfg} settlement source committed; publishing frozen result after apply fault", st.Def.CfgId);
                return;
            }
            throw new EventSettlementRolledBackException("MVP settlement did not commit.", failure);
        }
        finally { EndPending(st); }
    }

    private void AdoptCommittedSettlement(MonsterUnit m, State st, MvpSettlementSnapshot committed, string killerName)
    {
        st.Phase = MVP_STATE.Reborn;
        st.StateTimeMs = committed.DeathMs;
        if (!st.DeadMs.Contains(committed.DeathMs))
        {
            st.DeadMs.Add(committed.DeathMs);
            if (st.DeadMs.Count > 20) st.DeadMs.RemoveAt(0);
        }
        _lastDeath[st.Def.CfgId] = Math.Max(_lastDeath.GetValueOrDefault(st.Def.CfgId), committed.DeathMs);
        if (!_preparedKills.ContainsKey(m))
            _preparedKills[m] = new PreparedKill { State = st, Obligations = committed.Obligations, KillerName = killerName };
        _unpublishedLethalHits.Remove(m);
    }

    private async Task<MvpSettlementSnapshot?> ProbeSettlementAsync(string source)
    {
        if (BeforeSettlementProbeForTest is { } before) await before(source);
        return await _accounts.ReadAsync<MvpSettlementSnapshot?>(new MvpSettlementProbeReadPlan(source));
    }

    private async Task ResolveUnknownDeathAsync(MonsterUnit m, UnknownKill unknown)
    {
        var st = unknown.State;
        st.Pending++;
        try
        {
            var cfg = st.Def.CfgId.ToString(CultureInfo.InvariantCulture);
            var keys = new List<ReservationKey>
            {
                ReservationKey.Entity("mvp_cooldown", cfg),
                ReservationKey.Entity("mvp_settlement", unknown.Source),
            };
            if (st.Runtime.LineNumber == 1) keys.Add(ReservationKey.Entity("mvp_state", cfg));
            using (var scope = _loop.Owners is { } owners
                ? await owners.EnterAsync("mvp/resolve-uncertain", keys.ToArray()) : null)
            {
                var committed = await ProbeSettlementAsync(unknown.Source);
                if (!_unknownKills.TryGetValue(m, out var current) || !ReferenceEquals(current, unknown) || st.Retired) return;
                if (committed == null)
                {
                    _combat.RestoreUnpublishedLethalHit(m, unknown.BeforeHp);
                    _unknownKills.Remove(m);
                    return;
                }
                AdoptCommittedSettlement(m, st, committed, unknown.KillerName);
            }
            await _combat.CommitRetainedLethalRewardAsync(unknown.Killer, unknown.KillerRole,
                unknown.KillerAttachment, unknown.KillerSceneGeneration, unknown.KillerWorldScene, m, unknown.BeforeHp);
            if (BeforeRetainedPublicationForTest is { } beforePublication) await beforePublication(unknown.Source);
            await _combat.PublishRetainedLethalHitAsync(unknown.Killer, unknown.KillerRole,
                unknown.KillerAttachment, unknown.KillerSceneGeneration, unknown.KillerWorldScene, m);
            _unknownKills.Remove(m);
        }
        catch (Exception error)
        {
            if (m.Dead) _unknownKills.Remove(m);
            _log.LogError(error, "MVP frozen source {Source} recovery remains pending", unknown.Source);
        }
        finally { unknown.Resolving = false; EndPending(st); }
    }

    private Task OnKilledAsync(GameSession killer, MonsterUnit m) => FinalizePreparedAsync(m);

    private async Task FinalizePreparedAsync(MonsterUnit m)
    {
        if (!_preparedKills.TryGetValue(m, out var prepared))
        {
            if (Owner(m) != null) throw new InvalidOperationException("MVP death was published without a frozen settlement.");
            return;
        }
        if (prepared.Finalizing) return;
        prepared.Finalizing = true;
        var st = prepared.State;
        st.Pending++;
        try
        {
            st.Unit = null;
            st.Deadlines.RemoveAll(d => d.Actor == m);
            await ClearAddsAsync(st);
            if (!prepared.DeathBroadcastSent)
            {
                await BroadcastAsync(st.Runtime, DieMessage, ClientNpcName(st.Def.NpcId), prepared.KillerName);
                prepared.DeathBroadcastSent = true;
            }
            if (!prepared.DeliveryAttempted)
            {
                await RoleFanout.EachAsync(prepared.Obligations.Select(row => _sessions.All().FirstOrDefault(s => s.Role?.Id == row.PlayerId))
                    .Where(s => s != null).Cast<GameSession>(), TryDeliverPendingAsync);
                prepared.DeliveryAttempted = true; // offline/refused roles recover their durable obligation on login
            }
            if (!prepared.DeadNoticeSent)
            {
                await RoleFanout.EachAsync(_sessions.All().Where(p => p.Role != null && p.WorldScene == st.Runtime.WorldScene),
                    p => p.SendGameAsync(DeadNotify, new MVPDeadNotify { CfgId = st.Def.CfgId }));
                prepared.DeadNoticeSent = true;
            }
            _log.LogInformation("MVP {Cfg} killed by {Name}; {Count} participant(s)", st.Def.CfgId, prepared.KillerName, st.Stats.Count);
            _preparedKills.Remove(m);
        }
        finally { prepared.Finalizing = false; EndPending(st); }
    }

    // ---- settlement ----

    private async Task<MvpObligationRow[]> SettleAsync(State st, long killer, long deadMs, string parentSource)
    {
        var scores = Scores(st);
        var awards = st.Stats.Keys.ToDictionary(id => id, _ => new List<MVPPlayerAwardItem>());
        var online = _sessions.All().Where(s => s.Role != null && s.Progress != null && awards.ContainsKey(s.Role.Id))
            .ToDictionary(s => s.Role!.Id);
        await RoleFanout.EachAsync(online.Values, _tasks.PrepareProgressAsync);
        var keys = online.Keys.Select(id => ReservationKey.Role(id))
            .Concat(online.Keys.Select(id => RoleObligations<MvpObligationRow>.Key(parentSource + "/" + id)))
            .Append(ReservationKey.Entity("mvp_settlement", parentSource))
            .Append(ReservationKey.Entity("mvp_cooldown", st.Def.CfgId.ToString(CultureInfo.InvariantCulture)));
        if (st.Runtime.LineNumber == 1)
            keys = keys.Append(ReservationKey.Entity("mvp_state", st.Def.CfgId.ToString(CultureInfo.InvariantCulture)));
        using var scope = _loop.Owners is { } owners
            ? await owners.EnterAsync("mvp/kill-settlement", keys.ToArray()) : null;
        if (st.Retired) throw new OperationCanceledException("MVP line retired before lethal settlement.");
        online = online.Where(pair => pair.Value.Role?.Id == pair.Key && pair.Value.Progress != null
            && pair.Value.Attachment?.Cancelled != true).ToDictionary();
        var taskPlans = online.ToDictionary(pair => pair.Key,
            pair => _tasks.StageMvpParticipant(pair.Value, parentSource));
        var counters = new Dictionary<long, MVPPlayerInfo>();
        foreach (long player in online.Keys)
            counters[player] = (await LoadCountersAsync(player)).Info;
        // Counts before this kill decide the decay; offline participants have no loaded counters (inferred: not capped).
        var teamsRanked = RankTeams(st.Stats.Select(kv => (kv.Key, kv.Value.TeamId, kv.Value.Leader, scores.GetValueOrDefault(kv.Key))),
            id => counters.TryGetValue(id, out var c) && (c.TeamNum >= TeamCap || c.JoinNum >= JoinCap), capped => DecayPercent(_data, capped));

        void Give(long player, MVP_AWARD_TYPE tp, long awardId)
        {
            if (!online.TryGetValue(player, out var s)) return;
            var counter = counters[player];
            long current = tp switch
            {
                MVP_AWARD_TYPE.First => counter.FirstNum,
                MVP_AWARD_TYPE.End => counter.EndNum,
                MVP_AWARD_TYPE.Team => counter.TeamNum,
                MVP_AWARD_TYPE.Join => counter.JoinNum,
                _ => 0,
            };
            int cap = tp switch { MVP_AWARD_TYPE.First => FirstCap, MVP_AWARD_TYPE.End => EndCap, MVP_AWARD_TYPE.Team => TeamCap, MVP_AWARD_TYPE.Join => JoinCap, _ => int.MaxValue };
            if (current >= cap) return; // 26051 "Today's Reward Count has been exhausted"
            switch (tp)
            {
                case MVP_AWARD_TYPE.First: counter.FirstNum++; break;
                case MVP_AWARD_TYPE.End: counter.EndNum++; break;
                case MVP_AWARD_TYPE.Team: counter.TeamNum++; break;
                case MVP_AWARD_TYPE.Join: counter.JoinNum++; break;
            }
            foreach (var (entry, count) in _rewards.RollAward(awardId, s))
                awards.GetValueOrDefault(player)?.Add(new MVPPlayerAwardItem { ItemId = entry, Count = count, Tp = tp });
        }

        if (st.FirstPlayer != 0 && awards.ContainsKey(st.FirstPlayer)) Give(st.FirstPlayer, MVP_AWARD_TYPE.First, st.Def.First);
        if (awards.ContainsKey(killer)) Give(killer, MVP_AWARD_TYPE.End, st.Def.Last);
        var rankedMembers = teamsRanked.Take(RankedTeams).SelectMany(t => t.Members).ToHashSet();
        foreach (var id in rankedMembers) Give(id, MVP_AWARD_TYPE.Team, st.Def.Team);
        foreach (var id in st.Stats.Keys) Give(id, MVP_AWARD_TYPE.Join, st.Def.Part);
        // One rare winner per kill, among the ranked/participation receivers, weighted towards better ranks (inferred).
        long rarePlayer = 0, rareTeam = 0;
        if (st.Stats.Count > 0 && _random.Next(10000) < st.Def.RareChance)
        {
            var pool = teamsRanked.SelectMany((t, i) => t.Members.Select(id => (Id: id, Team: t.Key, Weight: Math.Max(1, teamsRanked.Count - i)))).ToList();
            long roll = _random.Next(pool.Sum(x => x.Weight));
            foreach (var x in pool)
            {
                roll -= x.Weight;
                if (roll >= 0) continue;
                rarePlayer = x.Id;
                rareTeam = x.Team;
                break;
            }
            if (rarePlayer != 0) Give(rarePlayer, MVP_AWARD_TYPE.Probability, st.Def.Rare);
        }
        var nextDeaths = st.DeadMs.Append(deadMs).TakeLast(20).ToArray();
        var builder = new RoleItemPlanBuilder(_accounts);
        builder.Add(new MvpSettlementPlan(parentSource, st.Def.CfgId, deadMs));
        if (st.Runtime.LineNumber == 1)
            builder.Add(new MvpWritePlan(st.Def.CfgId, (int)MVP_STATE.Reborn, deadMs, string.Join(',', nextDeaths)));
        builder.Add(new MvpWritePlan(st.Def.CfgId, deadMs));
        var obligations = new List<MvpObligationRow>();
        foreach (var player in online.Keys)
        {
            builder.Add(new MvpWritePlan(player, CounterRow(counters[player])));
            taskPlans[player]?.Persist(builder);
            var items = awards[player];
            var ntf = new MVPResultNtf { CfgId = st.Def.CfgId, PlayerInfo = counters[player].Clone() };
            ntf.Awards.Add(items);
            var grants = items.Select(item => new AwardGrant(checked((int)item.ItemId), checked((int)item.Count))).ToArray();
            obligations.Add(new(parentSource + "/" + player, st.Def.CfgId, deadMs, player,
                JsonSerializer.SerializeToUtf8Bytes(grants), ntf.ToByteArray()));
        }
        if (obligations.Count > 0) builder.Add(RoleObligations<MvpObligationRow>.CreatePlan(obligations));
        var catalog = EventEconomy.Catalog(_data);
        var minted = obligations.SelectMany(row => JsonSerializer.Deserialize<AwardGrant[]>(row.Awards)!
            .Where(grant => grant.Entry is not (100 or 101))
            .Select(grant => (row.PlayerId, Grant: grant))).ToArray();
        if (minted.Length > 0)
        {
            var goods = minted.Select(x => new EconomyQuantity(catalog.TryAsset(x.Grant.Entry, out var asset)
                ? asset : throw new InvalidOperationException("MVP award has an unknown economic item."), x.Grant.Count)).ToArray();
            var deltas = minted.Select((x, index) => new EventEconomyOwnerDelta(
                new EconomyOwner(EconomyOwnerKind.Role, x.PlayerId.ToString(CultureInfo.InvariantCulture)),
                goods[index].Asset, x.Grant.Count)).ToArray();
            builder.WithEventEconomy(new EventEconomyMutation(catalog, Guid.NewGuid().ToString("N"), "realm", 0,
                "event/mvp-settlement", parentSource, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                [], goods, deltas, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    st.Def.CfgId, DeadMs = deadMs,
                    Recipients = obligations.Select(row => new
                    {
                        row.PlayerId, row.SourceKey,
                        AwardHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(row.Awards)),
                        ResultHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(row.Result))
                    }).ToArray()
                })));
        }
        await builder.CommitAsync(_accounts, "mvp/kill-settlement", _ =>
        {
            foreach (var plan in taskPlans.Values) plan?.Apply();
            st.Phase = MVP_STATE.Reborn;
            st.StateTimeMs = deadMs;
            st.DeadMs.Clear(); st.DeadMs.AddRange(nextDeaths);
            _lastDeath[st.Def.CfgId] = Math.Max(_lastDeath.GetValueOrDefault(st.Def.CfgId), deadMs);
        });
        StoreReports(st, killer, deadMs, rarePlayer, rareTeam, scores, teamsRanked, awards);
        foreach (var participant in online.Values)
            foreach (var handler in ParticipantSettled)
                try { handler(participant); }
                catch (Exception e) { _log.LogError(e, "MVP participant hook failed for {Role}", participant.Role?.Id); }
        return obligations.ToArray();
    }

    internal sealed record RankedTeam(long Key, long Leader, List<long> Members, long Score, bool Decayed);

    /// <summary>
    /// Parties ranked by points (rules text 10960000073): the members' scores summed; a party with members who have no
    /// remaining Ranking or Participation count has a positive sum decayed, more such members more decay. The Point tab
    /// shows the decay icon for param2 0. Which counts make a member "capped" (either one) is inferred.
    /// </summary>
    internal static List<RankedTeam> RankTeams(IEnumerable<(long Role, long Team, long Leader, long Score)> players,
        Func<long, bool> capped, Func<int, int> decayPercent) =>
        players.GroupBy(p => p.Team != 0 ? p.Team : -p.Role).Select(g =>
        {
            var members = g.Select(p => p.Role).ToList();
            long sum = g.Sum(p => p.Score);
            int percent = decayPercent(members.Count(capped));
            return new RankedTeam(g.Key, g.First().Leader, members, sum > 0 ? sum * percent / 100 : sum, percent < 100);
        }).OrderByDescending(t => t.Score).ToList();

    /// <summary>PublicParamConfig RewardDecayRatio [50,60,70,80,90] as the percent kept: one capped member 90, two 80 ...
    /// five or more 50 (order inferred: "the more such members, the greater the decay").</summary>
    internal static int DecayPercent(GameData data, int capped)
    {
        if (capped <= 0 || !data.Table("PublicParamConfig").TryGetValue("RewardDecayRatio", out var list)
            || list.ValueKind != JsonValueKind.Array) return 100;
        var ratios = list.EnumerateArray().Select(v => (int)(GameData.Number(v) ?? 100)).OrderByDescending(v => v).ToArray();
        return ratios.Length == 0 ? 100 : ratios[Math.Min(capped, ratios.Length) - 1];
    }

    /// <summary>MVPScoreConfig points by rank in each statistic, deaths subtract (inferred).</summary>
    private Dictionary<long, long> Scores(State st)
    {
        var score = st.Stats.Keys.ToDictionary(id => id, _ => 0L);
        void Rank(Func<Stat, long> value, string field, bool ascending, int sign)
        {
            var ranked = st.Stats.Where(kv => value(kv.Value) > 0);
            ranked = ascending ? ranked.OrderBy(kv => value(kv.Value)) : ranked.OrderByDescending(kv => value(kv.Value));
            int rank = 0;
            foreach (var kv in ranked)
            {
                rank++;
                if (_data.Row("MVPScoreConfig", Math.Min(rank, 500)) is { } row) score[kv.Key] += sign * GameData.Int(row, field);
            }
        }
        Rank(s => s.Attack, "_iDemage", false, 1);
        Rank(s => s.BeAttack, "_iDemageSuffer", false, 1);
        Rank(s => s.Cure, "_iHeal", false, 1);
        Rank(s => s.Dead, "_iDeath", true, -1);
        return score;
    }

    private void StoreReports(State st, long killer, long deadMs, long rarePlayer, long rareTeam, Dictionary<long, long> scores,
        List<RankedTeam> ranked, Dictionary<long, List<MVPPlayerAwardItem>> awards)
    {
        foreach (MVPRankType tp in Enum.GetValues<MVPRankType>())
        {
            Func<Stat, long> value = tp switch
            {
                MVPRankType.Attack => s => s.Attack,
                MVPRankType.BeAttack => s => s.BeAttack,
                MVPRankType.Cure => s => s.Cure,
                MVPRankType.Dead => s => s.Dead,
                _ => _ => 0,
            };
            long total = Math.Max(1, st.Stats.Values.Sum(value));
            var info = new MVPReportRankInfo
            {
                CfgId = st.Def.CfgId, Tp = tp, FirstPlayerId = st.FirstPlayer, EndPlayerId = killer, TimeStamp = deadMs, DeadMs = deadMs,
                RarePlayerId = rarePlayer, RareTeamId = rareTeam,
            };
            var order = tp == MVPRankType.Total ? ranked
                : ranked.OrderByDescending(t => t.Members.Sum(m => value(st.Stats[m]))).ToList();
            foreach (var t in order.Take(100))
            {
                long teamValue = tp == MVPRankType.Total ? t.Score : t.Members.Sum(m => value(st.Stats[m]));
                var team = new MVPReportTeamInfo
                {
                    TeamId = t.Key, LeaderId = t.Leader, Param1 = teamValue,
                    Param2 = tp == MVPRankType.Total ? t.Decayed ? 0 : 1 : tp == MVPRankType.Dead ? 0 : teamValue * 10000 / total,
                };
                foreach (var m in t.Members)
                {
                    long v = tp == MVPRankType.Total ? scores.GetValueOrDefault(m) : value(st.Stats[m]);
                    var player = new MVPReportPlayerInfo
                    {
                        PlayerId = m, Param1 = v, Param2 = tp is MVPRankType.Total or MVPRankType.Dead ? 0 : v * 10000 / total,
                    };
                    player.Items.Add(awards.GetValueOrDefault(m) ?? []);
                    team.Players.Add(player);
                }
                info.Teams.Add(team);
            }
            st.Reports[(deadMs, tp)] = info;
        }
        foreach (var key in st.Reports.Keys.Where(k => !st.DeadMs.Contains(k.Version)).ToList()) st.Reports.Remove(key);
    }

    // ---- helpers ----

    private State? Owner(MonsterUnit m) => _states?.Values.Concat(_lineStates.Values.SelectMany(v => v.Values))
        .FirstOrDefault(st => !st.Retired && st.Unit == m);

    private Stat StatOf(State st, long role)
    {
        if (!st.Stats.TryGetValue(role, out var stat))
        {
            var (team, leader) = _teams.TeamOf(role);
            st.Stats[role] = stat = new Stat { TeamId = team, Leader = leader };
        }
        return stat;
    }

    private string NpcName(int npcId) => _data.Row("NPCConfig", npcId) is { } row ? _data.TextOf(row, "_kName") : npcId.ToString();
    private string ClientNpcName(int npcId) => _data.Row("NPCConfig", npcId) is { } row ? _data.ClientTextOf(row, "_kName") : npcId.ToString();

    /// <summary>1022 marquee: ChatConfig 603 "${1} has spawned on the ${2} map." / 604 "${1} was defeated by ${2}." (param encoding inferred).</summary>
    private async Task BroadcastAsync(RuntimeMapKey runtime, int chatId, params string[] args)
    {
        var ntf = new SystemBoardCastMsgNtf { Source = MsgSource.System, ChatId = chatId };
        for (int i = 0; i < args.Length; i++) ntf.ParamList[i + 1] = new ChatParameter { Params = { args[i] }, Type = ChatParamType.ChatParamString };
        await RoleFanout.EachAsync(_sessions.All().Where(p => p.Role != null && p.WorldScene == runtime.WorldScene),
            p => p.SendGameAsync(SystemBroadcast, ntf));
    }

    private Dictionary<int, State> States()
    {
        if (_states != null) return _states;
        var states = new Dictionary<int, State>();
        foreach (var row in _data.Table("MVPConfig").Values)
        {
            int cfg = (int)GameData.Int(row, "_iID"), npc = (int)GameData.Int(row, "_iNPCID");
            if (cfg == 0 || _data.Row("NPCConfig", npc) is not { } npcRow) continue;
            var def = new Def(cfg, npc, GameData.Int(row, "_iMapID"), (int)(GameData.Int(npcRow, "_iLevel") + GameData.Int(row, "_iLevelDiff")),
                Math.Max(60, GameData.Int(row, "_iRefreshTime")) * 1000, GameData.Int(row, "_iFirstDamageAward"), GameData.Int(row, "_iLastDamageAward"),
                GameData.Int(row, "_iTeamAward"), GameData.Int(row, "_iPartAward"), GameData.Int(row, "_iRareAward"), (int)GameData.Int(row, "_iRareRewardProbability"));
            states[cfg] = new State { Def = def, Runtime = RuntimeMapKey.Public(def.MapId), StateTimeMs = MonsterService.NowMs };
        }
        foreach (var row in _accounts.ReadSync<List<MvpStateRow>>(new MvpReadPlan(MvpReadKind.States)))
        {
            if (!states.TryGetValue(row.Config, out var st)) continue;
            st.Phase = (MVP_STATE)row.Phase == MVP_STATE.Reborn ? MVP_STATE.Reborn : MVP_STATE.Born;
            st.StateTimeMs = row.Time;
            st.DeadMs.AddRange(row.Dead.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse));
        }
        foreach (var row in _accounts.ReadSync<List<(int Config, long Death)>>(new MvpReadPlan(MvpReadKind.Cooldowns)))
            _lastDeath[row.Config] = row.Death;
        foreach (var st in states.Values.Where(s => s.Phase == MVP_STATE.Reborn))
            _lastDeath[st.Def.CfgId] = Math.Max(_lastDeath.GetValueOrDefault(st.Def.CfgId), st.StateTimeMs);
        _states = states;
        _log.LogInformation("MVP: {Count} field MVPs", states.Count);
        return states;
    }

    private async Task SaveStateAsync(State st, MVP_STATE phase, long time, bool onlyIfReborn = false)
    {
        if (st.Runtime.LineNumber == 1)
        {
            using var scope = _loop.Owners is { } owners ? await owners.EnterAsync("mvp/state",
                ReservationKey.Entity("mvp_state", st.Def.CfgId.ToString(CultureInfo.InvariantCulture))) : null;
            if (onlyIfReborn && st.Phase != MVP_STATE.Reborn) return;
            var builder = new RoleItemPlanBuilder(_accounts);
            builder.Add(new MvpWritePlan(st.Def.CfgId, (int)phase, time, string.Join(',', st.DeadMs)));
            await builder.CommitAsync(_accounts, "mvp/state");
        }
        else if (onlyIfReborn && st.Phase != MVP_STATE.Reborn) return;
        st.Phase = phase;
        st.StateTimeMs = time;
    }

    /// <summary>The player's shared daily counters, projected at 05:00; a mutation persists the reset.</summary>
    private async Task<(MVPPlayerInfo Info, bool Reset)> LoadCountersAsync(long player)
    {
        var info = new MVPPlayerInfo { PlayerId = player };
        if (await _accounts.ReadAsync<MvpCounterRow?>(new MvpReadPlan(MvpReadKind.Counters, player)) is { } row)
        {
            info.FirstNum = row.First;
            info.EndNum = row.End;
            info.JoinNum = row.Join;
            info.TeamNum = row.Team;
            info.LastTime = row.Last;
        }
        var now = DateTime.Now;
        var lastReset = now.Date.AddHours(ResetHour) is var today && now >= today ? today : today.AddDays(-1);
        if (DateTimeOffset.FromUnixTimeMilliseconds(info.LastTime).LocalDateTime < lastReset)
        {
            info.FirstNum = info.EndNum = info.JoinNum = info.TeamNum = 0;
            info.LastTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return (info, true);
        }
        return (info, false);
    }

    private static MvpCounterRow CounterRow(MVPPlayerInfo info) =>
        new(info.FirstNum, info.EndNum, info.JoinNum, info.TeamNum, info.LastTime);

    /// <summary>C/D 15: the role's pending obligations, read before the role select admits so it can reserve them.</summary>
    internal Task<ReservationKey[]> LoginKeysAsync(long roleId) => _awardObligations.LoginKeysAsync(roleId);

    /// <summary>On login and after a kill, complete frozen rewards without rolling again.</summary>
    public Task RecoverAsync(GameSession session) => DeliverPendingAsync(session);

    private async Task TryDeliverPendingAsync(GameSession session)
    {
        try { await DeliverPendingAsync(session); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            _log.LogError(error, "MVP reward remains durable and pending for role {Role}", session.Role?.Id);
        }
    }

    private async Task DeliverPendingAsync(GameSession session)
    {
        long? role = session.Role?.Id;
        if (role == null || session.Progress == null
            || session.Owner?.State == RoleOwnerState.RecoveryRequired) return;
        var attachment = session.Attachment;
        // Inside the role select only the obligations it reserved are delivered; a later one stays durable and
        // pending for the next kill or login.
        await _awardObligations.DeliverPendingAsync(session, role.Value, () => session.Role?.Id == role
            && ReferenceEquals(session.Attachment, attachment) && attachment?.Cancelled != true && session.Progress != null
            && session.Owner?.State != RoleOwnerState.RecoveryRequired);
    }

    /// <summary>One frozen award: its goods and the delivered mark in one commit (event/mvp-delivery), then the result
    /// popup and the source-linked quest follow-up. False stops the delivery pass (refused, or the role moved on).</summary>
    private async Task<bool> PayAsync(GameSession session, MvpObligationRow row)
    {
        var attachment = session.Attachment;
        using (var held = await CharacterService.Lock(session, [RoleObligations<MvpObligationRow>.Key(row.SourceKey)]))
        {
            if (session.Role?.Id != row.PlayerId) return false;
            var grants = JsonSerializer.Deserialize<AwardGrant[]>(row.Awards)
                ?? throw new InvalidDataException("Invalid frozen MVP award obligation.");
            var economy = grants.Any(grant => grant.Entry is not (100 or 101))
                ? EventEconomy.ForRole(_data, session, "mvp-delivery", "mvp/delivery/" + row.SourceKey,
                    Array.Empty<(int Entry, long Count)>(), Array.Empty<AwardGrant>(),
                    new
                    {
                        row.SourceKey, row.PlayerId,
                        AwardHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(row.Awards))
                    }) : null;
            if (!await _character.ApplyAwardAsync(session, new AwardPlan(0, 0, grants), persist: builder =>
                {
                    builder.Add(RoleObligations<MvpObligationRow>.DeliverPlan(row));
                    if (economy != null) builder.WithEventEconomy(economy);
                }, stateLockHeld: true)) return false;
        }
        if (session.Role?.Id != row.PlayerId || !ReferenceEquals(session.Attachment, attachment)
            || attachment?.Cancelled == true) return false;
        await session.SendGameAsync(ResultNtf, MVPResultNtf.Parser.ParseFrom(row.Result));
        await _tasks.RefreshAsync(session); // settles the source-linked 103022 task follow-up.
        return true;
    }
}
