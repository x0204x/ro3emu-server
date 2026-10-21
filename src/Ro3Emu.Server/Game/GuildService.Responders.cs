using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Founding responders: a guild being founded (PreCreate) needs
/// GuildResponsePeopleLimit (3) people, the founder included, within GuildResponseTimeLimit: the founder is the last row of
/// createHelperInfo (new rows are inserted before it, LC_DAL_Guild.lua:2895; the panel shows it first and counts
/// #list/limit, UI_Sys_Guild_ResponseResponseWidgetCtrl.lua:311-412,688-726), and journal event 13 names exactly three
/// founders.
/// <list type="bullet">
/// <item>autoCreateHelper is the founder's approval switch: the create widget's toggle and the founder panel's
/// "No Approval Required" (170005) / "Approval Required" (170004), edited by 32230 tp 7. A response (32220) to a guild
/// with the switch on joins at once; otherwise it waits in the founder's list (32292) for 32293. The client marks the
/// guild as applied on success (LC_DAL_Guild._processGuildCreateOpe).</item>
/// <item>32294/32310/32295: the founder invites an online guildless player by token; accepting joins without approval
/// (the founder asked). The token lives GuildResponseInviteTime (30 s); one invite per target per that time
/// (10021).</item>
/// <item>32320 keeps the founder's red dot (Guild.SponsorResponseApplyList) at the pending count.</item>
/// <item>(inferred) A role holds one guild: its pending responses elsewhere lapse once it joins one (dropped when the
/// other founder looks or approves), and the per-role limit GuildPersonResponseMaxNum counts only live ones.</item>
/// </list>
/// </summary>
public sealed partial class GuildService
{
    public const uint PullHelperApply = 32292, HelperApplyOpe = 32293, InviteHelperToken = 32294, InviteHelperOpe = 32295,
        InviteHelperNotify = 32310, RedDot = 32320;
    // ErrorCodeConfig (English): 466 Insufficient permissions, 467 The guild is not in the response period, 481 The
    // invitation is no longer valid, 10016 This guild has reached the limit for responding applicants, 10017 You have
    // reached the limit for guild responses to your application, 10018 Already responded to another guild, 10021 Please
    // send the response invite again later, 10022 The response invite has timed out.
    public const int ErrNoPermission = 466, ErrNotResponding = 467, ErrInvitationInvalid = 481, ErrResponderListFull = 10016,
        ErrResponderPersonalLimit = 10017, ErrRespondedElsewhere = 10018, ErrInviteAgainLater = 10021,
        ErrResponseInviteExpired = 10022;

    private sealed record HelperInvitation(long GuildId, long Recipient, long Inviter, long ExpiresMs);
    private readonly Dictionary<long, HelperInvitation> _helperInvites = new();

    private int ResponderLimit => (int)Setting("GuildResponsePeopleLimit", 3)[0];

    private bool Founding(GuildStore.State g) => g.Status == (int)GuildStatus.PreCreate && g.HelperDeadlineMs > NowMs();

    private bool IsFounder(GuildStore.State g, long roleId) => g.Members.TryGetValue(roleId, out var m) && m.Official == 1;

    /// <summary>Pending responses whose role is still guildless; the others lapsed.</summary>
    private IEnumerable<GuildStore.Application> LiveResponses(GuildStore.State g) =>
        g.HelperApplies.Values.Where(a => store.ForRole(a.RoleId) == null);

    private GuildCreateHelperInfo HelperInfo(long roleId, string name, int level, int career, long timeMs) => new()
    {
        PlayerID = roleId, Name = name, Level = level, Career = career, CreateTime = timeMs,
        BornAreaId = options.Value.ClusterId, IsOnline = Online(roleId) != null,
    };

    private async Task PushRedDotAsync(GuildStore.State g)
    {
        var founder = g.Members.Values.FirstOrDefault(m => m.Official == 1);
        if (founder != null && Online(founder.RoleId) is { } online)
            await online.SendGameAsync(RedDot, new GuildRedotNotify
            { ChangeType = GuildRedotNotify.Types.ChangeType.CreateHelper, Count = LiveResponses(g).Count() });
    }

