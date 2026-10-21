using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// MatchConfig type 2 five-person dungeon queue. LC_MatchServer sends 82681 with the selected scene,
/// multi dungeon and mark; LC_DAL_Match drives the separate 82636/82637 ready panel. The ordinary
/// DungeonService owns admission, 70010/30005 and encounter settlement after all votes.
/// </summary>
public sealed class DungeonMatchService : IGameTicker
{
    public const uint StartMatch = 82681, CancelMatch = 82682, Ready = 82637, ReadyNotify = 82636,
        Reminder = 82634, ReminderNotify = 82635, MatchStartNotify = 82638, CancelNotify = 82639, FailNotify = 82643;

    private readonly GameData _data;
    private readonly DungeonCatalog _catalog;
    private readonly DungeonService _dungeons;
    private readonly TeamService _teams;
    private readonly TimedBattleService _timedBattles;
    private readonly SessionRegistry _sessions;
    private readonly TimeProvider _time;
    private readonly GameLoop _loop;
    private readonly ILogger<DungeonMatchService> _log;

    private readonly record struct Selection(int Entry, long Scene, int Multi, int Mark);

    private sealed class Group
    {
        public required Selection Key;
        public required DungeonCatalog.Plan Plan;
        public required GameSession[] Members;
        public required long[] Ids;
        public required long Leader;
        public required long TeamId;
        public required MatchData Data;
        public required long StartSeconds;
    }

    private sealed class Match
    {
        public required Selection Key;
        public required DungeonCatalog.Plan Plan;
        public required Group[] Groups;
        public required GameSession[] Members;
        public required MatchData Data;
        public required long DeadlineMs;
        public required long StartSeconds;
        public HashSet<GameSession> Ready { get; } = new();
        public bool Closed;
    }

    private readonly Dictionary<Selection, List<Group>> _queue = new();
    private readonly Dictionary<GameSession, Group> _waiting = new();
    private readonly Dictionary<GameSession, Match> _preparing = new();
    private long _lastSecond = -1;

    public DungeonMatchService(GameData data, DungeonCatalog catalog, DungeonService dungeons, TeamService teams,
        TimedBattleService timedBattles, SessionRegistry sessions, TimeProvider time, GameLoop loop,
        ILogger<DungeonMatchService> log)
    {
        this._data = data; this._catalog = catalog; this._dungeons = dungeons; this._teams = teams;
        this._timedBattles = timedBattles; this._sessions = sessions; this._time = time; this._loop = loop; this._log = log;
        timedBattles.OtherMatchBusy = Owns;
    }

    public bool WantsEntry(int entry) => _data.Row("MatchConfig", entry) is { } row
        && GameData.Int(row, "_iMatchType") == 2;

    public bool Owns(GameSession s) => _waiting.ContainsKey(s) || _preparing.ContainsKey(s);

    private (int Error, Selection Key, DungeonCatalog.Plan? Plan) Select(int entry, MatchData? request)
    {
        if (!WantsEntry(entry) || request?.MultiDungeon is not { } selected
            || request.Chicken != null || request.MatchEntryId != 0 && request.MatchEntryId != entry
            || selected.DungeonId <= 0 || selected.MultiDungeonId is < 1 or > int.MaxValue
            || selected.MarkId is < 1 or > int.MaxValue) return (ErrorCodes.Parameter, default, null);
        // DungeonCatalog verifies the exported boss -> scene relation, map, waves and current playbook.
        var plan = _catalog.Resolve(new EnterDungeonReq
        {
            SceneEntry = selected.DungeonId,
            InitData = new DungeonInitData
            {
                MultiDungeonInitData = new MultiDungeonInitData
                { MultiDungeonId = (int)selected.MultiDungeonId, MarkId = (int)selected.MarkId }
            }
        });
        if (plan?.Boss is not { } boss || plan.Multi == null
            || GameData.Int(boss, "_iMatchID") != entry) return (ErrorCodes.DungeonDataMissing, default, null);
        int quantity = (int)GameData.Int(_data.Row("MatchConfig", entry)!.Value, "_iStartQuantity");
        if (quantity < plan.MinPlayers || quantity > plan.MaxPlayers || quantity > 5)
            return (ErrorCodes.DungeonDataMissing, default, null);
        return (0, new Selection(entry, selected.DungeonId, (int)selected.MultiDungeonId, (int)selected.MarkId), plan);
    }

