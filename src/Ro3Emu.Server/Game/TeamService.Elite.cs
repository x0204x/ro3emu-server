using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class TeamService
{
    /// <summary>
    /// The Guild League quick elite team (GuildService.EliteTeamAsync, 32313): the requester leads a TeamTypeForty team
    /// (a new one, or the party it leads converted as 42002 does) and every online elite outside a party is invited (42005).
    /// The guild chat card's link sends 42014, which admits the team's elites at once. (inferred) A requester in a party it
    /// does not lead is refused with AlreadyInParty; the client publishes no rule.
    /// </summary>
    public async Task<(int Code, long TeamId)> CreateEliteTeamAsync(GameSession s, IReadOnlyCollection<long> elites)
    {
        long self = s.Role!.Id;
        long[] online = elites.Where(id => id != self && Session(id) != null).Distinct().ToArray();
        (int Code, long TeamId) result = (0, 0);
        await RunTeamAsync("team/elite", online.Prepend(self), 0, async () => result = await CreateEliteTeamCoreAsync(s, elites, online));
        return result;
    }

    private async Task<(int Code, long TeamId)> CreateEliteTeamCoreAsync(GameSession s, IReadOnlyCollection<long> elites, long[] online)
    {
        long self = s.Role!.Id;
        Team team;
        if (_byMember.TryGetValue(self, out var existing))
        {
            if (existing.Leader != self) return (ErrorCodes.AlreadyInParty, 0);
            team = existing;
            if (team.Type != TeamType.Forty)
            {
                team.Type = TeamType.Forty;
                if (!ValidTarget(team.Target, team.Type)) team.Target = NoTarget;
                await ToOthersAsync(team, 0, TeamChangeTypeNotify, All(team));
            }
        }
        else
        {
            team = new Team
            {
                Id = ++_nextId, Type = TeamType.Forty, Leader = self, Target = NoTarget,
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Area = _options.Value.ClusterId.ToString(),
                PlayBook = _seasons.PlayBook,
            };
            _teams[team.Id] = team;
            await RecordAndJoinAsync(team, self);
            _log.LogInformation("{Name} creates elite team {Team}", s.Role.Name, team.Id);
            await s.SendGameAsync(TeamNewMember, new BroadCastNewMemberEnter { TeamType = team.Type, DetailInfo = Detail(team, self) });
            await s.SendGameAsync(TeamAll, All(team));
        }
        team.Elites.UnionWith(elites);
        DateTime expires = DateTime.UtcNow + InviteLife;
        long teamId = team.Id;
        foreach (long id in online)
        {
            if (_byMember.ContainsKey(id) || team.Members.Count >= Capacity(team) || Session(id) is not { } target) continue;
            _invites[(id, teamId)] = expires;
            long invited = id;
            _loop.ScheduleRuntime(InviteLife, () => RunTeamAsync("team/invite-expiry", [invited], teamId, () =>
            {
                if (_invites.TryGetValue((invited, teamId), out var current) && current == expires
                    && current <= DateTime.UtcNow) _invites.Remove((invited, teamId));
                return Task.CompletedTask;
            }));
            var simple = new TeamTotalSimpleInfo { Info = Simple(team) };
            simple.MemberList.Add(team.Members);
            await target.SendGameAsync(TeamNotifyInvitee, new NotifyInvitee { InviteGuid = self, TeamTotalSimpleInfo = simple });
        }
        return (0, teamId);
    }
}
