using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Impeaching the Guild Master (32235 GUILD_PEACH, 32236 GUILD_PEACH_APPLY, UI_Sys_Guild_ImpeachPresidentWidget). A member
/// of ActiveImpeachJoinDay minutes may start one for ActiveImpeachMoney (item 3 x30) when the master has been offline for
/// ActiveImpeachChairmanOffineTimeLimit days (UIWords 25079); it runs ActiveImpeachTime minutes and needs responses from
/// ActiveImpeachOnlineScale % of the members who logged in within ActiveImpeachOnlineDayNum days, the initiator counting
/// itself (25083). Enough responses make the initiator the master and the old master a member (25086, mail 1001); the
/// initiator may cancel (mail 1002, then ActiveImpeachCD hours before the next), leaving ends it (1003), and the deadline
/// fails it (1004). 1005/1006 announce it. The state reaches the client as GuildPeachInfo in 32213 and 32231 Change_Peach;
/// times are unix ms. ErrorCodeConfig 472-479.
/// (inferred) canPeach stays true while one runs, so the others can reach the Respond button; the second ground of 25079
/// (under ActiveImpeachWeekContribution Guild Contribution a week for ActiveImpeachChairmanWeekNum weeks) needs weekly
/// contribution, which this server does not keep, so only the offline ground applies. The count resolves as soon as it
/// is reached; the cooldown follows every end; every member gets the end mails.
/// </summary>
public sealed partial class GuildService
{
    public const uint Peach = 32235, PeachApply = 32236;
    private const int ImpeachItem = 3;

    private static bool Impeaching(GuildStore.State g) => g.ImpeachOwner != 0;

    /// <summary>The master's offline ground (25079 ${1} days); OfflineTime is unix s, -1 online.</summary>
    private bool CanImpeach(GuildStore.State g)
    {
        if (Impeaching(g)) return true;
        var master = g.Members.Values.FirstOrDefault(m => m.Official == 1);
        if (master == null) return false;
        long offline = master.RoleId is var id && Online(id) != null ? -1 : master.OfflineTime;
        return offline > 0 && NowSec() - offline >= Setting("ActiveImpeachChairmanOffineTimeLimit", 7)[0] * 86400;
    }

    private GuildPeachInfo PeachInfo(GuildStore.State g)
    {
        var info = new GuildPeachInfo
        {
            IsPeaching = Impeaching(g), NextPeachTime = g.NextImpeachMs, CanPeach = CanImpeach(g),
            PeachOwnerID = g.ImpeachOwner, NeedCount = g.ImpeachNeed, PeachEndTime = g.ImpeachEndMs,
        };
        info.PeachPlayerIDs.AddRange(g.ImpeachResponders);
        return info;
    }

    private Task PushPeach(GuildStore.State g) =>
        PushBase(g, new GuildBaseInfoChangeNtf { Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangePeach, Peach = PeachInfo(g) });

    /// <summary>25083: ceil(scale % of the members online now or within the last days), at least one.</summary>
    private int ImpeachNeed(GuildStore.State g)
    {
        long since = NowSec() - Setting("ActiveImpeachOnlineDayNum", 1)[0] * 86400;
        int recent = g.Members.Values.Count(m => Online(m.RoleId) != null || m.OfflineTime < 0 || m.OfflineTime >= since);
        long scale = Setting("ActiveImpeachOnlineScale", 50)[0];
        return (int)Math.Max(1, (recent * scale + 99) / 100);
    }

    private long ImpeachHours => Math.Max(1, (Setting("ActiveImpeachTime", 60)[0] + 59) / 60);

    public Task PeachAsync(GameSession s, GuildPeachReq req) =>
        RunGuildAsync(s, [], [], () => req.IsCancle ? CancelPeachCoreAsync(s) : StartPeachCoreAsync(s), characterGate: !req.IsCancle);

    public Task PeachApplyAsync(GameSession s) => RunGuildAsync(s, [], [], () => PeachApplyCoreAsync(s));

