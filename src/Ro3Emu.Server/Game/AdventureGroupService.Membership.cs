using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

public sealed partial class AdventureGroupService
{
    /// <summary>Chat invitation tokens (34009 Ope_Invite; the inviter's client sends the link, HLP:2134-2241).
    /// In memory: an invitation does not survive a restart (inferred).</summary>
    private readonly Dictionary<long, (long Group, long Inviter, long Invitee, long Expires)> _invites = new();
    private static readonly TimeSpan InviteLife = TimeSpan.FromHours(24); // (inferred) no config names it

    private static long NewToken() => Random.Shared.NextInt64(1, long.MaxValue);

    private int Cooldown(GameSession s, out int remaining)
    {
        long until = Personal(s).LockApplyTime;
        remaining = (int)Math.Max(0, until - Now);
        return remaining > 0 ? ErrCooldown : 0;
    }

    private AdventureGroupStore.Member NewMember(long roleId) => new()
    {
        RoleId = roleId, Name = Online(roleId)?.Role?.Name ?? _roles.Get(roleId)?.Name ?? "", EnterTime = Now,
    };

    /// <summary>A role joins exactly one group; its outstanding applications elsewhere are withdrawn (inferred).</summary>
    private async Task WithdrawApplicationsAsync(long roleId, long exceptGroup)
    {
        foreach (var other in _store.List().Where(x => x.Id != exceptGroup && x.Applications.ContainsKey(roleId)))
        {
            var applicant = other.Applications[roleId];
            other.Applications.Remove(roleId);
            if (await SaveAsync(other)) await BroadcastApplicantAsync(other, applicant, added: false);
        }
    }

    /// <summary>Add a member (support or normal), commit, publish. False when the role is already in another group.</summary>
    private async Task<bool> AdmitAsync(AdventureGroupStore.State g, long roleId)
    {
        var member = NewMember(roleId);
        g.Members[roleId] = member;
        var applicant = g.Applications.GetValueOrDefault(roleId);
        g.Applications.Remove(roleId);
        if (g.Status == StatusNormal) AddEvent(g, AdventureGroupEventType.EventTypeJoin, roleId);
        if (!await SaveAsync(g)) return false;
        if (applicant != null) await BroadcastApplicantAsync(g, applicant, added: false);
        await BroadcastMemberAsync(g, member, ChangeType.ChangeMemberAdd, except: roleId);
        if (Online(roleId) is { } joined)
        {
            await SendFullAsync(joined, g, joined: true);
            await SyncRelationAsync(joined, g);
            await RefreshGroupQuestsAsync(joined);
        }
        await WithdrawApplicationsAsync(roleId, g.Id);
        return true;
    }

    /// <summary>Remove a member (leave or kick): the remaining members get Member_Del, the former member an empty 34011.</summary>
    private async Task<bool> RemoveAsync(AdventureGroupStore.State g, long roleId, bool kicked, long actor)
    {
        if (!g.Members.Remove(roleId, out var member)) return false;
        if (g.Status == StatusNormal)
            AddEvent(g, kicked ? AdventureGroupEventType.EventTypeKick : AdventureGroupEventType.EventTypeLeave, roleId, kicked ? actor : 0, member.Name);
        if (!await SaveAsync(g)) return false;
        await BroadcastMemberAsync(g, member, ChangeType.ChangeMemberDel);
        if (Online(roleId) is { } former)
        {
            await SendClearAsync(former);
            await SyncRelationAsync(former, null);
        }
        return true;
    }

    /// <summary>Disband: every member is cleared (the group row and its membership rows go in one commit).</summary>
    private async Task<bool> DisbandAsync(AdventureGroupStore.State g)
    {
        if (!await DeleteAsync(g)) return false;
        AbortRitual(g.Id);
        foreach (var s in OnlineMembers(g))
        {
            await SendClearAsync(s);
            await SyncRelationAsync(s, null);
        }
        _log.LogInformation("Adventure group {Id} {Name} disbanded", g.Id, g.Name);
        return true;
    }

    // ---------------------------------------------------------------- 34000 / 34001

