using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Guild League elite roster (MG_Guild EliteGroup, label LeagueLabel): who may enter the Main Battlefield
/// (GuildLeagueService.EntryError, ErrorCodeConfig 518). 32303 reads it, 32302 saves it against its version, 32321 pushes
/// it to the guild, and each member's own standing is GuildFullInfoNtf.isLeagueElite / 32231 Change_LeagueElite, which
/// the client's Main Battlefield entry reads (UI_Sys_GuildLeague_BattleWidgetCtrl.lua:238-262, LC_GuildLeagueHelper.SelfIsElite).
/// Editing needs GuildPermissionConfig 20 EliteSetting (UI_Sys_Guild_Manage_EliteWidgetCtrl.lua); 32313 forms a 40-person team
/// of the elites with a guild chat card (template 102 GuildTeam).
/// </summary>
public sealed partial class GuildService
{
    public const uint EliteSave = 32302, EliteQuery = 32303, EliteTeam = 32313, EliteChange = 32321;
    private const int EliteRight = 20;

    /// <summary>GuildGameplayMember01 [max, comparison, position] (LC_GuildHelper.GetEliteRight: op 6 is "position &lt;= 20").</summary>
    private long[] EliteRule => Setting("GuildGameplayMember01", 40, 6, 20);
    internal int LeagueEliteMax => (int)Math.Clamp(EliteRule[0], 1, 40);

    private bool ElitePositionOk(int official)
    {
        var rule = EliteRule;
        if (rule.Length < 3) return true;
        long a = official, b = rule[2];
        return rule[1] switch { 1 => a == b, 2 => a != b, 3 => a > b, 4 => a >= b, 5 => a < b, 6 => a <= b, _ => true };
    }

    /// <summary>The roster's live slots: a member who left keeps no slot.</summary>
    internal static Dictionary<int, long> LeagueElites(GuildStore.State g) =>
        g.LeagueElites.Where(p => g.Members.ContainsKey(p.Value)).ToDictionary(p => p.Key, p => p.Value);

    internal static bool IsLeagueElite(GuildStore.State g, long role) =>
        g.Members.ContainsKey(role) && g.LeagueElites.ContainsValue(role);

    private static EliteGroup EliteGroupOf(GuildStore.State g)
    {
        var group = new EliteGroup { LabelType = EnumLabelType.LeagueLabel, Version = g.LeagueEliteVersion };
        group.ElitUnitData.AddRange(LeagueElites(g).OrderBy(p => p.Key).Select(p => new EliteUnit { Index = p.Key, PlayerId = p.Value }));
        return group;
    }

    public Task EliteQueryAsync(GameSession s) => RunGuildAsync(s, [], [], () => EliteQueryCoreAsync(s));
    public Task EliteSaveAsync(GameSession s, QuickJoinEliteGroupReq req) => RunGuildAsync(s, [], [], () => EliteSaveCoreAsync(s, req));

    /// <summary>32303: the roster (an empty group outside a guild, LC_DAL_Guild._processEliteResp iterates it).</summary>
    private Task EliteQueryCoreAsync(GameSession s)
    {
        var rsp = new GetEliteGroupDataRes();
        if (Own(s) is { } g) rsp.EliteGroupData.Add(EliteGroupOf(g));
        return s.SendGameAsync(EliteQuery, rsp);
    }

    /// <summary>
    /// 32302: the whole roster as the client edited it, with the version it read (UI_Sys_Guild_EliteSelection*). A stale
    /// version is 10047, a non-member 10046, a position outside GuildGameplayMember01 10048. The answer always carries the
    /// server's roster: the client replaces its copy with it (LC_DAL_Guild._processEliteGroupResp).
    /// </summary>
    private async Task EliteSaveCoreAsync(GameSession s, QuickJoinEliteGroupReq req)
    {
        var g = Own(s);
        var proposed = req.CurrentEliteData;
        int max = LeagueEliteMax;
        int code = g == null ? ErrorCodes.NotInGuild : !HasRight(g, s.Role!.Id, EliteRight) ? ErrorCodes.GuildPermission
            : proposed == null || proposed.LabelType != EnumLabelType.LeagueLabel ? ErrorCodes.Parameter
            : proposed.Version != g.LeagueEliteVersion ? ErrorCodes.EliteChanged : 0;
        var next = new Dictionary<int, long>();
        if (code == 0)
            foreach (var unit in proposed!.ElitUnitData.Where(u => u.PlayerId != 0))
            {
                if (unit.Index < 1 || unit.Index > max || next.ContainsKey(unit.Index) || next.ContainsValue(unit.PlayerId))
                { code = ErrorCodes.Parameter; break; }
                if (!g!.Members.TryGetValue(unit.PlayerId, out var member)) { code = ErrorCodes.EliteNotMember; break; }
                if (!ElitePositionOk(member.Official)) { code = ErrorCodes.ElitePosition; break; }
                next[unit.Index] = unit.PlayerId;
            }
        if (code != 0 || g == null)
        {
            var rejected = new QuickJoinEliteGroupRes { Result = code };
            if (g != null) rejected.CurrentEliteData = EliteGroupOf(g);
            await s.SendGameAsync(EliteSave, rejected);
            return;
        }
        var before = LeagueElites(g).Values.ToHashSet();
        g.LeagueElites = next;
        g.LeagueEliteVersion++;
        await SaveGuildAsync(g);
        await s.SendGameAsync(EliteSave, new QuickJoinEliteGroupRes { Result = 0, CurrentEliteData = EliteGroupOf(g) });
        await PushElitesAsync(g, before);
    }