    private async Task StartPeachCoreAsync(GameSession s)
    {
        var g = Own(s);
        long now = NowMs();
        var cost = Setting("ActiveImpeachMoney", ImpeachItem, 30);
        var self = g?.Members.GetValueOrDefault(s.Role!.Id);
        int code = g == null || self == null ? ErrorCodes.NotInGuild : Impeaching(g) ? ErrorCodes.ImpeachInProgress
            : self.Official == 1 ? ErrorCodes.ImpeachSelf
            : now - self.EnterTime < Setting("ActiveImpeachJoinDay", 10080)[0] * 60_000 ? ErrorCodes.ImpeachRequirements
            : now < g.NextImpeachMs ? ErrorCodes.ImpeachCooldown
            : !CanImpeach(g) ? ErrorCodes.ImpeachNoGrounds : 0;
        if (code == 0)
        {
            g!.ImpeachOwner = s.Role!.Id;
            g.ImpeachEndMs = now + Setting("ActiveImpeachTime", 60)[0] * 60_000;
            g.ImpeachNeed = ImpeachNeed(g);
            g.ImpeachResponders = [s.Role.Id];
            bool paid = cost.Length < 2 || cost[1] <= 0 || await character.ExchangeLockedAsync(s, [((int)cost[0], cost[1])], [],
                persist: tx =>
                {
                    store.EnlistSave(g, tx, "guild disappeared during impeachment"); EnlistViews(g, tx);
                    tx.WithEconomy(GuildPayment(s, "guild-impeach", (int)cost[0], cost[1]));
                }, asyncCommit: true) != null;
            if (paid && (cost.Length < 2 || cost[1] <= 0)) await SaveGuildAsync(g);
            if (!paid)
            {
                g.ImpeachOwner = 0; g.ImpeachEndMs = 0; g.ImpeachNeed = 0; g.ImpeachResponders = [];
                code = ErrorCodes.ItemsShort;
            }
        }
        await s.SendGameAsync(Peach, new GuildPeachResp { Result = code });
        if (code != 0) return;
        if (Impeaching(g!) && g!.ImpeachResponders.Count >= g.ImpeachNeed) { await ResolveImpeachAsync(g, true); return; }
        await PushPeach(g!);
        string initiator = g!.Members[g.ImpeachOwner].Name, master = MasterName(g);
        long hours = ImpeachHours;
        await GuildMailAsync([g.ImpeachOwner], ImpeachMail(5), $"guild-impeach:{g.Id}:{g.ImpeachEndMs}:start", hours, (long)g.ImpeachNeed);
        await GuildMailAsync(g.Members.Keys.Where(r => r != g.ImpeachOwner), ImpeachMail(6), $"guild-impeach:{g.Id}:{g.ImpeachEndMs}:call",
            initiator, master, hours, (long)g.ImpeachNeed, initiator);
    }

    private async Task CancelPeachCoreAsync(GameSession s)
    {
        var g = Own(s);
        int code = g == null ? ErrorCodes.NotInGuild : !Impeaching(g) ? ErrorCodes.ImpeachNotInProgress
            : g.ImpeachOwner != s.Role!.Id ? ErrorCodes.ImpeachCancelState : 0;
        if (code != 0)
        {
            await s.SendGameAsync(Peach, new GuildPeachResp { Result = code, IsCancle = true });
            return;
        }
        string initiator = g!.Members[g.ImpeachOwner].Name, master = MasterName(g);
        long end = g.ImpeachEndMs;
        EndImpeach(g);
        await SaveGuildAsync(g);
        await s.SendGameAsync(Peach, new GuildPeachResp { Result = 0, IsCancle = true });
        await PushPeach(g);
        await GuildMailAsync(g.Members.Keys, ImpeachMail(2), $"guild-impeach:{g.Id}:{end}:cancel", initiator, master);
    }

    private async Task PeachApplyCoreAsync(GameSession s)
    {
        var g = Own(s);
        int code = g == null ? ErrorCodes.NotInGuild : !Impeaching(g) ? ErrorCodes.ImpeachNotInProgress
            : g.ImpeachResponders.Contains(s.Role!.Id) ? ErrorCodes.ImpeachRepeat : 0;
        if (code == 0)
        {
            g!.ImpeachResponders.Add(s.Role!.Id);
            if (g.ImpeachResponders.Count < g.ImpeachNeed) await SaveGuildAsync(g);
        }
        await s.SendGameAsync(PeachApply, new GuildPeachApplyResp { Result = code });
        if (code != 0) return;
        if (g!.ImpeachResponders.Count >= g.ImpeachNeed) await ResolveImpeachAsync(g, true);
        else await PushPeach(g);
    }

