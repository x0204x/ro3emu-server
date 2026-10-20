using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>Extended party operations from all changes use the same authoritative roster.</summary>
public sealed partial class TeamService
{
    private readonly Dictionary<long, DateTimeOffset> _recruitUntil = new();
    public const uint TeamChangeType = 42002, TeamChangeInfo = 42003, TeamRally = 42009, TeamRallyBroadcast = 42010,
        TeamRallyResponse = 42011, TeamEditor = 42012, TeamGetList = 42013, TeamEnter = 42014, TeamSetFlag = 42015,
        TeamApplyList = 42018, TeamRallyResponseNotify = 42023, TeamChangeTypeNotify = 42027, TeamFollow = 42031,
        TeamRecruit = 42034, TeamBaseUpdate = 42049, TeamMemberIndexUpdate = 42052;

    /// <summary>42002; conversion retains membership and publishes a replacement roster (LC_DAL_Team:1860).</summary>
    public Task ChangeTypeAsync(GameSession s, ChangeTeamTypeReq req) =>
        RunTeamAsync(s, "team/change-type", () => ChangeTypeCoreAsync(s, req));

    private async Task ChangeTypeCoreAsync(GameSession s, ChangeTeamTypeReq req)
    {
        long self = s.Role!.Id;
        int code = !_byMember.TryGetValue(self, out var team) ? ErrorCodes.NotInParty
            : team!.Leader != self ? ErrorCodes.NoPermission
            : req.TeamType is not (TeamType.Five or TeamType.Forty) ? ErrorCodes.Parameter
            : req.TeamType == TeamType.Five && team.Members.Count > 5 ? ErrorCodes.PartyFull : 0;
        if (code == 0)
        {
            team!.Type = req.TeamType;
            if (!ValidTarget(team.Target, team.Type)) team.Target = NoTarget; // (inferred) converted picker cannot retain an incompatible target.
        }
        await s.SendGameAsync(TeamChangeType, new ChangeTeamTypeResp { Result = code, TeamType = req.TeamType, NewTeamId = code == 0 ? team!.Id : 0, Guid = self });
        if (code != 0) return;
        await ToOthersAsync(team!, 0, TeamChangeTypeNotify, All(team!));
    }

    /// <summary>42003; target and visible recruitment metadata (LC_TeamServer:261-286).</summary>
    public Task ChangeInfoAsync(GameSession s, ChangeTeamInfoReq req) =>
        RunTeamAsync(s, "team/change-info", () => ChangeInfoCoreAsync(s, req));

    private async Task ChangeInfoCoreAsync(GameSession s, ChangeTeamInfoReq req)
    {
        int code = !TryLeader(s, out var team) ? ErrorCodes.NoPermission
            : !ValidTarget(req.TargetId, team!.Type) || req.MinBattlePoint < 0 || req.TeamDesc.Length > 512 || req.TeamName.Length > 128 ? ErrorCodes.Parameter : 0;
        if (code == 0)
        {
            team!.Target = req.TargetId == 0 ? NoTarget : req.TargetId;
            team.MinBattlePoint = req.MinBattlePoint;
            team.Description = req.TeamDesc;
            team.Name = req.TeamName;
            team.AutoAgree = req.IsAutoAgree;
        }
        await s.SendGameAsync(TeamChangeInfo, new ChangeTeamInfoResp { Result = code });
        if (code != 0) return;
        await PublishBaseAsync(team!);
    }

    /// <summary>42013 exists in protobuf, while normal Lua browsing uses PartyService.</summary>
    public async Task GetListAsync(GameSession s, GetTeamListReq req)
    {
        var resp = new GetTeamListResp();
        foreach (var team in PublicTeams(req.TargetId, req.AreaInfo)) resp.List.Add(TotalSimple(team));
        await s.SendGameAsync(TeamGetList, resp);
    }