    private int Admission(GameSession s, DungeonCatalog.Plan plan, bool alreadyOwned = false)
    {
        if (s.Role == null || !_sessions.All().Contains(s)) return ErrorCodes.PlayerOffline;
        if (_timedBattles.IsMatching(s) || !alreadyOwned && Owns(s))
            return ErrorCodes.DungeonBusy;
        return _dungeons.MatchAdmission(s, plan);
    }

    private int GroupAdmission(Group group)
    {
        if (!_teams.MatchGroupValid(group.Leader, group.TeamId, group.Ids)) return ErrorCodes.MemberLeft;
        foreach (var member in group.Members)
        {
            if (member.Role == null || !group.Ids.Contains(member.Role.Id)) return ErrorCodes.PlayerOffline;
            int error = Admission(member, group.Plan, alreadyOwned: true);
            if (error != 0) return error;
        }
        return 0;
    }

    /// <summary>82681 enqueues the complete current party; the leader's selection is server verified.</summary>
    public async Task StartMatchAsync(GameSession s, StartMatchReq req)
    {
        var selected = Select(req.MatchEntryId, req.MatchData);
        var group = _teams.MatchGroup(s);
        int quantity = selected.Plan == null ? 0 : (int)GameData.Int(_data.Row("MatchConfig", req.MatchEntryId)!.Value, "_iStartQuantity");
        int error = req.OperateId != 0 ? ErrorCodes.Parameter
            : selected.Error != 0 ? selected.Error
            : group.Error != 0 ? group.Error
            : group.Members.Length > quantity ? ErrorCodes.PartyFull : 0;
        if (error == 0)
            foreach (var member in group.Members)
            { error = Admission(member, selected.Plan!); if (error != 0) break; }
        if (error != 0)
        {
            await s.SendGameAsync(StartMatch, new StartMatchResp
            {
                Result = error, MatchEntryId = req.MatchEntryId,
                MatchData = req.MatchData?.Clone()
            });
            return;
        }
        // LC_DAL_Match.ResetMatchTimer compares 82681 matchStartTime with TimerManager.GetSystemTime (seconds).
        long startSeconds = _time.GetUtcNow().ToUnixTimeSeconds();
        var item = new Group
        {
            Key = selected.Key, Plan = selected.Plan!, Members = group.Members,
            Ids = group.Members.Select(m => m.Role!.Id).ToArray(), Leader = s.Role!.Id,
            TeamId = group.TeamId, Data = req.MatchData!.Clone(), StartSeconds = startSeconds
        };
        if (!_queue.TryGetValue(item.Key, out var list)) _queue[item.Key] = list = [];
        list.Add(item);
        foreach (var member in item.Members) _waiting[member] = item;
        foreach (var member in item.Members)
            await SendQuietAsync(member, StartMatch, new StartMatchResp
            {
                MatchEntryId = req.MatchEntryId,
                MatchData = item.Data.Clone(), MatchStartTime = startSeconds
            });
        if (item.Members.Length == 1 && s.DungeonSoloMatch)
        {
            list.Remove(item);
            if (list.Count == 0) _queue.Remove(item.Key);
            _waiting.Remove(s);
            await PrepareAsync([item]);
            return;
        }
        await FormAsync(item.Key);
    }

    private async Task FormAsync(Selection key)
    {
        if (!_queue.TryGetValue(key, out var list)) return;
        foreach (var group in list.ToArray())
            if (GroupAdmission(group) != 0) await RemoveGroupAsync(group, manual: false);
        if (!_queue.TryGetValue(key, out list)) return;
        int quantity = (int)GameData.Int(_data.Row("MatchConfig", key.Entry)!.Value, "_iStartQuantity");
        if (quantity is < 1 or > 5) return;
        while (list.Count > 0)
        {
            var choice = PickGroupIndices(list.Select(g => g.Members.Length).ToArray(), quantity);
            if (choice == null) break;
            var picked = choice.Select(i => list[i]).ToArray();
            foreach (var group in picked)
            {
                list.Remove(group);
                foreach (var member in group.Members) _waiting.Remove(member);
            }
            if (list.Count == 0) _queue.Remove(key);
            await PrepareAsync(picked);
            if (!_queue.TryGetValue(key, out list)) break;
        }
    }