    /// <summary>The deadline passed (heartbeat reconciliation): it fails with mail 1004.</summary>
    private async Task ExpireImpeachmentsAsync()
    {
        long now = NowMs();
        foreach (var g in store.List().Where(g => g.ImpeachOwner != 0 && g.ImpeachEndMs <= now))
            await ResolveImpeachAsync(g, g.ImpeachResponders.Count >= g.ImpeachNeed);
    }

    /// <summary>
    /// Success hands the master's seat to the initiator (the old master becomes a member, journal 6 like a transfer) with
    /// mail 1001; failure is mail 1004. Either way the impeachment ends and the cooldown starts.
    /// </summary>
    private async Task ResolveImpeachAsync(GuildStore.State g, bool success)
    {
        long ownerId = g.ImpeachOwner, end = g.ImpeachEndMs;
        if (!g.Members.TryGetValue(ownerId, out var owner)) { EndImpeach(g); await SaveGuildAsync(g); await PushPeach(g); return; }
        var master = g.Members.Values.FirstOrDefault(m => m.Official == 1);
        string masterName = master?.Name ?? "";
        if (success && master != null)
        {
            master.Official = 20;
            owner.Official = 1;
            Journal(g, EvLeader, [master.RoleId, owner.RoleId], Str(master.Name), Str(owner.Name));
        }
        EndImpeach(g);
        await SaveGuildAsync(g);
        await PushPeach(g);
        if (success && master != null)
        {
            await RefreshMembers(g);
            await PushMember(g, owner, GuildMemberInfoChangeNtf.Types.ChangeType.Update);
            await PushMember(g, master, GuildMemberInfoChangeNtf.Types.ChangeType.Update);
            await GuildMailAsync(g.Members.Keys, ImpeachMail(1), $"guild-impeach:{g.Id}:{end}:success", owner.Name, masterName);
        }
        else await GuildMailAsync(g.Members.Keys, ImpeachMail(4), $"guild-impeach:{g.Id}:{end}:fail", owner.Name);
    }

    private void EndImpeach(GuildStore.State g)
    {
        g.ImpeachOwner = 0; g.ImpeachEndMs = 0; g.ImpeachNeed = 0; g.ImpeachResponders = [];
        g.NextImpeachMs = NowMs() + Setting("ActiveImpeachCD", 24)[0] * 3_600_000;
    }

    /// <summary>
    /// Members left the guild (leave, kick): the initiator's leaving ends the impeachment (mail 1003, 25104), a responder's
    /// response goes with it. Returns the mail to send after the roster is saved, or null.
    /// </summary>
    private Func<Task>? DropImpeacher(GuildStore.State g, IReadOnlyCollection<GuildStore.Member> leaving)
    {
        if (!Impeaching(g)) return null;
        if (leaving.FirstOrDefault(m => m.RoleId == g.ImpeachOwner) is { } owner)
        {
            string master = MasterName(g);
            long end = g.ImpeachEndMs;
            EndImpeach(g);
            return async () =>
            {
                await PushPeach(g);
                await GuildMailAsync(g.Members.Keys, ImpeachMail(3), $"guild-impeach:{g.Id}:{end}:left", owner.Name, master);
            };
        }
        return g.ImpeachResponders.RemoveAll(r => leaving.Any(m => m.RoleId == r)) > 0 ? () => PushPeach(g) : null;
    }

    private static string MasterName(GuildStore.State g) => g.Members.Values.FirstOrDefault(m => m.Official == 1)?.Name ?? "";

    /// <summary>GuildMail01..06 (MailConfig 1001-1006).</summary>
    private int ImpeachMail(int n) => (int)Setting($"GuildMail0{n}", 1000 + n)[0];

    private async Task GuildMailAsync(IEnumerable<long> recipients, int template, string key, params object[] args)
    {
        var to = recipients.Where(r => roles.Get(r) != null).Distinct().ToArray();
        if (to.Length == 0 || data.Row("MailConfig", template) == null) return;
        using (await mail.LockRecipientsAsync(to))
        {
            var transaction = roles.NewTransaction();
            foreach (long role in to) mail.QueueTemplateLocked(role, $"{key}:{role}", template, [], transaction, args);
            await roles.CommitAsync(transaction, "guild/impeach-mail");
        }
        foreach (long role in to)
            try { await mail.NotifyRecipientAsync(role); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or System.Net.Sockets.SocketException) { }
    }
}
