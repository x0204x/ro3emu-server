using System.Text.Json;
using System.Globalization;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Festival Invitation: DailyActivity 3301 / TimeLimit 1083, 43504.rainbowKoiBet and dungeon 1010002.
/// LC_RainbowKoiHelper, LC_DunGeon_RainbowKoi and DungeonVariableConfig define the wire contract;
/// sc_prontera_001_dg01_sceneeditor regions 27/28 are the true/false answer zones. Candidate selection,
/// question ordering, a 30-second answer / 5-second reveal cadence, tied ranks and automatic reward delivery
/// are (inferred). Position at the deadline is authoritative; a client event cannot answer or award the quiz.
/// </summary>
public sealed class FestivalService : IGameTicker
{
    public const int ActivityId = 3301;
    public const long Dungeon = 1010002;
    private readonly GameData data;
    private readonly AccountStore accounts;
    private readonly RoleStore roles;
    private readonly CharacterService character;
    private readonly FunctionService functions;
    private readonly AwardService awards;
    private readonly ActivityClock clock;
    private readonly TimedTodoSchedule schedule;
    private readonly DailyActivityService daily;
    private readonly InstanceService instances;
    private readonly SessionRegistry sessions;
    private readonly GameLoop loop;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<int, Zone> zones = new();
    private Round? current;
    private InstanceService.Room? room;
    private TimeSpan lastTick;
    private bool queued;
    private bool reconciledOnce;
    /// <summary>Rewards were written for some roles since the last online delivery (login delivers the rest).</summary>
    private bool deliverOnline;
    /// <summary>The shared part of <see cref="Project"/> last sent to every session; unchanged = nothing to sync.</summary>
    private string? lastProjection;
    private long Now => clock.UtcNow.ToUnixTimeSeconds();

    public sealed class Candidate
    {
        public bool Entered { get; set; }
        public bool Eliminated { get; set; }
        public bool GiveUp { get; set; }
        public int Rank { get; set; }
    }

    public sealed class Round
    {
        public long Cycle { get; set; }
        public long AnswerStart { get; set; }
        public long End { get; set; }
        public int Phase { get; set; }
        public int Question { get; set; }
        public long Deadline { get; set; }
        public long[] Questions { get; set; } = [];
        public List<long> Answers { get; set; } = new();
        public Dictionary<long, Candidate> Candidates { get; set; } = new();
        public Dictionary<long, long> Bets { get; set; } = new();
        public bool Complete { get; set; }
    }

    private sealed record Zone(float X, float Z, float Width, float Length, float Direction)
    {
        public bool Contains(Vec3 p)
        {
            double radians = Direction * Math.PI / 180, dx = p.X - X, dz = p.Z - Z;
            return Math.Abs(dx * Math.Cos(radians) + dz * Math.Sin(radians)) <= Width / 2
                && Math.Abs(dz * Math.Cos(radians) - dx * Math.Sin(radians)) <= Length / 2;
        }
    }