    /// <summary>Earliest feasible whole-party subset (inferred FIFO); a greedy 4 prefix must not strand 3+2.</summary>
    internal static IReadOnlyList<int>? PickGroupIndices(IReadOnlyList<int> sizes, int quantity)
    {
        if (quantity is < 1 or > 5) return null;
        var ways = new List<int>?[quantity + 1];
        ways[0] = [];
        for (int i = 0; i < sizes.Count; i++)
        {
            int size = sizes[i];
            if (size is < 1 or > 5 || size > quantity) continue;
            for (int count = quantity - size; count >= 0; count--)
                if (ways[count] != null && ways[count + size] == null)
                    ways[count + size] = [.. ways[count]!, i];
        }
        return ways[quantity];
    }

    private async Task PrepareAsync(IReadOnlyList<Group> picked)
    {
        var first = picked[0];
        int seconds = ReadySeconds();
        long nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        // The client receives whole Unix seconds. Make that same boundary authoritative for votes and expiry.
        long deadlineMs = ((nowMs + seconds * 1000L + 999) / 1000) * 1000;
        var match = new Match
        {
            Key = first.Key, Plan = first.Plan, Groups = picked.ToArray(),
            Members = picked.SelectMany(g => g.Members).ToArray(), Data = first.Data,
            DeadlineMs = deadlineMs, StartSeconds = picked.Min(g => g.StartSeconds)
        };
        foreach (var member in match.Members) _preparing[member] = match;
        await BroadcastReadyAsync(match);
        _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(Math.Max(0,
            match.DeadlineMs - _time.GetUtcNow().ToUnixTimeMilliseconds())), () => ExpireAsync(match));
    }

    private int ReadySeconds() => _data.Table("PublicParamConfig").TryGetValue("GamePlayAssembleMaxTime", out var row)
        ? (int)Math.Clamp(GameData.Number(row) ?? 30, 1, 300) : 30;

    private async Task BroadcastReadyAsync(Match match)
    {
        if (match.Closed) return;
        // UI_Sys_Dungeon_PreparePanelCtrl subtracts TimerManager.GetSystemTime (seconds) from 82636 endTime.
        var notify = new MatchReadyNotify
        {
            MatchEntryId = match.Key.Entry,
            EndTime = match.DeadlineMs / 1000,
            DungeonId = match.Key.Scene, MultiDungeonId = match.Key.Multi, MarkId = match.Key.Mark
        };
        foreach (var member in match.Members)
            notify.ReadyInfos.Add(new MatchRoleReadyInfo
            {
                PlayerId = member.Role!.Id,
                Status = match.Ready.Contains(member) ? MatchReadyStatus.Ready : MatchReadyStatus.None
            });
        foreach (var member in match.Members) await SendQuietAsync(member, ReadyNotify, notify);
    }

    public async Task ReadyAsync(GameSession s, MatchReadyReq req)
    {
        var match = _preparing.GetValueOrDefault(s);
        bool expired = match != null && _time.GetUtcNow().ToUnixTimeMilliseconds() >= match.DeadlineMs;
        int error = match == null || match.Closed || expired || match.Key.Entry != req.MatchEntryId
            || s.Role?.Id != req.PlayerId ? ErrorCodes.Parameter
            : !req.IsReady ? ErrorCodes.DungeonDeclined : 0;
        await s.SendGameAsync(Ready, new MatchReadyResp
        {
            Result = error, MatchEntryId = req.MatchEntryId,
            PlayerId = s.Role?.Id ?? 0, IsReady = req.IsReady
        });
        if (expired) { await ExpireAsync(match!); return; }
        if (match == null || match.Closed || error == ErrorCodes.Parameter) return;
        if (error != 0) { await FailReadyAsync(match, s); return; }
        match.Ready.Add(s);
        await BroadcastReadyAsync(match);
        if (match.Ready.Count != match.Members.Length) return;
        foreach (var group in match.Groups)
        {
            error = GroupAdmission(group);
            if (error != 0) { await CancelReadyAsync(match, false); return; }
        }
        match.Closed = true;
        foreach (var member in match.Members) _preparing.Remove(member);
        long teamId = match.Members.Length == 1 ? match.Groups[0].TeamId : 0;
        if (match.Members.Length > 1)
            teamId = await _teams.CreateMatchedPartyAsync(match.Members);
        if (match.Members.Length > 1 && teamId == 0) error = ErrorCodes.MemberLeft;
        if (error == 0)
        {
            try { error = await _dungeons.BeginMatchedAsync(match.Plan, match.Members, teamId); }
            catch (Exception e)
            {
                _log.LogWarning(e, "Matched dungeon handoff failed for {Scene}", match.Key.Scene);
                error = ErrorCodes.DungeonBusy;
            }
        }
        if (error != 0)
        {
            foreach (var member in match.Members) await SendQuietAsync(member, CancelNotify,
                new CancelMatchNotify { MatchEntryId = match.Key.Entry });
            return;
        }
        foreach (var member in match.Members) await SendQuietAsync(member, MatchStartNotify,
            new StartMatchNotify { MatchEntryId = match.Key.Entry, MatchData = match.Data.Clone() });
    }

    public async Task RemindAsync(GameSession s, MatchReadyReminderReq req)
    {
        var match = _preparing.GetValueOrDefault(s);
        var target = match?.Members.FirstOrDefault(m => m.Role?.Id == req.RemindedPlayerId);
        bool expired = match != null && _time.GetUtcNow().ToUnixTimeMilliseconds() >= match.DeadlineMs;
        int error = match == null || match.Closed || expired || match.Key.Entry != req.MatchEntryId
            || s.Role?.Id != req.PlayerId || target == null || target == s || match.Ready.Contains(target)
            ? ErrorCodes.Parameter : 0;
        await s.SendGameAsync(Reminder, new MatchReadyReminderResp
        {
            Result = error, MatchEntryId = req.MatchEntryId,
            PlayerId = s.Role?.Id ?? 0, RemindedPlayerId = req.RemindedPlayerId
        });
        if (expired) { await ExpireAsync(match!); return; }
        if (error == 0) await SendQuietAsync(target!, ReminderNotify,
            new MatchReadyReminderNotify { MatchEntryId = req.MatchEntryId, ReminderPlayerId = s.Role!.Id });
    }

    public async Task CancelMatchAsync(GameSession s, CancelMatchReq req)
    {
        var group = _waiting.GetValueOrDefault(s);
        var match = _preparing.GetValueOrDefault(s);
        int error = req.OperateId != 0 || (group == null || group.Key.Entry != req.MatchEntryId)
            && (match == null || match.Key.Entry != req.MatchEntryId) ? ErrorCodes.Parameter : 0;
        await s.SendGameAsync(CancelMatch, new CancelMatchResp { Result = error, MatchEntryId = req.MatchEntryId });
        if (error != 0) return;
        if (group != null) await RemoveGroupAsync(group, manual: true);
        else if (match != null) await CancelReadyAsync(match, true);
        // LC_MatchServer.lua:348-423 sends nextMatchEntry/nextMatchData when changing an active search.
        // The DAL cancel response clears its current state; a new 82681 response starts the next search.
        if (req.NextMatchEntry != 0)
        {
            var next = new StartMatchReq
            {
                MatchEntryId = req.NextMatchEntry,
                MatchData = req.NextMatchData?.Clone()
            };
            if (WantsEntry(next.MatchEntryId) || next.MatchData?.MultiDungeon != null)
                await StartMatchAsync(s, next);
            else await _timedBattles.StartMatchAsync(s, next);
        }
    }

    private async Task RemoveGroupAsync(Group group, bool manual, GameSession? departed = null)
    {
        if (!_queue.TryGetValue(group.Key, out var list) || !list.Remove(group)) return;
        if (list.Count == 0) _queue.Remove(group.Key);
        foreach (var member in group.Members)
        {
            _waiting.Remove(member);
            if (member != departed) await SendQuietAsync(member, CancelNotify,
                new CancelMatchNotify { MatchEntryId = group.Key.Entry, ManualCancel = manual });
        }
    }

    /// <summary>The ready check ran out: the first member who had not confirmed failed it (inferred, see FailReadyAsync).</summary>
    private Task ExpireAsync(Match match)
    {
        if (match.Closed || _time.GetUtcNow().ToUnixTimeMilliseconds() < match.DeadlineMs) return Task.CompletedTask;
        var late = match.Members.FirstOrDefault(m => !match.Ready.Contains(m));
        return late == null ? CancelReadyAsync(match, false) : FailReadyAsync(match, late);
    }

    /// <summary>
    /// 82643 FailMatchNtf: a member refused the ready check (82637 isReady false) or let it run out. LC_DAL_Match
    /// ._failMatchNtfHandler (Logic/Asset/LC_DAL_Match.lua:221-353) closes UI_Sys_Dungeon_PreparePanel, clears the match
    /// state and stores penaltyMemberGuid / penaltyMemberUnlockTs as PenaltyTimeData, which the Survivor Battle and Dragon
    /// Coffin panels show as a lockout ("{name} ... M:S", UIWords 63021); unlike 82639 it pops no tip. The member who
    /// did not confirm is the penalty member (MatchPunishReason ReadyRefuse / AFK). (inferred) No lockout: the export
    /// carries no refusal durations for MatchConfig 100 (MatchRefuseConfig rows hold ids only), so the unlock time is 0.
    /// </summary>
    private async Task FailReadyAsync(Match match, GameSession failed)
    {
        if (match.Closed) return;
        match.Closed = true;
        long failedId = failed.Role?.Id ?? 0;
        var notify = new FailMatchNtf
        {
            Result = ErrorCodes.DungeonDeclined, MatchEntryId = match.Key.Entry,
            PenaltyMemberGuid = failedId, FailPlayer = failedId, MatchData = match.Data.Clone(),
            MatchStartTime = match.StartSeconds
        };
        foreach (var member in match.Members)
        {
            _preparing.Remove(member);
            await SendQuietAsync(member, FailNotify, notify);
        }
    }

    private async Task CancelReadyAsync(Match match, bool manual, GameSession? departed = null)
    {
        if (match.Closed) return;
        match.Closed = true;
        foreach (var member in match.Members)
        {
            _preparing.Remove(member);
            if (member != departed) await SendQuietAsync(member, CancelNotify,
                new CancelMatchNotify { MatchEntryId = match.Key.Entry, ManualCancel = manual });
        }
    }

    public async Task LeaveRoleAsync(GameSession s, bool notifySelf = false)
    {
        if (_waiting.TryGetValue(s, out var group)) await RemoveGroupAsync(group, false, notifySelf ? null : s);
        if (_preparing.TryGetValue(s, out var match)) await CancelReadyAsync(match, false, notifySelf ? null : s);
    }

    public void Tick(GameTime _)
    {
        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        if (now == _lastSecond) return;
        _lastSecond = now;
        foreach (var group in _waiting.Values.Distinct().ToArray())
            if (GroupAdmission(group) != 0) _loop.PostLegacy(() => RemoveGroupAsync(group, false));
        foreach (var match in _preparing.Values.Distinct().ToArray())
            if (match.Groups.Any(g => GroupAdmission(g) != 0)) _loop.PostLegacy(() => CancelReadyAsync(match, false));
            else if (_time.GetUtcNow().ToUnixTimeMilliseconds() >= match.DeadlineMs) _loop.PostLegacy(() => ExpireAsync(match));
    }

    private async Task SendQuietAsync(GameSession s, uint id, Google.Protobuf.IMessage body)
    {
        try { await s.SendGameAsync(id, body); }
        catch (Exception e) { _log.LogWarning(e, "Match notification {Message} failed for {Role}", id, s.Role?.Id); }
    }
}