    /// <summary>42017 online invite suggestions; only actual ungrouped sessions are offered (LC_DAL_Team:2370-2415).</summary>
    public async Task InviteListAsync(GameSession s, TeamInviteListReq req)
    {
        int code = !_byMember.ContainsKey(s.Role!.Id) ? ErrorCodes.NotInParty : 0;
        var resp = new TeamInviteListResp { Result = code };
        if (code == 0)
        {
            var recent = friends.RecentTeammates(s.Role.Id).ToHashSet();
            foreach (var target in sessions.All().Where(target => target.Role != null && target.Role.Id != s.Role.Id
                && !_byMember.ContainsKey(target.Role.Id)).Take(100))
                resp.List.Add(new TeamInvitePlayerInfo
                {
                    Guid = target.Role!.Id, Name = target.Role.Name, Job = target.Progress?.Career ?? target.Role.Career,
                    Level = target.Progress?.Level ?? target.Role.Level, IsRecentlySameTeam = recent.Contains(target.Role.Id),
                });
        }
        await s.SendGameAsync(TeamInviteList, resp);
    }

    /// <summary>42014 applicant state and 42018 decision; expiry is an inferred 30 s policy (TeamInviteLastTime).</summary>
    public Task EnterAsync(GameSession s, EnterTeamReq req) =>
        RunTeamAsync(s, "team/apply", () => EnterCoreAsync(s, req), teamId: req.TeamId);

    private async Task EnterCoreAsync(GameSession s, EnterTeamReq req)
    {
        long self = s.Role!.Id;
        int code = !_teams.TryGetValue(req.TeamId, out var team) ? ErrorCodes.PartyMissing
            : _byMember.ContainsKey(self) ? ErrorCodes.AlreadyInParty
            : team!.Members.Count >= Capacity(team) ? ErrorCodes.PartyFull
            : !team.AutoAgree && team.Applicants.Count >= 50 && !team.Applicants.ContainsKey(self) ? ErrorCodes.PartyFull : 0;
        bool admitted = code == 0 && (team!.AutoAgree || team.Elites.Contains(self));
        if (code == 0)
        {
            if (admitted) await RecordAndJoinAsync(team!, self);
            else team!.Applicants[self] = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds(); // (inferred) ms from Lua clock
        }
        await s.SendGameAsync(TeamEnter, new EnterTeamResp { Result = code, TeamId = req.TeamId });
        if (code != 0) return;
        if (admitted) await PublishAdmissionAsync(team!, self);
        else await PublishBaseAsync(team!);
    }

    public Task ApplyAsync(GameSession s, ApplyListOperateReq req) =>
        RunTeamAsync(s, "team/apply-decision", () => ApplyCoreAsync(s, req), extraRole: req.TargetGuid);

    private async Task ApplyCoreAsync(GameSession s, ApplyListOperateReq req)
    {
        bool leader = TryLeader(s, out var team);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool expired = leader && req.OperType is (ApplyListOperateType.Agree or ApplyListOperateType.Refuse)
            && team!.Applicants.TryGetValue(req.TargetGuid, out long expiry) && expiry <= now;
        int code = !leader ? ErrorCodes.NoPermission
            : req.OperType == ApplyListOperateType.None ? ErrorCodes.Parameter
            : req.OperType == ApplyListOperateType.Clear ? 0
            : req.OperType is not (ApplyListOperateType.Agree or ApplyListOperateType.Refuse) || !team!.Applicants.ContainsKey(req.TargetGuid) || expired ? ErrorCodes.Parameter
            : req.OperType == ApplyListOperateType.Agree && (_byMember.ContainsKey(req.TargetGuid) || team.Members.Count >= Capacity(team)) ? ErrorCodes.PartyFull
            : req.OperType == ApplyListOperateType.Agree && (roles.Get(req.TargetGuid) == null || Session(req.TargetGuid) == null) ? ErrorCodes.PlayerOffline : 0;
        // Consume the decision and reserve a roster slot before the ACK yields to another application.
        if (expired) team!.Applicants.Remove(req.TargetGuid);
        if (code == 0)
        {
            if (req.OperType == ApplyListOperateType.Clear) team!.Applicants.Clear();
            else
            {
                if (req.OperType == ApplyListOperateType.Agree) await RecordAndJoinAsync(team!, req.TargetGuid);
                else team!.Applicants.Remove(req.TargetGuid);
            }
        }
        await s.SendGameAsync(TeamApplyList, new ApplyListOperateResp { Result = code });
        if (expired) await PublishBaseAsync(team!);
        if (code != 0) return;
        if (req.OperType == ApplyListOperateType.Agree) await PublishAdmissionAsync(team!, req.TargetGuid);
        await PublishBaseAsync(team!);
    }

