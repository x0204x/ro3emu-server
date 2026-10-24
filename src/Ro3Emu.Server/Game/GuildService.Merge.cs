using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Merging guilds (the guild window's Merge Guild tab, UI_Sys_Guild_Manage_MergeWidget, GuildPermissionConfig 21). A guild
/// invites another to merge into it (32319, announcement up to GuildMergeNoticeLimitNew), from the recommended list (32315);
/// the invited guild sees it among its invitations (32314) and accepts or refuses it (32318). An accepted merge runs at the
/// next 05:00 (UIWords 170349): the invited guild is disbanded and the members it chose (32317, at most the inviter's free
/// places, 10053) join the inviter; 32316 shows both guilds and the result. Invitations lapse after GuildMergeLapseTimeLimit
/// hours (10050), each side holds at most GuildMergeListNumLimit (10051/10052), a guild under impeachment cannot merge
/// (10060/10075), and a merge starts GuildMergeTimeCD seconds of cooldown for the guild that absorbed (10063/10078). The
/// pending partner reaches the client as pendingMergeGuildId in 32213 and in 32231 Change_Base. The absorbing guild's
/// members get mail GuildMail21 (1121) with the merged guild's name.
/// (inferred) Moved members join as ordinary members (170290: positions must be reassigned) and keep their contribution;
/// the absorbed guild's treasury, stalls and buildings go to recovery as in a disband (no client text moves them); without
/// a choice saved, every member comes along while there is room; a merge that no longer fits at 05:00 is dropped; a
/// refusal or a cancel before 05:00 (32318 with isAgree false) needs no cooldown; the new-guild and weekly-activity grounds
/// (10064/10065) are not checked; the recommended list is every other guild free to merge, largest first.
/// </summary>
public sealed partial class GuildService
{
    public const uint MergePull = 32314, MergeRecommend = 32315, MergeDetail = 32316, MergeMembers = 32317, MergeOpe = 32318,
        MergeAsk = 32319;
    private const int MergeRight = 21;

    private long MergeLapseSec => Setting("GuildMergeLapseTimeLimit", 24)[0] * 3600;
    private long MergeListLimit => Setting("GuildMergeListNumLimit", 5)[0];

    private bool MergeCooling(GuildStore.State g) => NowSec() < g.MergeCooldownSec;
    private List<GuildStore.MergeInvite> LiveInvites(GuildStore.State g) =>
        [.. g.MergeInvites.Where(i => NowSec() - i.RecvSec < MergeLapseSec && store.Get(i.From) != null)];
    private IEnumerable<GuildStore.State> InvitedBy(GuildStore.State g) =>
        store.List().Where(o => o.Id != g.Id && LiveInvites(o).Any(i => i.From == g.Id));

    public Task MergePullAsync(GameSession s) => RunGuildAsync(s, [], [], () => MergePullCoreAsync(s));
    public Task MergeRecommendAsync(GameSession s) => RunGuildAsync(s, [], [], () => MergeRecommendCoreAsync(s));
    public Task MergeDetailAsync(GameSession s, MergeGuildDetailDataReq req) =>
        RunGuildAsync(s, [req.Guildid, Own(s)?.PendingMergeGuildId ?? 0], [], () => MergeDetailCoreAsync(s, req));
    public Task MergeMembersAsync(GameSession s, SaveMergeMemberReq req) =>
        RunGuildAsync(s, [req.Guildid, Own(s)?.PendingMergeGuildId ?? 0], [], () => MergeMembersCoreAsync(s, req));
    public Task MergeOpeAsync(GameSession s, MergeGuildOpeReq req) => RunGuildAsync(s, [req.Guildid], [], () => MergeOpeCoreAsync(s, req));
    public Task MergeAskAsync(GameSession s, InviteMergeGuildReq req) => RunGuildAsync(s, [req.Guildid], [], () => MergeAskCoreAsync(s, req));

    /// <summary>32314: invitations received and sent.</summary>
    private Task MergePullCoreAsync(GameSession s)
    {
        var rsp = new PullMergeGuildDataRes();
        if (Own(s) is not { } g) rsp.Result = ErrorCodes.NotInGuild;
        else
        {
            // The accepted invitation stays for its announcement (32316) but is no longer a choice.
            rsp.RecvInvite.AddRange(LiveInvites(g).Where(i => i.From != g.PendingMergeGuildId).Select(i => Snapshot(store.Get(i.From)!)));
            rsp.SendInvite.AddRange(InvitedBy(g).Select(o => Snapshot(o)));
        }
        return s.SendGameAsync(MergePull, rsp);
    }

    /// <summary>32315: the other normal guilds free to merge.</summary>
    private Task MergeRecommendCoreAsync(GameSession s)
    {
        var rsp = new PullRecommendMergeGuildDataRes();
        if (Own(s) is not { } g) rsp.Result = ErrorCodes.NotInGuild;
        else rsp.Data.AddRange(store.List().Where(o => o.Id != g.Id && o.Status == (int)GuildStatus.Normal && o.PendingMergeGuildId == 0
                && o.Members.Values.Any(m => m.Official == 1))
            .OrderByDescending(o => o.Members.Count).ThenBy(o => o.Id).Take(20).Select(o => Snapshot(o)));
        return s.SendGameAsync(MergeRecommend, rsp);
    }

    /// <summary>The absorbing and the absorbed guild of a request: guildid 0 is the pending merge; isInvite means the
    /// requester invites (absorbs) the other.</summary>
    private (GuildStore.State Absorber, GuildStore.State Absorbed)? MergePair(GuildStore.State g, long other, bool isInvite)
    {
        if (other == 0)
        {
            if (g.PendingMergeGuildId == 0 || store.Get(g.PendingMergeGuildId) is not { } partner) return null;
            return g.MergeAbsorbs ? (g, partner) : (partner, g);
        }
        if (other == g.Id || store.Get(other) is not { } o) return null;
        return isInvite ? (g, o) : (o, g);
    }

    private int MergeRoom(GuildStore.State absorber) => (int)Math.Max(0, MaxMembers(absorber) - absorber.Members.Count);

    /// <summary>Who the absorbed guild brings: its saved choice, else everyone while there is room (master first).</summary>
    private List<long> MergeMembersOf(GuildStore.State absorbed, GuildStore.State absorber)
    {
        List<long> chosen = absorbed.PendingMergeGuildId == absorber.Id && absorbed.MergeMemberIds.Count > 0
            ? [.. absorbed.MergeMemberIds.Where(absorbed.Members.ContainsKey)]
            : [.. absorbed.Members.Values.OrderBy(m => m.Official).ThenBy(m => m.EnterTime).Select(m => m.RoleId)];
        return absorbed.MergeMemberIds.Count > 0 && absorbed.PendingMergeGuildId == absorber.Id ? chosen : [.. chosen.Take(MergeRoom(absorber))];
    }

    private GuildMergeInfo MergeInfo(GuildStore.State g, int members)
    {
        var info = new GuildMergeInfo
        {
            Guid = g.Id, Name = g.Name, Level = g.Level, MemberCount = members,
            Activity = (int)Math.Min(int.MaxValue, CurActivity(g)), IconInfo = Icon(g),
        };
        info.Buildind.AddRange(g.Buildings.Select(b => BuildingInfo(g, b.Key)));
        return info;
    }

    /// <summary>32316: both guilds, the merged result and the absorbed guild's choice.</summary>
    private Task MergeDetailCoreAsync(GameSession s, MergeGuildDetailDataReq req)
    {
        var rsp = new MergeGuildDetailDataRes();
        var g = Own(s);
        if (g == null) rsp.Result = ErrorCodes.NotInGuild;
        else if (MergePair(g, req.Guildid, req.IsInvite) is not var (absorber, absorbed)) rsp.Result = ErrorCodes.GuildMissing;
        else
        {
            var moving = MergeMembersOf(absorbed, absorber);
            rsp.TargetGuildData = MergeInfo(absorbed, absorbed.Members.Count);
            rsp.InvitedGuildData = MergeInfo(absorber, absorber.Members.Count);
            rsp.NewGuildData = MergeInfo(absorber, absorber.Members.Count + moving.Count);
            rsp.CanMergeCount = MergeRoom(absorber);
            rsp.Announce = absorbed.MergeInvites.FirstOrDefault(i => i.From == absorber.Id)?.Announce ?? "";
            rsp.Memberids.AddRange(moving);
            rsp.LastMergeTime = absorber.PendingMergeGuildId == absorbed.Id ? absorber.MergeAtSec : 0;
        }
        return s.SendGameAsync(MergeDetail, rsp);
    }

    /// <summary>32317: the absorbed guild chooses who comes along, at most the absorber's free places.</summary>
    private async Task MergeMembersCoreAsync(GameSession s, SaveMergeMemberReq req)
    {
        var g = Own(s);
        var partner = g == null || g.PendingMergeGuildId == 0 ? null : store.Get(g.PendingMergeGuildId);
        var ids = req.Memberids.Distinct().ToList();
        int code = g == null ? ErrorCodes.NotInGuild : !HasRight(g, s.Role!.Id, MergeRight) ? ErrorCodes.GuildPermission
            : partner == null || g.MergeAbsorbs ? ErrorCodes.MergeImpossible
            : ids.Count == 0 || ids.Any(id => !g.Members.ContainsKey(id)) ? ErrorCodes.Parameter
            : ids.Count > MergeRoom(partner) ? ErrorCodes.MergeTooMany : 0;
        if (code == 0)
        {
            g!.MergeMemberIds = ids;
            await SaveGuildAsync(g);
        }
        var rsp = new SaveMergeMemberRes { Guildid = req.Guildid, Result = code };
        rsp.Memberids.AddRange(code == 0 ? ids : g?.MergeMemberIds ?? []);
        await s.SendGameAsync(MergeMembers, rsp);
    }

    /// <summary>32319: invite a guild to merge into ours (isAgree), or withdraw the invitation.</summary>
    private async Task MergeAskCoreAsync(GameSession s, InviteMergeGuildReq req)
    {
        var g = Own(s);
        var target = req.Guildid == 0 || req.Guildid == g?.Id ? null : store.Get(req.Guildid);
        var limit = Setting("GuildMergeNoticeLimitNew", 0, 50);
        int code = g == null ? ErrorCodes.NotInGuild : !HasRight(g, s.Role!.Id, MergeRight) ? ErrorCodes.GuildPermission
            : target == null || target.Status != (int)GuildStatus.Normal ? ErrorCodes.GuildMissing : 0;
        if (code == 0 && req.IsAgree)
            code = (req.Announce ?? "").Length > limit[^1] || Sensitive(req.Announce ?? "") ? ErrorCodes.MergeAnnounce
                : g!.PendingMergeGuildId != 0 || target!.PendingMergeGuildId != 0 ? ErrorCodes.MergeImpossible
                : Impeaching(g) ? ErrorCodes.MergeOurImpeach : Impeaching(target) ? ErrorCodes.MergeTheirImpeach
                : MergeCooling(g) ? ErrorCodes.MergeOurCooldown : MergeCooling(target) ? ErrorCodes.MergeTheirCooldown
                : LiveInvites(target).Any(i => i.From == g.Id) ? ErrorCodes.MergeRepeat
                : InvitedBy(g).Count() >= MergeListLimit ? ErrorCodes.MergeOurInvitesFull
                : LiveInvites(target).Count >= MergeListLimit ? ErrorCodes.MergeTheirInvitesFull : 0;
        else if (code == 0 && !target!.MergeInvites.Any(i => i.From == g!.Id)) code = ErrorCodes.MergeNoInvite;
        if (code == 0)
        {
            target!.MergeInvites = [.. LiveInvites(target).Where(i => i.From != g!.Id)];
            if (req.IsAgree) target.MergeInvites.Add(new GuildStore.MergeInvite { From = g!.Id, RecvSec = NowSec(), Announce = req.Announce ?? "" });
            await SaveGuildAsync(target);
        }
        await s.SendGameAsync(MergeAsk, new InviteMergeGuildRes { Guildid = req.Guildid, IsAgree = req.IsAgree, Result = code });
    }

    /// <summary>32318: the invited guild accepts (the merge runs at the next 05:00) or refuses an invitation, or cancels
    /// the merge it accepted (isAgree false for its partner).</summary>
    private async Task MergeOpeCoreAsync(GameSession s, MergeGuildOpeReq req)
    {
        var g = Own(s);
        var inviter = req.Guildid == 0 || req.Guildid == g?.Id ? null : store.Get(req.Guildid);
        int code = g == null ? ErrorCodes.NotInGuild : !HasRight(g, s.Role!.Id, MergeRight) ? ErrorCodes.GuildPermission
            : inviter == null ? ErrorCodes.GuildMissing : 0;
        var invite = code == 0 ? g!.MergeInvites.FirstOrDefault(i => i.From == inviter!.Id) : null;
        bool cancel = code == 0 && !req.IsAgree && g!.PendingMergeGuildId == inviter!.Id;
        if (code == 0 && !cancel)
            code = invite == null ? ErrorCodes.MergeNoInvite
                : !req.IsAgree ? 0
                : NowSec() - invite.RecvSec >= MergeLapseSec ? ErrorCodes.MergeExpired
                : g!.PendingMergeGuildId != 0 || inviter!.PendingMergeGuildId != 0 ? ErrorCodes.MergeImpossible
                : Impeaching(g) ? ErrorCodes.MergeOurImpeach : Impeaching(inviter!) ? ErrorCodes.MergeTheirImpeach
                : MergeCooling(g) ? ErrorCodes.MergeOurCooldown : MergeCooling(inviter!) ? ErrorCodes.MergeTheirCooldown : 0;
        if (code == 0)
        {
            if (cancel)
            {
                g!.MergeInvites.RemoveAll(i => i.From == inviter!.Id);
                ClearMerge(g); ClearMerge(inviter!);
                await SaveGuildAsync(inviter!);
            }
            else if (!req.IsAgree) g!.MergeInvites.Remove(invite!);
            else
            {
                long at = DayStart + 86400; // the next 05:00 (170349 "completed at five o'clock tomorrow morning")
                g!.PendingMergeGuildId = inviter!.Id; g.MergeAbsorbs = false; g.MergeAtSec = at;
                g.MergeMemberIds = MergeMembersOf(g, inviter);
                inviter.PendingMergeGuildId = g.Id; inviter.MergeAbsorbs = true; inviter.MergeAtSec = at;
                await SaveGuildAsync(inviter);
            }
            await SaveGuildAsync(g!);
        }
        await s.SendGameAsync(MergeOpe, new MergeGuildOpeRes { Guildid = req.Guildid, Result = code });
        if (code == 0 && (cancel || req.IsAgree))
        {
            await PushBase(g!, BaseChangeOf(g!));
            await PushBase(inviter!, BaseChangeOf(inviter!));
        }
    }

    private static void ClearMerge(GuildStore.State g)
    {
        g.PendingMergeGuildId = 0; g.MergeAbsorbs = false; g.MergeAtSec = 0; g.MergeMemberIds = [];
    }

    /// <summary>32231 Change_Base: the client resets every field it carries, pendingMergeGuildId included.</summary>
    private GuildBaseInfoChangeNtf BaseChangeOf(GuildStore.State g)
    {
        var change = new GuildBaseInfoChangeNtf
        {
            Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeBase,
            Base = new GuildBaseInfoChangeNtf.Types.BaseChangeInfo
            {
                Name = g.Name, Level = g.Level,
                Announce = g.Announce, Notice = g.Notice, Status = (GuildStatus)g.Status,
                MaxMemberCount = MaxMembers(g), LastChangeNameTime = g.LastChangeNameTime,
                LastChangeNoticeTime = (int)g.LastChangeNoticeTime, PendingMergeGuildId = g.PendingMergeGuildId,
            }
        };
        change.Base.HistoryNames.AddRange(g.HistoryNames.Count > 0 ? g.HistoryNames : [g.Name]);
        return change;
    }

    /// <summary>Heartbeat: lapsed invitations go, and accepted merges run once their 05:00 has come.</summary>
    private async Task RunMergesAsync()
    {
        long now = NowSec();
        foreach (var g in store.List().Where(g => g.MergeInvites.Any(i => now - i.RecvSec >= MergeLapseSec || store.Get(i.From) == null)).ToArray())
        {
            g.MergeInvites = LiveInvites(g);
            await SaveGuildAsync(g);
        }
        foreach (var absorber in store.List().Where(g => g.MergeAbsorbs && g.PendingMergeGuildId != 0 && g.MergeAtSec <= now).ToArray())
        {
            var absorbed = store.Get(absorber.PendingMergeGuildId);
            var moving = absorbed == null || absorbed.PendingMergeGuildId != absorber.Id ? null : MergeMembersOf(absorbed, absorber);
            if (moving == null || moving.Count == 0 || moving.Count > MergeRoom(absorber))
            {
                // The partner is gone or the chosen members no longer fit: nothing changes (10960000033 item 6).
                ClearMerge(absorber);
                await SaveGuildAsync(absorber);
                await PushBase(absorber, BaseChangeOf(absorber));
                if (absorbed != null && absorbed.PendingMergeGuildId == absorber.Id)
                {
                    ClearMerge(absorbed);
                    await SaveGuildAsync(absorbed);
                    await PushBase(absorbed, BaseChangeOf(absorbed));
                }
                continue;
            }
            await MergeAsync(absorber, absorbed!, moving);
        }
    }

    /// <summary>
    /// One transaction: the absorbed guild is disbanded (its rows first, so the membership index stays exclusive), the
    /// members who stay lose their guild view, and the moving members join the absorber as ordinary members.
    /// </summary>
    private async Task MergeAsync(GuildStore.State absorber, GuildStore.State absorbed, List<long> moving)
    {
        var staying = absorbed.Members.Keys.Where(id => !moving.Contains(id)).ToArray();
        var movers = moving.Select(id => absorbed.Members[id]).ToArray();
        long nowMs = NowMs();
        var transaction = roles.NewTransaction();
        var disband = EnlistDisband(absorbed, transaction, staying, absorber.Members.Values.First(m => m.Official == 1).RoleId);
        foreach (var m in movers)
        {
            absorber.Members[m.RoleId] = new GuildStore.Member
            {
                RoleId = m.RoleId, Name = m.Name, Level = m.Level, Career = m.Career, Official = 20, EnterTime = nowMs,
                OfflineTime = m.OfflineTime, CurContribute = m.CurContribute, HistoryContribute = m.HistoryContribute,
            };
            Journal(absorber, EvJoined, [m.RoleId], Str(m.Name));
        }
        ClearMerge(absorber);
        absorber.MergeCooldownSec = NowSec() + Setting("GuildMergeTimeCD", 604800)[0];
        absorber.MergeInvites = [.. LiveInvites(absorber).Where(i => i.From != absorbed.Id)];
        store.EnlistSave(absorber, transaction, "Guild merge roster write failed");
        EnlistViews(absorber, transaction);
        transaction.WithEconomy(disband);
        await roles.CommitAsync(transaction, "guild/merge");

        await RefreshMembers(absorber);
        foreach (long id in staying)
            if (Online(id) is { } s) await ClearMember(s, absorbed);
        await GuildMailAsync(absorber.Members.Keys, (int)Setting("GuildMail21", 1121)[0], $"guild-merge:{absorber.Id}:{absorbed.Id}",
            absorbed.Name);
    }
}