    public async Task CreateAsync(GameSession s, CreateAdventureGroupReq req)
    {
        var resp = new CreateAdventureGroupRes { Req = req.Clone() };
        int remaining = 0;
        int code = !Open(s) ? ErrorCodes.FeatureUnavailable : Cooldown(s, out remaining);
        resp.PunishTime = code == ErrCooldown ? remaining : 0;
        long self = s.Role!.Id;
        var labels = req.Label.ToList();
        bool npc = req.CreateType == AdventureGroupCreateType.CreateTypeNpc;
        if (code == 0 && Own(s) != null) code = ErrJoined;
        if (code == 0 && req.CreateType is not (AdventureGroupCreateType.CreateTypeNpc or AdventureGroupCreateType.CreateTypeRecruit))
            code = ErrorCodes.Parameter;
        if (code == 0) code = CheckName(req.Name);
        if (code == 0) code = CheckAnnounce(req.Announce);
        if (code == 0) code = CheckLabels(labels);
        int background = req.Background == 0 ? 1 : req.Background;
        if (code == 0 && !DefaultCard(background)) code = ErrorCodes.Parameter;
        if (code == 0 && _store.List().Count >= Number("AdventureGroupMaxCreateLimit", 100000)) code = ErrMaxGroups;
        // The mentor route needs the party at hand (LaunchBrotherhoodCreation, HLP:2333-2482); checked again at 34014.
        if (code == 0 && npc) code = PartyCheck(s, null);
        if (code == 0)
        {
            var g = new AdventureGroupStore.State
            {
                Name = req.Name, Announce = req.Announce, Labels = labels, Background = background,
                Status = npc ? StatusTemp : StatusHelper, CreateType = npc ? 2 : 1, CreateTime = Now, Owner = self,
                AutoAgreeHelperApply = req.AutoAgreeHelperApply, DayResetTime = DayStart, WeekResetTime = WeekStart,
            };
            g.Members[self] = NewMember(self);
            if (!await SaveAsync(g)) code = _store.NameExists(req.Name) ? ErrNameTaken : ErrJoined;
            else
            {
                await s.SendGameAsync(Create, resp);
                await SendFullAsync(s, g, joined: true);
                await SyncRelationAsync(s, g);
                await WithdrawApplicationsAsync(self, g.Id);
                _log.LogInformation("{Name} creates adventure group {Id} {Group} ({Type})", s.Role.Name, g.Id, g.Name, npc ? "mentor" : "recruit");
                return;
            }
        }
        resp.Result = code;
        await s.SendGameAsync(Create, resp);
    }

    /// <summary>34001: the owner dissolves the group (the recruit board's "Cancel recruitment" on a support group).</summary>
    public async Task DeleteAsync(GameSession s)
    {
        var g = Own(s);
        int code = g == null ? ErrNotInGroup : g.Owner != s.Role!.Id ? ErrPermission
            : g.Status == StatusNormal && g.Members.Count > 1 ? ErrPermission : 0; // (inferred) a normal group with members is not deleted
        if (code == 0 && !await DisbandAsync(g!)) code = ErrorCodes.Unknown;
        await s.SendGameAsync(Delete, new DeleteAdventureGroupRes { Result = code });
    }

    // ---------------------------------------------------------------- 34005 support recruitment