    private async Task PublishAdmissionAsync(Team team, long role)
    {
        var detail = Detail(team, role);
        if (Session(role) is { } joiner)
        {
            await joiner.SendGameAsync(TeamNewMember, new BroadCastNewMemberEnter { TeamType = team.Type, DetailInfo = detail });
            await joiner.SendGameAsync(TeamAll, All(team));
        }
        await ToOthersAsync(team, role, TeamNewMember, new BroadCastNewMemberEnter { TeamType = team.Type, DetailInfo = detail });
    }

    /// <summary>42012 → 42052; both indices are zero-based (LC_TeamServer:680-710).</summary>
    public Task EditorAsync(GameSession s, EditorTeamReq req) =>
        RunTeamAsync(s, "team/editor", () => EditorCoreAsync(s, req));

    private async Task EditorCoreAsync(GameSession s, EditorTeamReq req)
    {
        int code = !TryLeader(s, out var team) ? ErrorCodes.NoPermission
            : req.IndexFirst < 0 || req.IndexSecond < 0 || req.IndexFirst >= team!.Members.Count || req.IndexSecond >= team.Members.Count || req.IndexFirst == req.IndexSecond ? ErrorCodes.Parameter : 0;
        await s.SendGameAsync(TeamEditor, new EditorTeamResp { Result = code, IndexFirst = req.IndexFirst, IndexSecond = req.IndexSecond, OperGuid = s.Role!.Id });
        if (code != 0) return;
        (team!.Members[req.IndexFirst], team.Members[req.IndexSecond]) = (team.Members[req.IndexSecond], team.Members[req.IndexFirst]);
        var update = new TeamMemberIndexChangeRes();
        for (int i = 0; i < team.Members.Count; i++) update.MemberIndex[team.Members[i]] = i;
        await ToOthersAsync(team, 0, TeamMemberIndexUpdate, update);
    }

    public Task SetFlagAsync(GameSession s, TeamSetFlagReq req) =>
        RunTeamAsync(s, "team/flag", () => SetFlagCoreAsync(s, req));

    private async Task SetFlagCoreAsync(GameSession s, TeamSetFlagReq req)
    {
        int code = !TryLeader(s, out var team) ? ErrorCodes.NoPermission
            : req.Slot is < 0 or >= 10 || req.FlagInfo == null ? ErrorCodes.Parameter : 0;
        await s.SendGameAsync(TeamSetFlag, new TeamSetFlagResp { Result = code, Slot = req.Slot, FlagInfo = req.FlagInfo ?? new TeamFlagInfo(), OperGuid = s.Role!.Id });
        if (code != 0) return;
        team!.Flags[req.Slot] = req.FlagInfo!.Clone();
        await PublishBaseAsync(team);
    }

    public Task FollowAsync(GameSession s, TeamFollowOperReq req) =>
        RunTeamAsync(s, "team/follow", () => FollowCoreAsync(s, req));

    private async Task FollowCoreAsync(GameSession s, TeamFollowOperReq req)
    {
        int code = !_byMember.TryGetValue(s.Role!.Id, out var team) ? ErrorCodes.NotInParty
            : req.Type is not (ETeamFollowType.Team or ETeamFollowType.Group) ? ErrorCodes.Parameter : 0;
        await s.SendGameAsync(TeamFollow, new TeamFollowOperResp { Result = code, Type = req.Type, IsFollow = req.IsFollow });
        if (code != 0) return;
        if (req.IsFollow) team!.Following.Add(s.Role.Id); else team!.Following.Remove(s.Role.Id);
        await PublishMemberAsync(team, s.Role.Id);
    }

    public Task ChangeMemberStateAsync(GameSession s, ChangeTeamMemberStateReq req) =>
        RunTeamAsync(s, "team/member-state", () => ChangeMemberStateCoreAsync(s, req), extraRole: req.MemberGuid);