    public FestivalService(GameData data, AccountStore accounts, RoleStore roles, CharacterService character, FunctionService functions,
        AwardService awards, ActivityClock clock, TimedTodoSchedule schedule, DailyActivityService daily,
        InstanceService instances, SceneService scene, SessionRegistry sessions, GameLoop loop, IOptions<ServerOptions> options)
    {
        this.data = data; this.accounts = accounts; this.roles = roles; this.character = character; this.functions = functions;
        this.awards = awards; this.clock = clock; this.schedule = schedule; this.daily = daily;
        this.instances = instances; this.sessions = sessions; this.loop = loop;
        if (accounts.ReadSync<byte[]?>(new FestivalReadPlan(FestivalReadKind.LatestRound)) is { } bytes)
            current = JsonSerializer.Deserialize<Round>(bytes);
        if (data.Row("DungeonConfig", Dungeon) is { } config
            && config.TryGetProperty("_kResSceneEditorName", out var editor))
        {
            if (DataFiles.Find(Path.Combine(options.Value.GameDataDir ?? options.Value.DataDir, "sceneeditor"), editor.GetString() + ".json") is { } path)
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                if (doc.RootElement.TryGetProperty("m_kRegins", out var regions))
                    foreach (int id in new[] { 27, 28 })
                        if (regions.TryGetProperty(id.ToString(), out var region))
                        {
                            var center = region.GetProperty("center");
                            zones[id] = new(center.GetProperty("x").GetSingle(), center.GetProperty("z").GetSingle(),
                                region.GetProperty("width").GetSingle(), region.GetProperty("length").GetSingle(),
                                region.GetProperty("direction").GetSingle());
                        }
            }
        }
        daily.EventProjections.Add(Project);
        scene.Loaded.Add(LoadedAsync);
        scene.Leaving.Add(LeavingAsync);
    }

    private ActivityClock.Window? Segment => clock.Segments(1083, Now).FirstOrDefault(w => w.Contains(Now));

    internal bool IsOwnerRequest(uint message, ReadOnlyMemory<byte> body)
    {
        if (message == DailyActivityService.RingAccept)
        {
            var request = CommonDailyActivityReq.Parser.ParseFrom(body.Span);
            return request.ActivityId == ActivityId && request.RainbowKoiBet != null;
        }
        if (message == DungeonService.Enter)
            return EnterDungeonReq.Parser.ParseFrom(body.Span).SceneEntry == Dungeon;
        return false;
    }

    private ReservationKey[] RoundKeys(params long[] additionalRoles)
    {
        var keys = new List<ReservationKey> { ReservationKey.Entity("festival_runtime", ActivityId.ToString(CultureInfo.InvariantCulture)) };
        var cycles = new HashSet<long>();
        if (current is { } existing) cycles.Add(existing.Cycle);
        var windows = clock.Segments(1083, Now);
        var active = windows.FirstOrDefault(window => window.Contains(Now));
        if (active != null && windows.FirstOrDefault(window => window.Cycle == active.Cycle
                && window.State == "Candidate") is { } candidate)
            cycles.Add(candidate.Start);
        foreach (long cycle in cycles.Order())
            keys.Add(ReservationKey.Entity("festival_round", cycle.ToString(CultureInfo.InvariantCulture)));
        var participants = (current?.Candidates.Keys ?? Enumerable.Empty<long>())
            .Concat(current?.Bets.Keys ?? Enumerable.Empty<long>()).Concat(additionalRoles).Where(id => id > 0).Distinct();
        foreach (long id in participants)
        {
            keys.Add(ReservationKey.Role(id));
            if (current is { } round)
                foreach (int source in new[] { 0, 1 })
                    keys.Add(ReservationKey.Entity("festival_reward", $"{round.Cycle}/{id}/{source}"));
        }
        return keys.ToArray();
    }

    private async Task<LegacyOwnerScope?> EnterRoundWithGateAsync(string origin, params long[] additionalRoles)
    {
        if (loop.Owners is not { } owners)
        {
            await gate.WaitAsync();
            return null;
        }
        while (true)
        {
            var keys = RoundKeys(additionalRoles);
            var scope = await owners.EnterWithGateAsync(origin, gate, keys);
            if (keys.SequenceEqual(RoundKeys(additionalRoles))) return scope;
            gate.Release();
            scope.Dispose();
        }
    }

    private async Task EnsureRoundAsync()
    {
        // Settle the preceding occurrence before selecting a later one after a clock jump or restart (inferred).
        if (current is { Complete: false } old && Now >= old.End) await CompleteAsync(Clone(old));
        var windows = clock.Segments(1083, Now);
        var active = windows.FirstOrDefault(w => w.Contains(Now));
        if (active == null) return;
        var cycle = windows.FirstOrDefault(w => w.Cycle == active.Cycle && w.State == "Candidate");
        var answer = windows.FirstOrDefault(w => w.Cycle == active.Cycle && w.State == "Answer");
        if (cycle == null || answer == null) return;
        bool changed = false, newCycle = current?.Cycle != cycle.Start;
        Round? next = newCycle ? null : current == null ? null : Clone(current);
        if (newCycle)
        {
            changed = true;
            next = await accounts.ReadAsync<byte[]?>(new FestivalReadPlan(FestivalReadKind.Round, cycle.Start))
                is { } saved ? JsonSerializer.Deserialize<Round>(saved) : null;
            next ??= new Round
            {
                Cycle = cycle.Start, AnswerStart = answer.Start, End = answer.End,
                Deadline = answer.Start, Questions = data.Table("RainbowKoiQuestionConfig").Values
                    .Select(r => (long)GameData.Int(r, "_iID")).Where(id => id > 0).Order().Take(15).ToArray()
            };
        }
        // (inferred) Select eligible online roles during the candidate minute, stable by role id, capped by the map.
        if (active.State == "Candidate" && !next!.Complete)
        {
            int maximum = data.Row("DungeonConfig", Dungeon) is { } config ? checked((int)Math.Max(1, GameData.Int(config, "_iRoleCntLimit"))) : 20;
            foreach (var s in sessions.All().Where(s => s.Role != null && s.ActivitiesReady && s.DailyReady && functions.IsOpen(s, 5901)).OrderBy(s => s.Role!.Id))
            {
                if (next.Candidates.Count >= maximum) break;
                changed |= next.Candidates.TryAdd(s.Role!.Id, new());
            }
        }
        if (changed) await SaveAsync(next!);
        if (newCycle) room = null;
    }

    public async Task LoginAsync(GameSession session)
    {
        // C/D 15: the round is realm state; the one-second Tick ensures and settles it from startup, so the login no
        // longer reserves the festival runtime. It only delivers this role's own committed entitlements.
        if (session.Role != null) await RoleFanout.EachAsync([session], DeliverAsync);
        await daily.SyncEventsAsync(session);
    }

    private void Project(GameSession session, DBDailyActivityInfo info)
    {
        if (!info.ActivityData.TryGetValue(ActivityId, out var row)) return;
        var segment = Segment;
        var state = new PlayerActivityInfo
        {
            ActivityKey = ActivityId.ToString(), Playbook = 101,
            State = segment?.State ?? "_close", ActivityInstance = current?.Cycle ?? 0,
            CurStartTime = segment?.Start ?? 0, CurEndTime = segment?.End ?? 0,
            CycleStartTime = current?.Cycle ?? 0, CycleEndTime = current?.End ?? 0,
            IsEnterComplete = segment != null, IsExitComplete = current?.Complete ?? false,
            ActivityParam = new()
            {
                RainbowKoi = new()
                {
                    BetTargetId = current?.Bets.GetValueOrDefault(session.Role!.Id) ?? 0,
                    Progress = new() { IsGameOver = current?.Complete ?? false }
                }
            }
        };
        if (current != null)
            foreach (var (id, c) in current.Candidates)
                state.ActivityParam.RainbowKoi.Progress.CandidateMap[id] = new RainbowKoiCandidateInfo
                { IsEliminated = c.Eliminated, IsGiveUp = c.GiveUp, Rank = c.Rank, IsSettle = current.Complete || c.Eliminated };
        row.ActivityInfo = state;
    }

    public async Task BetAsync(GameSession session, CommonDailyActivityReq req)
    {
        var scope = await EnterRoundWithGateAsync("festival/bet", session.Role?.Id ?? 0,
            req.RainbowKoiBet?.TargetPlayerId ?? 0);
        int result;
        try
        {
            await EnsureRoundAsync();
            result = req.ActivityId != ActivityId || req.RainbowKoiBet == null || session.Role == null ? ErrorCodes.Parameter
                : !session.ActivitiesReady ? ErrorCodes.FeatureUnavailable
                : !functions.IsOpen(session, 5901) ? ErrorCodes.FunctionLocked
                : !schedule.IsOpen(ActivityId) || Segment?.State != "Ready" || current == null || current.Complete ? ErrorCodes.ActivityPeriod
                : !current.Candidates.TryGetValue(req.RainbowKoiBet.TargetPlayerId, out var target) || target.GiveUp ? ErrorCodes.Parameter
                : current.Bets.ContainsKey(session.Role.Id) ? ErrorCodes.RewardClaimed : 0;
            if (result == 0)
            {
                var next = Clone(current!);
                next.Bets[session.Role!.Id] = req.RainbowKoiBet!.TargetPlayerId;
                await SaveAsync(next);
            }
        }
        finally { gate.Release(); scope?.Dispose(); }
        if (session.Role is { } role && loop.Owners is { } dailyOwners)
            await dailyOwners.RunSessionAsync("festival/bet daily projection",
                [ReservationKey.Role(role.Id), ReservationKey.Lifecycle(role.Id)], session,
                () => daily.SyncEventsAsync(session));
        else await daily.SyncEventsAsync(session);
        await session.SendGameAsync(DailyActivityService.RingAccept, new CommonDailyActivityRes
        {
            ActivityId = req.ActivityId, RequestId = req.RequestId, Result = result,
            RainbowKoiBet = new() { TargetPlayerId = req.RainbowKoiBet?.TargetPlayerId ?? 0 }
        });
    }

    public async Task<bool> TryEnterAsync(GameSession session, EnterDungeonReq req)
    {
        if (req.SceneEntry != Dungeon) return false;
        var scope = await EnterRoundWithGateAsync("festival/enter", session.Role?.Id ?? 0);
        try
        {
            await EnsureRoundAsync();
            int error = !session.ActivitiesReady ? ErrorCodes.FeatureUnavailable
                : session.Role == null || !functions.IsOpen(session, 5901) ? ErrorCodes.FunctionLocked
                : session.SceneLoading ? ErrorCodes.SwitchingScenes
                : session.InstanceId != 0 ? ErrorCodes.DungeonBusy
                : session.Dead || !schedule.IsOpen(ActivityId) || Segment?.State is not ("Candidate" or "Ready")
                    || current == null || current.Complete ? ErrorCodes.ActivityPeriod
                : !current.Candidates.TryGetValue(session.Role.Id, out var c) || c.GiveUp ? ErrorCodes.NoPermission
                : zones.Count != 2 || req.InitData?.RainbowKoiInitData != null ? ErrorCodes.Parameter : 0;
            if (error == 0)
            {
                if (room == null || room.Closed) room = instances.Create(Dungeon, [session]);
                else if (!instances.AddParticipant(room, session)) error = ErrorCodes.DungeonBusy;
            }
            await session.SendGameAsync(DungeonService.Enter, new EnterDungeonRsp { Result = error });
            if (error == 0 && await instances.EnterAsync(room!, session, 3))
            {
                var next = Clone(current!);
                next.Candidates[session.Role!.Id].Entered = true;
                await SaveAsync(next);
            }
        }
        finally { gate.Release(); scope?.Dispose(); }
        return true;
    }

    private Task LoadedAsync(GameSession session) => room != null && session.InstanceId == room.Id
        ? session.SendGameAsync(DungeonService.BaseInfo, Snapshot()) : Task.CompletedTask;

    private async Task LeavingAsync(GameSession session)
    {
        long? leavingRole = session.Role?.Id;
        if (leavingRole is null || room == null || session.InstanceId != room.Id || current is not { Complete: false }
            || !current.Candidates.TryGetValue(leavingRole.Value, out var candidate) || candidate.Eliminated)
            return;
        using var scope = loop.Owners is { } owners
            ? await owners.EnterAsync("festival/leave", RoundKeys(leavingRole.Value)) : null;
        if (session.Role?.Id != leavingRole || room == null || session.InstanceId != room.Id
            || current is not { Complete: false } || !current.Candidates.TryGetValue(leavingRole.Value, out candidate)
            || candidate.Eliminated)
            return;
        var next = Clone(current);
        next.Candidates[leavingRole.Value].GiveUp = true;
        await SaveAsync(next);
    }

    public Task LeaveRoleAsync(GameSession session) => LeavingAsync(session);

    private DungeonBaseInfoNotify Snapshot()
    {
        var r = current!;
        var result = new DungeonBaseInfoNotify
        {
            DungeonEntry = Dungeon, DungeonId = room!.Id, Level = 1,
            Stage = r.Question + 1, ChangeType = (DungeonBaseInfoNotify.Types.eChangeType)2,
            InitData = new() { RainbowKoiInitData = new() { ActivityKey = ActivityId.ToString(), ActivityInstance = r.Cycle } }
        };
        result.InitData.RainbowKoiInitData.CandidatePlayerIds.Add(r.Candidates.Keys);
        result.InitData.RainbowKoiInitData.QuestionIds.Add(r.Questions.Select(id => checked((int)id)));
        result.Vars.Add(Var(1102, r.Deadline * 1000));
        result.Vars.Add(Var(1103, r.Phase));
        result.Vars.Add(Var(1104, r.Question + 1));
        result.Vars.Add(Var(1105, r.Candidates.Where(c => c.Value.Entered && !c.Value.Eliminated && !c.Value.GiveUp).Select(c => c.Key).ToArray()));
        result.Vars.Add(Var(1106, r.Candidates.Where(c => c.Value.Eliminated || c.Value.GiveUp).Select(c => c.Key).ToArray()));
        result.Vars.Add(Var(1107, r.Questions));
        result.Vars.Add(Var(1108, r.Answers.ToArray()));
        result.Vars.Add(Var(1110, r.Candidates.Count(c => c.Value.Entered && !c.Value.Eliminated && !c.Value.GiveUp)));
        result.Vars.Add(Var(1111, r.Complete ? 2 : r.Phase == 2 ? 1 : 0));
        return result;
    }

    private static DungeonVar Var(int id, params long[] values)
    { var variable = new DungeonVar { VarId = id }; variable.Vi64.Add(values); return variable; }

    public async Task ReconcileAsync()
    {
        var scope = await EnterRoundWithGateAsync("festival/reconcile");
        bool deliver = false;
        try
        {
            await EnsureRoundAsync();
            if (current is { Complete: false } currentRound && Now >= currentRound.AnswerStart)
            {
                var r = Clone(currentRound);
                bool changed = false;
                if (r.Phase == 0)
                {
                    foreach (var c in r.Candidates.Values.Where(c => !c.Entered)) c.GiveUp = true;
                    r.Phase = 1; r.Deadline = r.AnswerStart + 30;
                    changed = true;
                }
                if (Now >= r.End || r.Questions.Length == 0) await CompleteAsync(r);
                else if (Now >= r.Deadline)
                {
                    if (r.Phase == 1) await EvaluateAsync(r);
                    else { r.Question++; r.Phase = 1; r.Deadline = Now + 30; }
                    if (!r.Complete) await SaveAsync(r);
                }
                else if (changed) await SaveAsync(r);
            }
            if (deliverOnline)
            {
                deliverOnline = false;
                deliver = true;
            }
            if (room is { Closed: false } && current != null)
            {
                var snapshot = Snapshot();
                await RoleFanout.EachAsync(room.ActiveMembers
                    .Where(member => member.Session.InstanceId == room.Id && !member.Session.SceneLoading)
                    .Select(member => member.Session),
                    session => session.SendGameAsync(DungeonService.BaseInfo, snapshot));
                if (Now >= current.End)
                    await RoleFanout.EachAsync(room.ActiveMembers.Select(member => member.Session), async session =>
                    {
                        if (session.InstanceId == room.Id) await instances.LeaveAsync(session);
                        else await instances.AbandonAsync(room, session);
                    });
            }
        }
        finally { gate.Release(); scope?.Dispose(); }
        if (deliver)
            await RoleFanout.EachAsync(sessions.All().Where(s => s.Role != null && s.ActivitiesReady && s.DailyReady), DeliverAsync);
        // The 5004 diff is empty unless the projected festival state moved (phase/segment boundary, candidates,
        // settlement); the per-session bet is synced by BetAsync and a login sends its own full projection.
        string projection = ProjectionKey();
        if (projection == lastProjection) return;
        lastProjection = projection;
        // Each session's projection is its own role-owned work: a role that is logging out (not admitting new
        // mutations) is skipped instead of aborting this realm pass for every later session.
        foreach (var session in sessions.All().Where(s => s.Role != null && s.ActivitiesReady && s.DailyReady))
            loop.PostOwned(session, () => daily.SyncEventsAsync(session), "festival projection");
    }

    /// <summary>Everything <see cref="Project"/> reads that is shared by all sessions.</summary>
    private string ProjectionKey()
    {
        var segment = Segment;
        var key = new System.Text.StringBuilder()
            .Append(segment?.State).Append('|').Append(segment?.Start).Append('|').Append(segment?.End).Append('|')
            .Append(current?.Cycle).Append('|').Append(current?.End).Append('|').Append(current?.Complete);
        if (current != null)
            foreach (var (id, c) in current.Candidates)
                key.Append('|').Append(id).Append(c.Eliminated ? 'e' : '-').Append(c.GiveUp ? 'g' : '-').Append(c.Rank);
        return key.ToString();
    }

    private async Task EvaluateAsync(Round r)
    {
        if (r.Question >= r.Questions.Length) { await CompleteAsync(r); return; }
        int answer = data.Row("RainbowKoiQuestionConfig", r.Questions[r.Question]) is { } question
            ? checked((int)GameData.Int(question, "_iQuestionAnswer")) : -1;
        var online = sessions.All().Where(s => s.Role != null && s.ActivitiesReady).ToDictionary(s => s.Role!.Id);
        var wrong = new List<Candidate>();
        foreach (var (id, c) in r.Candidates.Where(c => c.Value.Entered && !c.Value.Eliminated && !c.Value.GiveUp))
        {
            if (!online.TryGetValue(id, out var s) || room == null || s.InstanceId != room.Id || s.SceneLoading)
            { c.GiveUp = true; continue; }
            if (s.Dead || !zones.TryGetValue(answer == 1 ? 27 : 28, out var zone) || !zone.Contains(s.Position)) wrong.Add(c);
        }
        foreach (var c in wrong) c.Eliminated = true;
        int survivors = r.Candidates.Count(c => c.Value.Entered && !c.Value.Eliminated && !c.Value.GiveUp);
        foreach (var c in wrong) c.Rank = Math.Max(2, survivors + 1);
        r.Answers.Add(answer); r.Phase = 2; r.Deadline = Now + 5;
        if (survivors <= 1 || r.Question + 1 >= r.Questions.Length) await CompleteAsync(r);
    }

    private async Task CompleteAsync(Round r)
    {
        if (r.Complete) return;
        var present = room?.ActiveMembers.Where(m => m.Session.Role != null && m.Session.InstanceId == room.Id
            && !m.Session.SceneLoading).Select(m => m.Session.Role!.Id).ToHashSet() ?? [];
        foreach (var (id, c) in r.Candidates.Where(c => !c.Value.Eliminated && !c.Value.GiveUp))
            if (!c.Entered || !present.Contains(id)) c.GiveUp = true;
        foreach (var c in r.Candidates.Values.Where(c => c.Entered && !c.Eliminated && !c.GiveUp)) c.Rank = 1;
        r.Complete = true; r.Phase = 2; r.Deadline = Math.Min(r.End, Now + 20);
        var transaction = accounts.NewTransaction();
        var frozen = new List<(long Role, int Source, long Award, AwardGrant[] Grants)>();
        foreach (var (id, c) in r.Candidates.Where(c => c.Value.Rank > 0 && !c.Value.GiveUp))
        {
            var reward = data.Table("RainbowKoiRewardConfig").Values.Where(x => GameData.Int(x, "_iRank") <= c.Rank)
                .OrderByDescending(x => GameData.Int(x, "_iRank")).FirstOrDefault();
            if (reward.ValueKind == JsonValueKind.Object)
                frozen.Add((id, 0, GameData.Int(reward, "_iAward"),
                    awards.ResolveForProgress(roles.LoadProgress(id), GameData.Int(reward, "_iAward"), 101).Grants.ToArray()));
        }
        // (inferred) The bet panel previews LuckyAwardPreview; a successful prediction receives that award once.
        long betAward = data.Table("PublicParamConfig").TryGetValue("LuckyAwardPreview", out var preview) ? (long)(GameData.Number(preview) ?? 0) : 0;
        foreach (var (id, target) in r.Bets)
            if (betAward > 0 && r.Candidates.TryGetValue(target, out var c) && c.Rank == 1 && !c.GiveUp)
                frozen.Add((id, 1, betAward,
                    awards.ResolveForProgress(roles.LoadProgress(id), betAward, 101).Grants.ToArray()));
        foreach (var row in frozen)
            transaction.Add(new FestivalRewardInsertPlan(r.Cycle, row.Role, row.Source, row.Award,
                JsonSerializer.SerializeToUtf8Bytes(row.Grants)));
        transaction.Add(new FestivalRoundWritePlan(r.Cycle, JsonSerializer.SerializeToUtf8Bytes(r)));
        var catalog = EventEconomy.Catalog(data);
        EconomyAsset Asset(int entry) => catalog.TryAsset(entry, out var asset) ? asset
            : throw new InvalidDataException($"Festival reward item {entry} is not catalogued.");
        var goods = frozen.SelectMany(row => row.Grants.Where(grant => grant.Entry is not (100 or 101))
            .Select(grant => (row.Role, Grant: grant)))
            .Select(value => new EconomyQuantity(Asset(value.Grant.Entry), value.Grant.Count)).ToArray();
        if (goods.Length > 0)
        {
            var deltas = frozen.SelectMany(row => row.Grants.Where(grant => grant.Entry is not (100 or 101))
                .Select(grant => new EventEconomyOwnerDelta(
                    new EconomyOwner(EconomyOwnerKind.Role, row.Role.ToString(CultureInfo.InvariantCulture)),
                    Asset(grant.Entry), grant.Count))).ToArray();
            transaction.WithEventEconomy(new EventEconomyMutation(catalog, Guid.NewGuid().ToString("N"),
                "realm", 0, "event/festival-complete", $"festival/{r.Cycle}/complete",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [], goods, deltas,
                JsonSerializer.SerializeToUtf8Bytes(new { r.Cycle, Rows = frozen.Count })));
        }
        await transaction.CommitAsync(accounts, "festival/complete", _ => current = r);
        deliverOnline = true;
    }

    private async Task DeliverAsync(GameSession session)
    {
        if (session.Role == null) return;
        var pending = await accounts.ReadAsync<List<FestivalPendingReward>>(
            new FestivalReadPlan(FestivalReadKind.PendingRewards, session.Role.Id));
        if (pending.Count == 0) return;
        long roleId = session.Role.Id;
        var keys = new[] { ReservationKey.Role(roleId), ReservationKey.Lifecycle(roleId) }
            .Concat(pending.Select(p => ReservationKey.Entity("festival_reward", $"{p.Cycle}/{roleId}/{p.Source}"))).ToArray();
        if (loop.Owners is { } owners)
            await owners.RunSessionAsync("festival/deliver", keys, session, () => DeliverPendingRowsAsync(session, pending));
        else await DeliverPendingRowsAsync(session, pending);
    }

    private async Task DeliverPendingRowsAsync(GameSession session, List<FestivalPendingReward> pending)
    {
        foreach (var p in pending)
        {
            var grants = JsonSerializer.Deserialize<AwardGrant[]>(p.Grants)
                ?? throw new InvalidDataException("Festival reward has an invalid frozen result.");
            var plan = new AwardPlan(p.Award, 0, grants);
            await character.ApplyAwardAsync(session, plan, persist: tx =>
            {
                tx.Add(new Ro3Emu.Server.Accounts.Persistence.FestivalClaimPlan(p.Cycle, session.Role!.Id, p.Source));
                if (grants.Any(grant => grant.Entry is not (100 or 101)))
                    tx.WithEventEconomy(EventEconomy.ForRole(data, session, "festival-deliver",
                        $"festival/{p.Cycle}/{session.Role.Id}/{p.Source}/deliver", [], [],
                        new { p.Cycle, p.Source, p.Award, Grants = grants }));
            });
        }
    }

    private async Task SaveAsync(Round next)
    {
        var tx = accounts.NewTransaction();
        tx.Add(new FestivalRoundWritePlan(next.Cycle, JsonSerializer.SerializeToUtf8Bytes(next)));
        await tx.CommitAsync(accounts, "festival/round", _ => current = next);
    }

    private static Round Clone(Round source) => JsonSerializer.Deserialize<Round>(JsonSerializer.SerializeToUtf8Bytes(source))!;

    public void Tick(GameTime time)
    {
        if (queued || reconciledOnce && time.Now - lastTick < TimeSpan.FromSeconds(1)) return;
        reconciledOnce = true; // the first reconciliation runs on the loop's first tick (startup), not 1 s later
        lastTick = time.Now; queued = true;
        loop.PostRuntime(async () => { try { await ReconcileAsync(); } finally { queued = false; } });
    }
}