    public async Task HelperOpeAsync(GameSession s, HelperAdventureGroupApplyerOpeReq req)
    {
        long self = s.Role!.Id;
        var resp = new HelperAdventureGroupApplyerOpeRes { Op = req.Op, TargetPlayerid = req.TargetPlayerid, AdventureGroupId = req.AdventureGroupId };
        int code;
        switch (req.Op)
        {
            case OpeType.OpeHelperApply:
                code = await HelperApplyAsync(s, _store.Get(req.AdventureGroupId), resp);
                break;
            case OpeType.OpeHelperCancel:
                {
                    var g = _store.Get(req.AdventureGroupId) ?? Own(s);
                    if (g == null || g.Status == StatusNormal) { code = ErrNotHelperGroup; break; }
                    resp.AdventureGroupId = g.Id;
                    if (g.Applications.Remove(self, out var applicant))
                        code = await SaveAsync(g) ? 0 : ErrorCodes.Unknown;
                    else if (g.Members.ContainsKey(self) && g.Owner != self)
                        code = await RemoveAsync(g, self, kicked: false, self) ? 0 : ErrorCodes.Unknown; // QuitResponse
                    else { code = ErrNoApplication; break; }
                    if (code == 0 && applicant != null) await BroadcastApplicantAsync(g, applicant, added: false);
                    break;
                }
            case OpeType.OpeAgree or OpeType.OpeReject or OpeType.OpeKick:
                {
                    var g = Own(s);
                    code = g == null ? ErrNotInGroup : g.Owner != self ? ErrPermission : g.Status == StatusNormal ? ErrNotHelperGroup : 0;
                    if (code != 0) break;
                    resp.AdventureGroupId = g!.Id;
                    long target = req.TargetPlayerid;
                    if (req.Op == OpeType.OpeKick)
                    {
                        code = target == self || !g.Members.ContainsKey(target) ? ErrorCodes.Parameter
                            : await RemoveAsync(g, target, kicked: true, self) ? 0 : ErrorCodes.Unknown;
                        break;
                    }
                    if (!g.Applications.TryGetValue(target, out var applicant)) { code = ErrNoApplication; break; }
                    if (req.Op == OpeType.OpeReject)
                    {
                        g.Applications.Remove(target);
                        code = await SaveAsync(g) ? 0 : ErrorCodes.Unknown;
                        if (code == 0) await BroadcastApplicantAsync(g, applicant, added: false);
                        break;
                    }
                    code = _store.ForRole(target) != null ? ErrInGroup : g.Members.Count >= MemberLimit ? ErrHelperLimit : 0;
                    if (code == ErrInGroup)
                    {
                        g.Applications.Remove(target);
                        if (await SaveAsync(g)) await BroadcastApplicantAsync(g, applicant, added: false);
                    }
                    if (code == 0 && !await AdmitAsync(g, target)) code = ErrInGroup;
                    break;
                }
            default:
                code = ErrorCodes.Parameter;
                break;
        }
        resp.Result = code;
        await s.SendGameAsync(HelperOpe, resp);
    }

    private async Task<int> HelperApplyAsync(GameSession s, AdventureGroupStore.State? g, HelperAdventureGroupApplyerOpeRes resp)
    {
        long self = s.Role!.Id;
        int remaining = 0;
        int code = !Open(s) ? ErrorCodes.FeatureUnavailable : Cooldown(s, out remaining);
        resp.PunishTime = code == ErrCooldown ? remaining : 0;
        if (code != 0) return code;
        if (g == null) return ErrNoGroup;
        if (g.Status != StatusHelper) return ErrNotHelperGroup;
        if (!HelperLive(g)) return ErrRecruitExpired;
        if (Own(s) != null) return ErrJoined;
        if (g.Applications.ContainsKey(self)) return ErrApplied;
        var all = _store.List();
        if (all.Count(x => x.Status == StatusHelper && x.Applications.ContainsKey(self)) >= HelperApplyLimit) return ErrApplyLimit;
        if (g.Members.Count >= MemberLimit) return ErrHelperLimit;
        if (g.Applications.Count >= GroupApplicationLimit) return ErrAcceptLimit;
        if (g.AutoAgreeHelperApply) return await AdmitAsync(g, self) ? 0 : ErrJoined;
        var applicant = new AdventureGroupStore.Applicant { RoleId = self, Name = s.Role.Name, Time = Now };
        g.Applications[self] = applicant;
        if (!await SaveAsync(g)) return ErrorCodes.Unknown;
        await BroadcastApplicantAsync(g, applicant, added: true);
        return 0;
    }

    // ---------------------------------------------------------------- 34009 normal group

