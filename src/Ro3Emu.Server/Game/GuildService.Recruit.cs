using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// 82211 PULLRECOMMENDPLAYERS for the guild. Label 3 fills the Members >
/// Member Recruitment list (UI_Sys_Guild_Member_RecommendWidgetCtrl.lua:57 open, :495 refresh; LC_DAL_Guild.lua:1192-1229
/// keeps only playerId); label 4 the founder's responder invite list (LC_DAL_Guild.lua:1138-1190; its sender has no
/// caller in this client). The handler drops replies of any other label, so the label is always echoed.
/// <list type="bullet">
/// <item>(inferred) Candidates: online, guildless, accepting invitations, at least the guild's join level, closest level
/// first, at most GuildRecruitInviteListNum (20); isInvited marks a live invitation from this guild.</item>
/// <item>(inferred) Label 3 needs the recruit right (GuildPermissionConfig 7), label 4 the founder of a guild still being
/// founded. One pull per role every few seconds, else 10023 "System-recommended player actions are too frequent".</item>
/// </list>
/// </summary>
public sealed partial class GuildService
{
    public const uint RecommendPlayers = 82211;
    public const int ErrRecommendTooFrequent = 10023;
    private const long RecommendCooldownMs = 3000;
    private readonly Dictionary<long, long> _lastRecommendMs = new();

    public Task RecommendPlayersAsync(GameSession s, PullRecommendPlayersReq req) =>
        RunGuildAsync(s, [], [], () => RecommendPlayersCoreAsync(s, req));

    private Task RecommendPlayersCoreAsync(GameSession s, PullRecommendPlayersReq req)
    {
        var resp = new PullRecommendPlayersRes { Type = req.Type, Label = req.Label };
        var g = Own(s);
        long now = NowMs();
        resp.Result = s.Role == null || g == null ? ErrorCodes.NotInGuild
            : req.Label == RecommendLabel.InviteJoinGuild
                ? g.Status != (int)GuildStatus.Normal ? ErrorCodes.FeatureUnavailable
                : !HasRight(g, s.Role.Id, 7) ? ErrorCodes.GuildPermission : 0
            : req.Label == RecommendLabel.InviteJoinGuildHelper
                ? g.Status != (int)GuildStatus.PreCreate || g.Members[s.Role.Id].Official != 1 ? ErrorCodes.GuildPermission : 0
            : ErrorCodes.Parameter;
        if (resp.Result == 0 && _lastRecommendMs.TryGetValue(s.Role!.Id, out long last) && now - last < RecommendCooldownMs)
            resp.Result = ErrRecommendTooFrequent;
        if (resp.Result == 0)
        {
            _lastRecommendMs[s.Role!.Id] = now;
            int level = s.Progress!.Level;
            var invited = _invites.Values.Where(i => i.GuildId == g!.Id && i.ExpiresMs > now).Select(i => i.Recipient).ToHashSet();
            var picks = sessions.All()
                .Where(p => p.Role != null && p.Progress != null && p.Role.Id != s.Role.Id)
                .Where(p => p.Progress!.Level >= g!.JoinLevelLimit && !(p.Guild?.CantBeInvited ?? false))
                .Where(p => store.ForRole(p.Role!.Id) == null)
                .OrderBy(p => Math.Abs(p.Progress!.Level - level)).ThenBy(p => p.Role!.Id)
                .Take((int)Math.Clamp(Setting("GuildRecruitInviteListNum", 20)[0], 1, 100));
            resp.RecommendList.AddRange(picks.Select(p => new RecommendPlayerInfo
            { PlayerId = p.Role!.Id, Area = options.Value.ClusterId, IsInvited = invited.Contains(p.Role.Id) }));
        }
        return s.SendGameAsync(RecommendPlayers, resp);
    }
}
