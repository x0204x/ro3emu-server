using Romsg;
using static Ro3Emu.Server.Game.ChickenRules;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Survivor Battle landing follow, on the ChoosePos map panel (UI_Sys_Chicken_MapPanelModel): a player follows a
/// teammate (and lands with it), or invites teammates to follow it. The client sends only 82628; the team's relation
/// comes back whole in 82673 and an invitation in 82674.
/// </summary>
public sealed partial class TimedBattleService
{
    public const uint SelectFollow = 82628, FollowNotify = 82673, InviteFollowNotify = 82674;

    /// <summary>PublicParam InviteCdChicken 5 (s): the client's own invite cooldown, kept on the server as well.</summary>
    private const long InviteCdSec = 5;
    private const int FollowChainLimit = 10; // UI_Sys_Chicken_MapPanelModel.GetFollowLeader walks at most 10 hops

    /// <summary>The end of the follow chain that starts at this role (the role itself when it follows nobody).</summary>
    private static long FollowLeader(ChickenRun run, long role)
    {
        long at = role;
        for (int i = 0; i < FollowChainLimit && run.Follow.TryGetValue(at, out long next); i++) at = next;
        return at;
    }

    /// <summary>
    /// 82628 SelectFollowReq{type, roleId, followedRoleId} (LC_DAL_Chicken.SendChickenFollow):
    /// Followed (2) {self, target}: follow a teammate (also accepting its invite); Cancle (0) {self, target}: stop;
    /// Invite (1) {target or 0 = every teammate, self}: invite. The checks mirror the panel (CanShowFollow,
    /// CanShowInvite): no following inside one's own follow group or someone who follows another; no invite while
    /// following, before a landing pick, or inside the invite cooldown. The response handler is empty.
    /// </summary>
    public async Task SelectFollowAsync(GameSession session, SelectFollowReq req)
    {
        int result = ErrorCodes.Parameter;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken follow", async () =>
            {
                if (match.Settled || run.Stage != StageChoosePos) { result = ErrorCodes.FeatureUnavailable; return; }
                long self = match.Participants[session].Id;
                var team = Team(match, match.Faction[session]).Where(p => p != session && !match.Absent.Contains(p))
                    .ToDictionary(p => match.Participants[p].Id);
                switch (req.Type)
                {
                    case EChickenFollowNotifyType.Followed:
                        if (req.RoleId != self || !team.ContainsKey(req.FollowedRoleId) || run.Follow.ContainsKey(req.FollowedRoleId)
                            || FollowLeader(run, req.FollowedRoleId) == FollowLeader(run, self)) return;
                        run.Follow[self] = req.FollowedRoleId;
                        break;
                    case EChickenFollowNotifyType.Cancle:
                        if (req.RoleId != self || run.Follow.GetValueOrDefault(self) != req.FollowedRoleId || req.FollowedRoleId == 0) return;
                        run.Follow.Remove(self);
                        break;
                    case EChickenFollowNotifyType.Invite:
                        if (req.FollowedRoleId != self || run.Follow.ContainsKey(self) || !me.LandChosen) return;
                        long now = NowMs() / 1000;
                        GameSession[] invited = req.RoleId == 0 ? [.. team.Values]
                            : team.TryGetValue(req.RoleId, out var one) ? [one] : [];
                        invited = [.. invited.Where(p => run.InviteCdSec.GetValueOrDefault((self, match.Participants[p].Id)) <= now
                            && FollowLeader(run, match.Participants[p].Id) != self)];
                        if (invited.Length == 0) { result = ErrTooFrequent; return; }
                        foreach (var invitee in invited)
                        {
                            run.InviteCdSec[(self, match.Participants[invitee].Id)] = now + InviteCdSec;
                            if (InRoom(match).Contains(invitee))
                                await invitee.SendGameAsync(InviteFollowNotify, new ChickenInviteFollowNotify { FollowedRoleId = self });
                        }
                        break;
                    default:
                        return;
                }
                result = 0;
                await SendFollowAsync(match, run, InRoom(match).Where(p => match.Faction[p] == match.Faction[session]));
            });
        await session.SendGameAsync(SelectFollow, new SelectFollowResp { Result = result });
    }

    /// <summary>
    /// 82673 ChickenSelectFollowResp{followedMap follower -> followed, inviteCd invitee -> end in seconds} to these
    /// players of one team: LC_DAL_Chicken._processCheckFollowNotify replaces the team relation and pairs() both maps
    /// unguarded, so an empty one carries a wire-only 0 -> 0 (no player is 0; GetFollowLeader stops on it).
    /// </summary>
    private async Task SendFollowAsync(Match match, ChickenRun run, IEnumerable<GameSession> to)
    {
        foreach (var group in to.GroupBy(p => match.Faction[p]))
        {
            var ids = Team(match, group.Key).Select(p => match.Participants[p].Id).ToHashSet();
            var notify = new ChickenSelectFollowResp();
            foreach (var (follower, followed) in run.Follow.Where(kv => ids.Contains(kv.Key))) notify.FollowedMap[follower] = followed;
            long now = NowMs() / 1000;
            foreach (var ((inviter, invitee), end) in run.InviteCdSec.Where(kv => ids.Contains(kv.Key.Inviter) && kv.Value > now))
                notify.InviteCd[invitee] = Math.Max(end, notify.InviteCd.GetValueOrDefault(invitee));
            if (notify.FollowedMap.Count == 0) notify.FollowedMap[0] = 0;
            if (notify.InviteCd.Count == 0) notify.InviteCd[0] = 0;
            await RoleFanout.EachAsync(group, p => p.SendGameAsync(FollowNotify, notify));
        }
    }

    /// <summary>A player who leaves drops out of the follow relation (its followers stop following).</summary>
    private async Task DropFollowAsync(Match match, ChickenRun run, GameSession player)
    {
        long id = match.Participants[player].Id;
        bool changed = run.Follow.Remove(id);
        foreach (long follower in run.Follow.Where(kv => kv.Value == id).Select(kv => kv.Key).ToArray())
            changed |= run.Follow.Remove(follower);
        if (changed && run.Stage == StageChoosePos)
            await SendFollowAsync(match, run, InRoom(match).Where(p => match.Faction[p] == match.Faction[player]));
    }
}