    /// <summary>
    /// Adds a responder as member 20; at GuildResponsePeopleLimit members the guild becomes Normal and the journal records
    /// the founding (event 13).
    /// </summary>
    private async Task<bool> AdmitHelperAsync(GuildStore.State guild, long roleId)
    {
        var online = Online(roleId);
        var role = online?.Role ?? roles.Get(roleId);
        if (role == null || store.ForRole(roleId) != null) return false;
        var member = online != null ? NewMember(online, 20) : NewMember(role, 20);
        guild.Members[member.RoleId] = member;
        guild.HelperApplies.Remove(roleId);
        bool formed = guild.Members.Count >= ResponderLimit;
        if (formed)
        {
            guild.Status = (int)GuildStatus.Normal;
            guild.HelperDeadlineMs = 0;
            guild.HelperApplies.Clear();
            // "${1}, ${2}, and ${3} founded the guild together": the founder, then the responders.
            var founders = guild.Members.Values.OrderBy(m => m.Official != 1).ThenBy(m => m.EnterTime).Take(3).ToArray();
            if (founders.Length == 3)
                Journal(guild, EvFounded, founders.Select(m => m.RoleId).ToArray(), founders.Select(m => Str(m.Name)).ToArray());
        }
        if (!await SaveGuildAsync(guild)) return false;
        if (online != null)
        {
            await SyncRole(online);
            await SendFull(online, guild, true);
        }
        var info = HelperInfo(member.RoleId, member.Name, member.Level, member.Career, member.EnterTime);
        foreach (var peer in guild.Members.Values)
            if (Online(peer.RoleId) is { } other)
                await other.SendGameAsync(HelperChange, new GuildCreateHelperMemberChangeNtf
                { Status = GuildCreateHelperMemberChangeNtf.Types.ChangeType.Add, Info = info });
        if (formed) await RefreshMembers(guild);
        await PushRedDotAsync(guild);
        return true;
    }

    /// <summary>32220 for one guild: join at once (no approval) or wait in the founder's list.</summary>
    private async Task<int> RespondAsync(GameSession s, GuildStore.State? guild)
    {
        if (guild == null || !Founding(guild)) return ErrNotResponding;
        if (guild.Members.Count >= ResponderLimit) return ErrResponderListFull;
        if (s.Progress!.Level < guild.JoinLevelLimit) return ErrorCodes.GuildJoinLevel;
        if (guild.AutoCreateHelper) return await AdmitHelperAsync(guild, s.Role!.Id) ? 0 : ErrorCodes.AlreadyInGuild;
        if (guild.HelperApplies.ContainsKey(s.Role!.Id)) return 0;
        if (LiveResponses(guild).Count() >= Setting("GuildResponseMaxNum", 50)[0]) return ErrResponderListFull;
        if (store.List().Count(g => g.Id != guild.Id && Founding(g) && g.HelperApplies.ContainsKey(s.Role.Id))
            >= Setting("GuildPersonResponseMaxNum", 10)[0]) return ErrResponderPersonalLimit;
        guild.HelperApplies[s.Role.Id] = new GuildStore.Application
        {
            RoleId = s.Role.Id, Name = s.Role.Name,
            Level = s.Progress.Level, Career = s.Progress.Career, ApplyTime = NowMs()
        };
        if (!await SaveGuildAsync(guild)) return ErrorCodes.GuildMissing;
        await PushRedDotAsync(guild);
        return 0;
    }

    public Task PullHelperApplyAsync(GameSession s) => RunGuildAsync(s, [], [], () => PullHelperApplyCoreAsync(s));

    private Task PullHelperApplyCoreAsync(GameSession s)
    {
        var res = new PullCreateHelperApplyRes();
        if (Own(s) is { } g && IsFounder(g, s.Role!.Id) && Founding(g))
            res.CreateHelperApplyData.AddRange(LiveResponses(g).OrderBy(a => a.ApplyTime)
                .Select(a => HelperInfo(a.RoleId, a.Name, a.Level, a.Career, a.ApplyTime)));
        return s.SendGameAsync(PullHelperApply, res);
    }

    public Task HelperApplyOpeAsync(GameSession s, CreateHelperApplyOpeReq req) => RunGuildAsync(s, [],
        req.PlayerIDList.Count > 0 ? req.PlayerIDList : Own(s)?.HelperApplies.Keys.ToArray() ?? [],
        () => HelperApplyOpeCoreAsync(s, req));