    public async Task NormalOpeAsync(GameSession s, NormalAdventureGroupOpeReq req)
    {
        var data = req.Opedata ?? new NormalOpeData();
        var resp = new NormalAdventureGroupOpeRes { Tp = req.Tp, Opedata = data.Clone() };
        int code = req.Tp switch
        {
            OpeType.OpeApply => data.InviteToken != 0 ? await AcceptInviteAsync(s, data, resp) : await ApplyAsync(s, data, resp),
            OpeType.OpeCancelApply => await CancelApplyAsync(s, data),
            OpeType.OpeAgreeApply => await AnswerApplicationAsync(s, data),
            OpeType.OpeLeave => await LeaveAsync(s),
            OpeType.OpeKick => await KickAsync(s, data.InvitedPlayerId),
            OpeType.OpeChangeOwner => await TransferAsync(s, data.InvitedPlayerId),
            OpeType.OpeInvite => await InviteAsync(s, data, resp),
            OpeType.OpeEditNickName => await NicknameAsync(s, data.Nickname),
            OpeType.OpeChangeRecruit => await RecruitAsync(s, data.IsRecruit),
            OpeType.OpeTakenLevelUpReward => await ClaimLevelRewardAsync(s, data.LevelReward),
            _ => ErrorCodes.Parameter,
        };
        resp.Result = code;
        await s.SendGameAsync(NormalOpe, resp);
    }

    private async Task<int> ApplyAsync(GameSession s, NormalOpeData data, NormalAdventureGroupOpeRes resp)
    {
        long self = s.Role!.Id;
        int remaining = 0;
        int code = !Open(s) ? ErrorCodes.FeatureUnavailable : Cooldown(s, out remaining);
        resp.PunishTime = code == ErrCooldown ? remaining : 0;
        if (code != 0) return code;
        var g = _store.Get(data.AdventureId);
        if (g == null || g.Status != StatusNormal) return ErrNoGroup;
        if (Own(s) != null) return ErrJoined;
        if (g.Applications.ContainsKey(self)) return ErrApplied;
        if (g.Members.Count >= MemberLimit) return ErrFull;
        if (_store.List().Count(x => x.Status == StatusNormal && x.Applications.ContainsKey(self)) >= NormalApplyLimit) return ErrApplyLimit;
        if (g.Applications.Count >= GroupApplicationLimit) return ErrAcceptLimit;
        if (g.AutoAgreeApply) return await AdmitAsync(g, self) ? 0 : ErrJoined;
        var applicant = new AdventureGroupStore.Applicant { RoleId = self, Name = s.Role.Name, Time = Now };
        g.Applications[self] = applicant;
        if (!await SaveAsync(g)) return ErrorCodes.Unknown;
        await BroadcastApplicantAsync(g, applicant, added: true);
        return 0;
    }

    private async Task<int> CancelApplyAsync(GameSession s, NormalOpeData data)
    {
        var g = _store.Get(data.AdventureId);
        if (g == null || !g.Applications.Remove(s.Role!.Id, out var applicant)) return ErrNoApplication;
        if (!await SaveAsync(g)) return ErrorCodes.Unknown;
        await BroadcastApplicantAsync(g, applicant, added: false);
        return 0;
    }

    private async Task<int> AnswerApplicationAsync(GameSession s, NormalOpeData data)
    {
        var g = Own(s);
        if (g == null) return ErrNotInGroup;
        if (g.Owner != s.Role!.Id) return ErrPermission;
        long target = data.InvitedPlayerId;
        if (!g.Applications.TryGetValue(target, out var applicant)) return ErrNoApplication;
        if (data.IsAgree && _store.ForRole(target) == null && g.Members.Count < MemberLimit)
            return await AdmitAsync(g, target) ? 0 : ErrInGroup;
        g.Applications.Remove(target);
        if (!await SaveAsync(g)) return ErrorCodes.Unknown;
        await BroadcastApplicantAsync(g, applicant, added: false);
        return !data.IsAgree ? 0 : g.Members.Count >= MemberLimit ? ErrFull : ErrInGroup;
    }

