using Google.Protobuf;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Parties: one group system on the game gate, TeamTypeFive (party, 5) and TeamTypeForty (team,
/// 40). Create 42000 → 42026 (self: what puts the client "in" the party) → 42048 TEAM_ALL; invite 42004 → 42005 to the
/// invitee; accept 42006 → 42026/42048 to the joiner, 42026 to the others; leave 42001 / 42037; kick and leader change
/// 42007 / 42008 / 42050. A 2 s ticker keeps the member cards live: 42050 when a member's map, level, job or online state
/// changes, 42051 when HP, SP, position or life state does. Parties live in memory (a server restart dissolves them).
/// </summary>
public sealed partial class TeamService(SessionRegistry sessions, CharacterService character, ChatService chat, RoleStore roles, FriendStore friends,
    GameData data, SeasonService seasons, IOptions<ServerOptions> options, GameLoop loop, ILogger<TeamService> log) : IGameTicker
{
    /// <summary>DungeonService supplies the authoritative role preference, allowances and Light of Bonds projection.</summary>
    public Func<GameSession, IReadOnlyList<long>, TeamMultiDungeonInfo>? MultiDungeonInfo { get; set; }
    public const uint TeamCreate = 42000, TeamLeave = 42001, TeamInvite = 42004, TeamNotifyInvitee = 42005, TeamInviteeResp = 42006,
        TeamOperate = 42007, TeamOperateBroadcast = 42008, TeamInviteList = 42017, TeamNewMember = 42026, TeamChangeMemberState = 42036,
        TeamBroadcastLeave = 42037, TeamAll = 42048, TeamMemberUpdate = 42050, TeamMemberBattleUpdate = 42051;

    /// <summary>PublicParamConfig.NoTargetID: a party without an activity target.</summary>
    private const long NoTarget = 1001;

    /// <summary>PublicParamConfig.TeamInviteLastTime (s): the client drops an invite after this too.</summary>
    private static readonly TimeSpan InviteLife = TimeSpan.FromSeconds(30);

    /// <summary>PublicParamConfig.TeamExpBuffNum (‱) by the number of members on the same map.</summary>
    private static readonly int[] ExpBuff = [10000, 12500, 15000, 17500, 20000];

    private const int MaxHp = 101, MaxSp = 102;

    private sealed class Team
    {
        public long Id;
        public TeamType Type;
        public long Leader;
        public long Target = NoTarget;
        public long Created;
        public long MinBattlePoint;
        public string Description = "", Name = "", Area = "";
        public bool AutoAgree;
        public long PlayBook;
        public readonly Dictionary<int, TeamFlagInfo> Flags = new();
        public readonly Dictionary<long, long> Applicants = new();
        /// <summary>Guild League elites a quick elite team admits without the leader's approval (TeamService.Elite).</summary>
        public readonly HashSet<long> Elites = new();
        public readonly HashSet<long> ViceLeaders = new();
        public readonly Dictionary<long, TeamMemberOnlineState> OnlineStates = new();
        public readonly Dictionary<long, TeamMemberChatState> ChatStates = new();
        public readonly HashSet<long> Muted = new(), Following = new();
        public readonly Dictionary<long, (RallyTeamType Type, DateTimeOffset Expires)> Rallies = new();
        public readonly List<long> Members = new(); // slot order = teamIndex
        public readonly Dictionary<long, long> Joined = new();
        public readonly Dictionary<long, string> LastCard = new();
        public readonly Dictionary<long, string> LastBattle = new();
    }

    private readonly SessionRegistry _sessions = sessions;
    private readonly CharacterService _character = character;
    private readonly ChatService _chat = chat;
    private readonly RoleStore _roles = roles;
    private readonly FriendStore _friends = friends;
    private readonly GameData _data = data;
    private readonly SeasonService _seasons = seasons;
    private readonly IOptions<ServerOptions> _options = options;
    private readonly ILogger<TeamService> _log = log;
    private readonly GameLoop _loop = loop;
    private readonly Dictionary<long, Team> _teams = new();
    private readonly Dictionary<long, Team> _byMember = new();
    private readonly Dictionary<(long Target, long Team), DateTime> _invites = new();
    private long _nextId = 1L << 24;
    private TimeSpan _sinceUpdate;

    /// <summary>Party EXP multiplier for a kill (‱ of TeamExpBuffNum by same-map members; the split rule itself is inferred).</summary>
    public int ExpBonus(GameSession killer)
    {
        if (killer.Role is not { } role || !_byMember.TryGetValue(role.Id, out var team)) return 10000;
        int sameMap = team.Members.Count(m => Session(m) is { } s && s.WorldScene == killer.WorldScene);
        return ExpBuff[Math.Clamp(sameMap, 1, ExpBuff.Length) - 1];
    }

    /// <summary>After a party's roster or a member's card (career, online state) changed, with the roles it concerns: the
    /// members and one who left (PassiveSkills: Union).</summary>
    internal List<Func<IReadOnlyList<long>, Task>> RosterChanged { get; } = [];

    private async Task NotifyRosterAsync(IReadOnlyList<long> roles)
    {
        foreach (var handler in RosterChanged) await handler(roles);
    }

    /// <summary>The party a role is in: (team id, leader), or (0, the role itself) when solo.</summary>
    public (long TeamId, long Leader) TeamOf(long role) =>
        _byMember.TryGetValue(role, out var team) ? (team.Id, team.Leader) : (0, role);

    /// <summary>Authoritative roster, including offline members; dungeon admission must not silently omit them.</summary>
    public IReadOnlyList<long> MembersOf(long role) =>
        _byMember.TryGetValue(role, out var team) ? team.Members.ToArray() : [role];

    /// <summary>The leader's forbidden-chat restriction on a member (TeamChangeMemberState, team.Muted); used by the
    /// voice relay to silence a muted member's microphone the same way text chat is silenced.</summary>
    public bool IsForbiddenChat(long role) =>
        _byMember.TryGetValue(role, out var team) && team.Muted.Contains(role);

    public Task CreateAsync(GameSession s, TeamCreateReq req) =>
        RunTeamAsync(s, "team/create", () => CreateCoreAsync(s, req));

    private async Task CreateCoreAsync(GameSession s, TeamCreateReq req)
    {
        long self = s.Role!.Id;
        if (_byMember.ContainsKey(self))
        {
            await s.SendGameAsync(TeamCreate, new TeamCreateResp { Result = ErrorCodes.AlreadyInParty });
            return;
        }
        if (req.TeamType is not (TeamType.Five or TeamType.Forty) || !ValidTarget(req.TargetId, req.TeamType))
        {
            await s.SendGameAsync(TeamCreate, new TeamCreateResp { Result = ErrorCodes.Parameter });
            return;
        }
        var team = new Team
        {
            Id = ++_nextId, Type = req.TeamType == TeamType.Forty ? TeamType.Forty : TeamType.Five, Leader = self,
            Target = req.TargetId != 0 ? req.TargetId : NoTarget, Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Description = req.TeamDesc, Area = req.TeamAreaInfo.Length > 0 ? req.TeamAreaInfo : _options.Value.ClusterId.ToString(),
            MinBattlePoint = req.LimitScore, PlayBook = req.PlayBookId != 0 ? req.PlayBookId : _seasons.PlayBook,
        };
        _teams[team.Id] = team;
        await RecordAndJoinAsync(team, self);
        _log.LogInformation("{Name} creates party {Team} ({Type})", s.Role.Name, team.Id, team.Type);
        await s.SendGameAsync(TeamCreate, new TeamCreateResp { Result = 0, TeamId = team.Id, TeamType = team.Type });
        await s.SendGameAsync(TeamNewMember, new BroadCastNewMemberEnter { TeamType = team.Type, DetailInfo = Detail(team, self) });
        await s.SendGameAsync(TeamAll, All(team));
    }

    public Task InviteAsync(GameSession s, InviteIntoTeamReq req) =>
        RunTeamAsync(s, "team/invite", () => InviteCoreAsync(s, req), extraRole: req.Guid);

    private async Task InviteCoreAsync(GameSession s, InviteIntoTeamReq req)
    {
        long self = s.Role!.Id;
        var target = Session(req.Guid);
        // Codes as (inferred from their texts).
        int code = !_byMember.TryGetValue(self, out var team) ? ErrorCodes.NotInParty
            : target == null ? ErrorCodes.PlayerOffline
            : _byMember.ContainsKey(req.Guid) ? ErrorCodes.InviteeInParty
            : team!.Members.Count >= Capacity(team) ? ErrorCodes.PartyFull : 0;
        bool ok = code == 0;
        await s.SendGameAsync(TeamInvite, new InviteIntoTeamResp { Result = code });
        _log.LogInformation("{Name} invites {Target} to party: {Result}", s.Role.Name, req.Guid, ok ? "ok" : "refused");
        if (!ok) return;
        DateTime expires = DateTime.UtcNow + InviteLife;
        _invites[(req.Guid, team!.Id)] = expires;
        long invitedTeam = team.Id;
        _loop.ScheduleRuntime(InviteLife, () => RunTeamAsync("team/invite-expiry", [req.Guid], invitedTeam, () =>
        {
            if (_invites.TryGetValue((req.Guid, invitedTeam), out var current) && current == expires
                && current <= DateTime.UtcNow) _invites.Remove((req.Guid, invitedTeam));
            return Task.CompletedTask;
        }));
        var simple = new TeamTotalSimpleInfo { Info = Simple(team) };
        simple.MemberList.Add(team.Members); // the invite cell finds the inviter's slot by uid
        await target!.SendGameAsync(TeamNotifyInvitee, new NotifyInvitee { InviteGuid = self, TeamTotalSimpleInfo = simple });
    }

    public Task InviteeRespAsync(GameSession s, InviteeResp req) =>
        RunTeamAsync(s, "team/invite-response", () => InviteeRespCoreAsync(s, req), teamId: req.TeamId);

    private async Task InviteeRespCoreAsync(GameSession s, InviteeResp req)
    {
        long self = s.Role!.Id;
        bool invited = _invites.TryGetValue((self, req.TeamId), out var expires) && expires > DateTime.UtcNow;
        if (!req.IsAgree)
        {
            _invites.Remove((self, req.TeamId));
            await s.SendGameAsync(TeamInviteeResp, new InviteeRespResp { Result = 0, TeamId = req.TeamId });
            return;
        }
        // An absent / expired invitation has no recovered code: Parameter.
        Team? team = null;
        int code = !invited ? ErrorCodes.Parameter
            : !_teams.TryGetValue(req.TeamId, out team) ? ErrorCodes.PartyMissing
            : _byMember.ContainsKey(self) ? ErrorCodes.AlreadyInParty
            : team!.Members.Count >= Capacity(team) ? ErrorCodes.PartyFull : 0;
        bool ok = code == 0;
        if (ok) await RecordAndJoinAsync(team!, self);
        _invites.Remove((self, req.TeamId));
        await s.SendGameAsync(TeamInviteeResp, new InviteeRespResp { Result = code, TeamId = req.TeamId });
        if (!ok) return;
        _log.LogInformation("{Name} joins party {Team}", s.Role.Name, team!.Id);
        await PublishAdmissionAsync(team, self);
    }

    public Task LeaveAsync(GameSession s) =>
        RunTeamAsync(s, "team/leave", () => LeaveCoreAsync(s));

    private async Task LeaveCoreAsync(GameSession s)
    {
        long self = s.Role!.Id;
        bool ok = _byMember.ContainsKey(self);
        await s.SendGameAsync(TeamLeave, new LeaveTeamResp { Result = ok ? 0 : ErrorCodes.NotInParty });
        if (ok) await RemoveAsync(_byMember[self], self, leaveType: 0);
    }

    public Task OperateAsync(GameSession s, TeamOperateReq req) =>
        RunTeamAsync(s, "team/operate", () => OperateCoreAsync(s, req), extraRole: req.TargetGuid);

    private async Task OperateCoreAsync(GameSession s, TeamOperateReq req)
    {
        long self = s.Role!.Id;
        int code = !_byMember.TryGetValue(self, out var team) ? ErrorCodes.NotInParty
            : team!.Leader != self ? ErrorCodes.NoPermission
            : req.TargetGuid == self || req.Oper is not (TeamOperationType.TeamOperTypeKick or TeamOperationType.TeamOperTypeChangeLeader or TeamOperationType.TeamOperTypeAppointViceLeader or TeamOperationType.TeamOperTypeRemovalViceLeader) ? ErrorCodes.Parameter
            : !team.Members.Contains(req.TargetGuid) ? ErrorCodes.MemberLeft
            : req.Oper == TeamOperationType.TeamOperTypeAppointViceLeader && team.ViceLeaders.Count >= 7 && !team.ViceLeaders.Contains(req.TargetGuid) ? ErrorCodes.PartyFull
            : req.Oper == TeamOperationType.TeamOperTypeRemovalViceLeader && !team.ViceLeaders.Contains(req.TargetGuid) ? ErrorCodes.Parameter : 0;
        bool ok = code == 0;
        await s.SendGameAsync(TeamOperate, new TeamOperateResp { Result = code, Oper = req.Oper, TargetGuid = req.TargetGuid });
        if (!ok) return;
        if (req.Oper == TeamOperationType.TeamOperTypeKick)
        {
            if (Session(req.TargetGuid) is { } kicked)
                await kicked.SendGameAsync(TeamOperateBroadcast, new TeamOperateNotify { Oper = req.Oper, TargetGuid = req.TargetGuid });
            await RemoveAsync(team!, req.TargetGuid, leaveType: 1, notifyLeaver: false);
        }
        else if (req.Oper == TeamOperationType.TeamOperTypeChangeLeader)
        {
            await ChangeLeaderAsync(team!, req.TargetGuid);
        }
        else
        {
            if (req.Oper == TeamOperationType.TeamOperTypeAppointViceLeader) team!.ViceLeaders.Add(req.TargetGuid);
            else team!.ViceLeaders.Remove(req.TargetGuid);
            await ToOthersAsync(team!, 0, TeamOperateBroadcast, new TeamOperateNotify { Oper = req.Oper, TargetGuid = req.TargetGuid });
            var update = new TeamMemberChangeResp { TeamId = team!.Id };
            update.MemberList[req.TargetGuid] = Detail(team, req.TargetGuid);
            await ToOthersAsync(team, 0, TeamMemberUpdate, update);
        }
    }

    /// <summary>Game login of a member: the party comes back (42048), the others see it online again (via the ticker).</summary>
    public async Task OnEnteredAsync(GameSession s)
    {
        if (s.Role is { } role && _byMember.TryGetValue(role.Id, out var team)) await s.SendGameAsync(TeamAll, All(team));
    }

    // ---- membership ----

    private void Join(Team team, long role)
    {
        team.Applicants.Remove(role);
        team.Members.Add(role);
        team.Joined[role] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _byMember[role] = team;
    }

    private async Task RemoveAsync(Team team, long role, int leaveType, bool notifyLeaver = true)
    {
        string name = Session(role)?.Role?.Name ?? _roles.Get(role)?.Name ?? "";
        team.Members.Remove(role);
        team.Applicants.Remove(role);
        team.ViceLeaders.Remove(role);
        team.OnlineStates.Remove(role);
        team.ChatStates.Remove(role);
        team.Muted.Remove(role);
        team.Following.Remove(role);
        team.Joined.Remove(role);
        team.LastCard.Remove(role);
        team.LastBattle.Remove(role);
        _byMember.Remove(role);
        _log.LogInformation("{Name} leaves party {Team} ({Left} left)", name, team.Id, team.Members.Count);
        await NotifyRosterAsync([.. team.Members, role]);
        if (team.Members.Count == 0)
        {
            _teams.Remove(team.Id);
            return;
        }
        if (team.Leader == role) await ChangeLeaderAsync(team, team.Members[0]);
        var leave = new BroadCastMemberLeave { TeamId = team.Id, LeaveType = (ELeaveType)leaveType, Guid = role, Name = name };
        for (int i = 0; i < team.Members.Count; i++) leave.MemberIndex[team.Members[i]] = i;
        await ToOthersAsync(team, role, TeamBroadcastLeave, leave);
    }

    /// <summary>42008 to everyone first (its tip reads the current leader's name), then the two changed cards (42050).</summary>
    private async Task ChangeLeaderAsync(Team team, long next)
    {
        long previous = team.Leader;
        team.Leader = next;
        team.ViceLeaders.Remove(next);
        await ToOthersAsync(team, 0, TeamOperateBroadcast, new TeamOperateNotify { Oper = TeamOperationType.TeamOperTypeChangeLeader, TargetGuid = next });
        var update = new TeamMemberChangeResp { TeamId = team.Id };
        foreach (var m in new[] { previous, next }.Where(team.Members.Contains)) update.MemberList[m] = Detail(team, m);
        await ToOthersAsync(team, 0, TeamMemberUpdate, update);
    }

    // ---- live member cards ----

    public void Tick(GameTime time)
    {
        _sinceUpdate += time.Delta;
        if (_sinceUpdate < TimeSpan.FromSeconds(2)) return;
        _sinceUpdate = TimeSpan.Zero;
        foreach (var team in _teams.Values.ToList())
            _loop.TrackRuntimeDetached("team refresh", () => RunTeamAsync("team/refresh", team.Members.ToArray(),
                team.Id, () => RefreshAsync(team)));
    }

    private async Task RefreshAsync(Team team)
    {
        if (!_teams.TryGetValue(team.Id, out var current) || !ReferenceEquals(team, current)) return;
        bool cardChanged = false;
        foreach (var m in team.Members.ToList())
        {
            var detail = Detail(team, m);
            var battle = detail.BattleInfo;
            detail.BattleInfo = null;
            string card = detail.ToString(), fight = battle.ToString();
            if (team.LastCard.GetValueOrDefault(m) != card)
            {
                team.LastCard[m] = card;
                cardChanged = true;
                detail.BattleInfo = battle;
                var update = new TeamMemberChangeResp { TeamId = team.Id };
                update.MemberList[m] = detail;
                // The owner also consumes its card for the dungeon Light of Bonds panel (verified LC_DAL_Team).
                await ToOthersAsync(team, 0, TeamMemberUpdate, update);
            }
            else if (team.LastBattle.GetValueOrDefault(m) != fight)
            {
                await ToOthersAsync(team, m, TeamMemberBattleUpdate, new TeamMemberBattleInfoChangeRes { Guid = m, BattleInfo = battle });
            }
            team.LastBattle[m] = fight;
        }
        // A card carries a member's career and online state, which party-counting passives (Union) read.
        if (cardChanged) await NotifyRosterAsync([.. team.Members]);
    }

    // ---- shapes ----

    private TeamTotalDetailInfo All(Team team)
    {
        var all = new TeamTotalDetailInfo { Info = Base(team) };
        foreach (var m in team.Members) all.MemberList[m] = Detail(team, m);
        return all;
    }

    private static TeamDetailInfo Base(Team team)
    {
        var result = new TeamDetailInfo { Info = Simple(team) };
        result.FlagList[0] = new TeamFlagInfo(); // LC_DAL_Team indexes flagList unguarded.
        foreach (var (slot, flag) in team.Flags) result.FlagList[slot] = flag;
        foreach (var (role, expiry) in team.Applicants) result.ApplyerList[role] = expiry;
        return result;
    }

    private static TeamSimpleInfo Simple(Team team) => new()
    {
        TeamType = team.Type, TeamId = team.Id, TargetId = team.Target, TeamName = team.Name, TeamDesc = team.Description,
        MinBattlePoint = team.MinBattlePoint, AreaInfo = team.Area, PlayBookId = team.PlayBook,
        IsAutoAgree = team.AutoAgree, CreatTime = team.Created,
    };

    /// <summary>A member card; battleInfo.hpPercent / mpPercent carry the absolute HP / SP, pos is mandatory (§2.3).</summary>
    private TeamMemberDetailInfo Detail(Team team, long role)
    {
        var s = Session(role);
        var r = s?.Role ?? _roles.Get(role);
        var p = s?.Progress;
        var detail = new TeamMemberDetailInfo
        {
            Guid = role, Job = p?.Career ?? r?.Career ?? 1000, Level = p?.Level ?? r?.Level ?? 1, Name = r?.Name ?? "", Gender = r?.Sex ?? 1,
            GuildName = "", MapId = (int)(s?.SceneEntry ?? r?.SceneEntry ?? 0),
            LineId = s is { InstanceId: 0 } ? s.LineNumber : 1,
            Title = team.Leader == role ? TeamTitleType.Leader : team.ViceLeaders.Contains(role) ? TeamTitleType.ViceLeader : TeamTitleType.None,
            TeamIndex = team.Members.IndexOf(role),
            JoinTeamTime = team.Joined.GetValueOrDefault(role),
            OnlineState = s == null ? TeamMemberOnlineState.Offline : team.OnlineStates.GetValueOrDefault(role),
            ChatState = team.ChatStates.GetValueOrDefault(role), IsForbiddenChat = team.Muted.Contains(role), IsFollow = team.Following.Contains(role),
        };
        var battle = new TeamMemberBattleInfo { Pos = new Vec3(), MaxHpValue = 1, MaxMpValue = 1 };
        if (s?.Progress != null)
        {
            var stats = _character.Stats(s);
            battle.MaxHpValue = Math.Max(1, stats[MaxHp]);
            battle.MaxMpValue = Math.Max(1, stats[MaxSp]);
            battle.HpPercent = (int)Math.Min(int.MaxValue, s.CurrentHp ?? battle.MaxHpValue);
            battle.MpPercent = (int)Math.Min(int.MaxValue, s.CurrentSp ?? battle.MaxMpValue);
            battle.Pos = new Vec3 { X = s.Position.X, Y = s.Position.Y, Z = s.Position.Z };
            battle.PlayerLifeStatus = s.Dead ? EPlayerLifeStatus.Dead : EPlayerLifeStatus.Alive;
        }
        detail.BattleInfo = battle;
        if (s != null && MultiDungeonInfo != null) detail.MultiDungeonBytes = MultiDungeonInfo(s, team.Members).ToByteString();
        return detail;
    }

    private static int Capacity(Team team) => team.Type == TeamType.Forty ? 40 : 5;

    private GameSession? Session(long role) => _sessions.All().FirstOrDefault(s => s.Role?.Id == role);

    private async Task ToOthersAsync(Team team, long except, uint msgId, IMessage body)
    {
        foreach (var m in team.Members)
            if (m != except && Session(m) is { } other) await other.SendGameAsync(msgId, body);
    }
}