    private async Task HelperApplyOpeCoreAsync(GameSession s, CreateHelperApplyOpeReq req)
    {
        var g = Own(s);
        var res = new CreateHelperApplyOpeRes { Agree = req.Agree, IsBatch = req.IsBatch };
        res.Result = g == null ? ErrorCodes.NotInGuild : !IsFounder(g, s.Role!.Id) ? ErrNoPermission
            : !Founding(g) ? ErrNotResponding : 0;
        if (res.Result == 0)
        {
            // Accept/Refuse All may name every row or none.
            var ids = (req.PlayerIDList.Count > 0 ? req.PlayerIDList.Distinct() : g!.HelperApplies.Keys.ToArray())
                .Where(g!.HelperApplies.ContainsKey).ToList();
            if (ids.Count == 0) res.Result = ErrorCodes.GuildApplicationMissing;
            foreach (long id in ids)
            {
                if (!g.HelperApplies.ContainsKey(id)) continue; // the guild formed and cleared the list
                if (req.Agree && store.ForRole(id) == null && g.Members.Count < ResponderLimit
                    && await AdmitHelperAsync(g, id)) { res.PlayerIDList.Add(id); continue; }
                if (req.Agree && res.Result == 0 && store.ForRole(id) != null) res.Result = ErrRespondedElsewhere;
                g.HelperApplies.Remove(id);
                if (!req.Agree) res.PlayerIDList.Add(id);
            }
            if (g.Status == (int)GuildStatus.PreCreate) await SaveGuildAsync(g);
            await PushRedDotAsync(g);
        }
        await s.SendGameAsync(HelperApplyOpe, res);
    }

    public Task InviteHelperTokenAsync(GameSession s, InviteCreateHelperTokenReq req) =>
        RunGuildAsync(s, [], [req.InvitePlayerId], () => InviteHelperTokenCoreAsync(s, req));

    private async Task InviteHelperTokenCoreAsync(GameSession s, InviteCreateHelperTokenReq req)
    {
        var g = Own(s);
        var target = Online(req.InvitePlayerId);
        long now = NowMs();
        int code = g == null ? ErrorCodes.NotInGuild : !IsFounder(g, s.Role!.Id) ? ErrNoPermission
            : !Founding(g) ? ErrNotResponding
            : g.Members.Count >= ResponderLimit ? ErrResponderListFull
            : target?.Role == null ? ErrorCodes.PlayerOffline
            : store.ForRole(target.Role.Id) != null ? ErrorCodes.AlreadyInGuild
            : target.Progress!.Level < g.JoinLevelLimit ? ErrorCodes.GuildJoinLevel
            : _helperInvites.Values.Any(i => i.GuildId == g.Id && i.Recipient == target.Role.Id && i.ExpiresMs > now)
            ? ErrInviteAgainLater : 0;
        long token = 0;
        if (code == 0)
        {
            foreach (var stale in _helperInvites.Where(i => i.Value.ExpiresMs <= now).Select(i => i.Key).ToArray())
                _helperInvites.Remove(stale);
            token = ++_nextToken;
            _helperInvites[token] = new HelperInvitation(g!.Id, target!.Role!.Id, s.Role!.Id,
                now + Setting("GuildResponseInviteTime", 30)[0] * 1000);
            await target.SendGameAsync(InviteHelperNotify, new InviteCreateHelperNotify
            {
                Token = token, PlayerId = s.Role.Id,
                GuildId = g.Id, HelperCount = g.Members.Count, GuildName = g.Name
            });
        }
        await s.SendGameAsync(InviteHelperToken, new InviteCreateHelperTokenRes { Token = token, Result = code });
    }

    public Task InviteHelperOpeAsync(GameSession s, InviteCreateHelperOpeReq req) => RunGuildAsync(s,
        _helperInvites.GetValueOrDefault(req.Token) is { } invite ? [invite.GuildId] : [], [], () => InviteHelperOpeCoreAsync(s, req));

    private async Task InviteHelperOpeCoreAsync(GameSession s, InviteCreateHelperOpeReq req)
    {
        var invite = _helperInvites.GetValueOrDefault(req.Token);
        int code = s.Role == null || invite == null || invite.Recipient != s.Role.Id
            ? ErrInvitationInvalid : invite.ExpiresMs < NowMs() ? ErrResponseInviteExpired : 0;
        if (code == 0)
        {
            _helperInvites.Remove(req.Token);
            if (req.Agree)
            {
                var g = store.Get(invite!.GuildId);
                code = g == null || !Founding(g) ? ErrNotResponding
                    : g.Members.Count >= ResponderLimit ? ErrResponderListFull
                    : Own(s) != null ? ErrorCodes.AlreadyInGuild
                    : s.Progress!.Level < g.JoinLevelLimit ? ErrorCodes.GuildJoinLevel
                    : await AdmitHelperAsync(g, s.Role!.Id) ? 0 : ErrorCodes.AlreadyInGuild;
            }
        }
        await s.SendGameAsync(InviteHelperOpe, new InviteCreateHelperOpeRes { Result = code });
    }
}