    /// <summary>Ope_Leave. The owner of a group with other members transfers first (inferred); a last member's leave
    /// dissolves it. Leaving starts the AdventureGroupLeavePunish cooldown (10226 with punishTime, DAL:52-86).</summary>
    private async Task<int> LeaveAsync(GameSession s)
    {
        var g = Own(s);
        long self = s.Role!.Id;
        if (g == null) return ErrNotInGroup;
        if (g.Owner == self && g.Members.Count > 1) return ErrPermission;
        bool ok = g.Members.Count == 1 ? await DisbandAsync(g) : await RemoveAsync(g, self, kicked: false, self);
        if (!ok) return ErrorCodes.Unknown;
        if (g.Status == StatusNormal)
        {
            var next = Personal(s).Clone();
            next.LockApplyTime = (int)(Now + LeavePunishSeconds);
            await SavePersonalAsync(s, next);
            await SyncRelationAsync(s, null);
        }
        return 0;
    }

    private async Task<int> KickAsync(GameSession s, long target)
    {
        var g = Own(s);
        if (g == null) return ErrNotInGroup;
        if (g.Owner != s.Role!.Id || target == g.Owner) return ErrPermission;
        if (!g.Members.ContainsKey(target)) return ErrorCodes.Parameter;
        return await RemoveAsync(g, target, kicked: true, s.Role.Id) ? 0 : ErrorCodes.Unknown;
    }

    private async Task<int> TransferAsync(GameSession s, long target)
    {
        var g = Own(s);
        if (g == null) return ErrNotInGroup;
        if (g.Owner != s.Role!.Id || target == g.Owner || g.Status != StatusNormal) return ErrPermission;
        if (!g.Members.ContainsKey(target)) return ErrorCodes.Parameter;
        if (g.ChangeOwnerTime != 0 && g.ChangeOwnerTime + ChangeOwnerCooldown > Now) return ErrOwnerCooldown;
        g.Owner = target;
        g.ChangeOwnerTime = Now;
        if (!await SaveAsync(g)) return ErrorCodes.Unknown;
        await BroadcastBaseAsync(g);
        // memberData[1] is the owner: the reordered roster goes out whole.
        foreach (var member in OnlineMembers(g))
        {
            await SendFullAsync(member, g);
            await SyncRelationAsync(member, g);
        }
        return 0;
    }

    private async Task<int> InviteAsync(GameSession s, NormalOpeData data, NormalAdventureGroupOpeRes resp)
    {
        var g = Own(s);
        long target = data.InvitedPlayerId, self = s.Role!.Id;
        if (g == null || g.Status != StatusNormal) return ErrNotInGroup;
        if (target == self || _roles.Get(target) == null) return ErrorCodes.Parameter;
        if (!await _friends.AreFriendsAsync(self, target)) return ErrFriendsOnly;
        if (_store.ForRole(target) != null) return ErrInGroup;
        if (g.Members.Count >= MemberLimit) return ErrFull;
        long token = NewToken();
        _invites[token] = (g.Id, self, target, Now + (long)InviteLife.TotalSeconds);
        resp.Opedata.InvitedPlayerId = target;
        resp.Opedata.InviteToken = token;
        resp.Opedata.Inviter = self;
        return 0;
    }

    /// <summary>The chat link's "accept" (LC_LinkHelper → HLP:1783-1867): Ope_Apply with inviteToken and inviter.</summary>
    private async Task<int> AcceptInviteAsync(GameSession s, NormalOpeData data, NormalAdventureGroupOpeRes resp)
    {
        long self = s.Role!.Id;
        if (!_invites.TryGetValue(data.InviteToken, out var invite) || invite.Invitee != self || invite.Inviter != data.Inviter)
            return ErrNoInvite;
        if (invite.Expires < Now) { _invites.Remove(data.InviteToken); return ErrInviteExpired; }
        int remaining = 0;
        int code = !Open(s) ? ErrorCodes.FeatureUnavailable : Cooldown(s, out remaining);
        resp.PunishTime = code == ErrCooldown ? remaining : 0;
        if (code != 0) return code;
        var g = _store.Get(invite.Group);
        if (g == null || g.Status != StatusNormal) return ErrNoGroup;
        if (Own(s) != null) return ErrJoined;
        if (g.Members.Count >= MemberLimit) return ErrFull;
        _invites.Remove(data.InviteToken);
        resp.Opedata.AdventureId = g.Id;
        return await AdmitAsync(g, self) ? 0 : ErrJoined;
    }