    private async Task ChangeMemberStateCoreAsync(GameSession s, ChangeTeamMemberStateReq req)
    {
        long self = s.Role!.Id;
        int code = !_byMember.TryGetValue(self, out var team) ? ErrorCodes.NotInParty
            : req.EOperType == ETeamMemberStateOperEnum.EteamMemberStateOperOnline && req.MemberGuid == self && req.OnlineState is TeamMemberOnlineState.None or TeamMemberOnlineState.SavePower ? 0
            : req.EOperType == ETeamMemberStateOperEnum.EteamMemberStateOperChat && req.MemberGuid == self
                && req.ChatState is TeamMemberChatState.None or TeamMemberChatState.Listen or TeamMemberChatState.Close ? 0
            : req.EOperType == ETeamMemberStateOperEnum.EteamMemberStateForbiddenChat && team!.Leader == self && team.Members.Contains(req.MemberGuid) ? 0 : ErrorCodes.NoPermission;
        await s.SendGameAsync(TeamChangeMemberState, new ChangeTeamMemberStateResp { Result = code, Info = req });
        if (code != 0) return;
        if (req.EOperType == ETeamMemberStateOperEnum.EteamMemberStateOperOnline) team!.OnlineStates[self] = req.OnlineState;
        else if (req.EOperType == ETeamMemberStateOperEnum.EteamMemberStateOperChat) team!.ChatStates[self] = req.ChatState;
        else if (req.IsForbiddenChat) team!.Muted.Add(req.MemberGuid); else team!.Muted.Remove(req.MemberGuid);
        await PublishMemberAsync(team!, req.MemberGuid);
    }

    /// <summary>42009/11 popup outcome only; no inferred teleport is performed.</summary>
    public Task RallyAsync(GameSession s, RallyTeamReq req) =>
        RunTeamAsync(s, "team/rally", () => RallyCoreAsync(s, req));

    private async Task RallyCoreAsync(GameSession s, RallyTeamReq req)
    {
        int code = !TryLeader(s, out var team) ? ErrorCodes.NoPermission
            : req.RallyType is not (RallyTeamType.Team or RallyTeamType.Group) ? ErrorCodes.FeatureUnavailable : 0;
        await s.SendGameAsync(TeamRally, new RallyTeamResp { Result = code, RallyType = req.RallyType });
        if (code != 0) return;
        foreach (long member in team!.Members.Where(m => m != s.Role!.Id))
        {
            team.Rallies[member] = (req.RallyType, DateTimeOffset.UtcNow.AddSeconds(15)); // TeamGatheringTime; inferred server expiry.
            if (Session(member) is { } target) await target.SendGameAsync(TeamRallyBroadcast, new RallyTeamNotify { RallyType = req.RallyType, OperGuid = s.Role!.Id });
        }
    }

    public Task RallyResponseAsync(GameSession s, RallyTeamMemberResponse req) =>
        RunTeamAsync(s, "team/rally-response", () => RallyResponseCoreAsync(s, req));

    private async Task RallyResponseCoreAsync(GameSession s, RallyTeamMemberResponse req)
    {
        long self = s.Role!.Id;
        int code = !_byMember.TryGetValue(self, out var team) || !team!.Rallies.TryGetValue(self, out var pending)
            || pending.Type != req.RallyType || pending.Expires <= DateTimeOffset.UtcNow ? ErrorCodes.Parameter : 0;
        await s.SendGameAsync(TeamRallyResponse, new RallyTeamMemberResponseResp { Result = code, RallyType = req.RallyType, IsAgree = req.IsAgree });
        if (code != 0) return;
        team!.Rallies.Remove(self);
        if (Session(team.Leader) is { } leader)
            await leader.SendGameAsync(TeamRallyResponseNotify, new RallyTeamMemberResponse { RallyType = req.RallyType, IsAgree = req.IsAgree, OperGuid = self, InviteGuid = team.Leader });
    }

    /// <summary>42034: leader posts a special recruitment link to joined IM recruit channels (LC_DAL_Team:2874-2904).</summary>
    public async Task RecruitAsync(GameSession s, TeamRecruitReq req)
    {
        (long Id, int Count, int Capacity, int[] Jobs)? publication = null;
        await RunTeamAsync(s, "team/recruit", async () =>
        {
            publication = await RecruitCoreAsync(s, req);
        });
        if (publication is { } value)
            await chat.PublishTeamRecruitmentAsync(s, req.Content, value.Id, value.Count, value.Capacity, value.Jobs);
    }