    /// <summary>32321 to the online members, then 32231 Change_LeagueElite to each member whose own standing changed.</summary>
    private async Task PushElitesAsync(GuildStore.State g, IReadOnlySet<long> before)
    {
        var notice = new EliteGroupChangeNotify();
        notice.CurrentEliteData.Add(EliteGroupOf(g));
        var after = LeagueElites(g).Values.ToHashSet();
        foreach (var m in g.Members.Values)
        {
            if (Online(m.RoleId) is not { } peer) continue;
            await peer.SendGameAsync(EliteChange, notice);
            if (before.Contains(m.RoleId) != after.Contains(m.RoleId))
                await peer.SendGameAsync(BaseChange, new GuildBaseInfoChangeNtf
                { Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeLeagueElite, IsLeagueElite = after.Contains(m.RoleId) });
        }
    }

    /// <summary>A member left: the slot is freed and the version moves on, so an editor holding the old roster refetches.</summary>
    private static bool DropElite(GuildStore.State g, long role)
    {
        var slots = g.LeagueElites.Where(p => p.Value == role).Select(p => p.Key).ToArray();
        foreach (int slot in slots) g.LeagueElites.Remove(slot);
        if (slots.Length > 0) g.LeagueEliteVersion++;
        return slots.Length > 0;
    }

    /// <summary>
    /// Mail 1125 text 10140100124: with fewer than GuildLeagueEliteNum elites when the match begins the system fills the
    /// roster. (inferred) Free slots take the eligible members by position (leader first), then level, then id; the
    /// league decides who is eligible. Returns the number added.
    /// </summary>
    internal async Task<int> FillLeagueElitesAsync(long guildId, Func<GuildStore.Member, bool> eligible)
    {
        int added = 0;
        await RunGuildAsync(null, [guildId], [], async () =>
        {
            if (store.Get(guildId) is not { } g) return;
            var live = LeagueElites(g);
            int max = LeagueEliteMax;
            var candidates = g.Members.Values.Where(m => !live.ContainsValue(m.RoleId) && ElitePositionOk(m.Official) && eligible(m))
                .OrderBy(m => m.Official).ThenByDescending(m => m.Level).ThenBy(m => m.RoleId).Select(m => m.RoleId).ToList();
            var before = live.Values.ToHashSet();
            for (int slot = 1; slot <= max && candidates.Count > 0; slot++)
            {
                if (live.ContainsKey(slot)) continue;
                live[slot] = candidates[0];
                candidates.RemoveAt(0);
                added++;
            }
            if (added == 0) return;
            g.LeagueElites = live;
            g.LeagueEliteVersion++;
            await SaveGuildAsync(g);
            await PushElitesAsync(g, before);
        });
        return added;
    }

    /// <summary>
    /// 32313 (UI_Sys_Guild_Manage_EliteWidgetCtrl._formTeam, UIWords 170134 "Invite all Elite Members to the party?"): the
    /// EliteSetting holder leads a TeamTypeForty team that invites the online elites, and the guild channel gets a GuildTeam
    /// card (LC_ChatMessageHandler: text 170138 and the 34075 "Tap to Join Party" link). 10059 when the roster holds no
    /// elite besides the requester (inferred threshold).
    /// </summary>
    public async Task EliteTeamAsync(GameSession s)
    {
        var g = Own(s);
        long self = s.Role?.Id ?? 0;
        long[] elites = g == null ? [] : [.. LeagueElites(g).Values];
        int code = g == null ? ErrorCodes.NotInGuild : !HasRight(g, self, EliteRight) ? ErrorCodes.GuildPermission
            : !elites.Any(id => id != self) ? ErrorCodes.EliteTeamShort : 0;
        long teamId = 0;
        if (code == 0) (code, teamId) = await teams.CreateEliteTeamAsync(s, elites);
        await s.SendGameAsync(EliteTeam, new QuickCreateEliteTeamRes { Result = code });
        if (code == 0) await chat.PublishGuildTeamAsync(s, g!.Id, teamId, data.Text(170138));
    }
}