    private async Task<int> NicknameAsync(GameSession s, string nickname)
    {
        var g = Own(s);
        if (g == null) return ErrNotInGroup;
        var (_, max) = Range("AdventureGroupNickNameLength", 0, 4);
        if (TextLength(nickname) > max) return ErrorCodes.Parameter;
        if (Banned(nickname)) return ErrBanned;
        var member = g.Members[s.Role!.Id];
        member.Nickname = nickname;
        if (!await SaveAsync(g)) return ErrorCodes.Unknown;
        await BroadcastMemberAsync(g, member, ChangeType.ChangePlayerChange, except: s.Role.Id);
        return 0;
    }

    private async Task<int> RecruitAsync(GameSession s, bool recruit)
    {
        var g = Own(s);
        if (g == null) return ErrNotInGroup;
        if (g.Owner != s.Role!.Id || g.Status != StatusNormal) return ErrPermission;
        g.RecruitTime = recruit ? Now : 0;
        if (!await SaveAsync(g)) return ErrorCodes.Unknown;
        await BroadcastBaseAsync(g);
        return 0;
    }

    // ---------------------------------------------------------------- 34007

    /// <summary>
    /// One field per request (LC_AdventureGroupServer.RequestEditAdventureGroup, SRV:344-505); the response must echo
    /// every current field, the DAL copies them all (DAL:514-594).
    /// </summary>
    public async Task EditAsync(GameSession s, AdventureGroupEditReq req)
    {
        var g = Own(s);
        var d = req.Data ?? new editData();
        int code = g == null ? ErrNotInGroup : g.Owner != s.Role!.Id ? ErrPermission : 0;
        string renamed = "";
        if (code == 0)
        {
            switch (req.Tp)
            {
                case EditType.EditName:
                    code = d.Name == g!.Name ? 0 : CheckName(d.Name, g.Id);
                    if (code == 0 && d.Name != g.Name) { renamed = d.Name; g.Name = d.Name; }
                    break;
                case EditType.EditAnnounce:
                    code = CheckAnnounce(d.Announce);
                    if (code == 0) g!.Announce = d.Announce;
                    break;
                case EditType.EditBackground:
                    code = CardUnlocked(g!, d.Background) ? 0 : ErrorCodes.Parameter;
                    if (code == 0) g!.Background = d.Background;
                    break;
                case EditType.EditLabel:
                    code = CheckLabels(d.Label.ToList());
                    if (code == 0) g!.Labels = d.Label.ToList();
                    break;
                case EditType.EditAutoAcceptApply:
                    g!.AutoAgreeApply = d.AutoAcceptApply;
                    break;
                case EditType.EditAutoCreateHelper:
                    g!.AutoAgreeHelperApply = d.AutoCreateHelper;
                    break;
                case EditType.EditCanSeeAnnounce:
                    g!.CanSeeMemberAnnounce = d.CanSeeAnnounce;
                    break;
                default:
                    code = ErrorCodes.Parameter;
                    break;
            }
        }
        if (code == 0)
        {
            if (renamed.Length > 0 && g!.Status == StatusNormal)
                AddEvent(g, AdventureGroupEventType.EventTypeRename, s.Role!.Id, 0, renamed);
            if (!await SaveAsync(g!)) code = renamed.Length > 0 ? ErrNameTaken : ErrorCodes.Unknown;
        }
        var resp = new AdventureGroupEditRes { Result = code, Tp = (long)req.Tp };
        var current = Own(s);
        if (current != null)
        {
            resp.Data = new editData
            {
                Name = current.Name, Announce = current.Announce, Background = current.Background,
                AutoAcceptApply = current.AutoAgreeApply, AutoCreateHelper = current.AutoAgreeHelperApply,
                CanSeeAnnounce = current.CanSeeMemberAnnounce,
            };
            resp.Data.Label.Add(current.Labels);
        }
        await s.SendGameAsync(Edit, resp);
        if (code == 0 && current != null) await BroadcastBaseAsync(current);
    }
}