    private async Task<(long Id, int Count, int Capacity, int[] Jobs)?> RecruitCoreAsync(GameSession s, TeamRecruitReq req)
    {
        int code = !TryLeader(s, out var team) ? ErrorCodes.NoPermission
            : string.IsNullOrWhiteSpace(req.Content) || req.Content.Length > 512 ? ErrorCodes.Parameter
            : _recruitUntil.GetValueOrDefault(team!.Id) > DateTimeOffset.UtcNow ? ErrorCodes.Cooldown : 0;
        await s.SendGameAsync(TeamRecruit, new TeamRecruitResp { Result = code });
        if (code != 0) return null;
        _recruitUntil[team!.Id] = DateTimeOffset.UtcNow.AddSeconds(30); // TeamPromptCD, server enforcement inferred.
        return (team.Id, team.Members.Count, Capacity(team), team.Members.Select(m => Detail(team, m).Job).ToArray());
    }

    private bool TryLeader(GameSession s, out Team? team) =>
        _byMember.TryGetValue(s.Role!.Id, out team) && team.Leader == s.Role.Id;

    private bool ValidTarget(long target, TeamType type)
    {
        if (target is 0 or 1000 or 1001) return true; // PublicParamConfig AllTargetID / NoTargetID.
        if (target < 0) return false;
        long kind = target >> 32, id = target & 0xffffffffL; // LC_TeamHelper.CombineTargetTypeAndID.
        if (kind is < 0 or > 3 || id == 0) return false;
        if (kind is 2 or 3) return true; // MVP/on-hook IDs resolve outside TeamTargetConfig.
        if (data.Row("TeamTargetConfig", id) is not { } row) return false;
        // _iIsRaidTarget is a picker hint; enforcing it at the server is inferred.
        return (GameData.Int(row, "_iIsRaidTarget") == 1) == (type == TeamType.Forty);
    }

    private Task PublishBaseAsync(Team team) => ToOthersAsync(team, 0, TeamBaseUpdate, Base(team));

    private Task PublishMemberAsync(Team team, long role)
    {
        var update = new TeamMemberChangeResp { TeamId = team.Id };
        update.MemberList[role] = Detail(team, role);
        return ToOthersAsync(team, 0, TeamMemberUpdate, update);
    }

    private static TeamTotalSimpleInfo TotalSimple(Team team)
    {
        var result = new TeamTotalSimpleInfo { Info = Simple(team) };
        result.MemberList.Add(team.Members);
        return result;
    }

    private IEnumerable<Team> PublicTeams(long target, string area) => _teams.Values.Where(t =>
        (target == 0 || target == t.Target) && (string.IsNullOrEmpty(area) || area == t.Area) && t.Members.Count < Capacity(t));

    /// <summary>PartyService directory payloads consumed by LC_DAL_Team:1967-2123,3191-3265.</summary>
    public object[] Directory(long target = 0, string area = "", long playBook = 0, int map = 0, int offset = 0, int limit = 100) =>
        PublicTeams(target, area).Where(t => (playBook == 0 || t.PlayBook == playBook)
            && (map == 0 || Detail(t, t.Leader).MapId == map)).OrderBy(t => t.Created).ThenBy(t => t.Id)
            .Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 0, 100)).Select(t => (object)new Dictionary<string, object?>
            {
                ["GameSummaryData"] = TotalSimple(t).ToByteArray(),
            }).ToArray();

    public object[] TargetCounts(string area = "") => _teams.Values.Where(t => area.Length == 0 || t.Area == area)
        .GroupBy(t => t.Target).Select(g => (object)new Dictionary<string, object?>
        {
            ["SubType"] = g.Key, ["Count"] = g.Count(),
        }).ToArray();

    public object? DirectoryParty(long id)
    {
        if (!_teams.TryGetValue(id, out var team)) return null;
        return new Dictionary<string, object?>
        {
            ["ID"] = team.Id, ["PartyType"] = 0,
            ["MemberList"] = team.Members.Select(m => (object)new Dictionary<string, object?> { ["GameMemberData"] = Detail(team, m).ToByteArray() }).ToArray(),
            ["PartyData"] = Base(team).ToByteArray(),
        };
    }
}
