using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class TeamService
{
    /// <summary>Authoritative, online five-person-party snapshot for MatchConfig type 2.</summary>
    public (int Error, GameSession[] Members, long TeamId) MatchGroup(GameSession requester)
    {
        if (requester.Role is not { } role) return (ErrorCodes.Parameter, [], 0);
        if (!_byMember.TryGetValue(role.Id, out var team)) return (0, [requester], 0);
        if (team.Type != TeamType.Five || team.Leader != role.Id) return (ErrorCodes.NoPermission, [], 0);
        var members = team.Members.Select(Session).ToArray();
        if (members.Any(s => s?.Role == null)) return (ErrorCodes.PlayerOffline, [], 0);
        return (0, members.Select(s => s!).OrderBy(s => s == requester ? 0 : 1).ToArray(), team.Id);
    }

    /// <summary>Compare the queued party snapshot with current membership before ready and admission.</summary>
    public bool MatchGroupValid(long leader, long teamId, IReadOnlyList<long> members)
    {
        if (members.Count == 0 || !members.Contains(leader)) return false;
        if (teamId == 0) return members.Count == 1 && !_byMember.ContainsKey(leader);
        return _teams.TryGetValue(teamId, out var team) && team.Type == TeamType.Five
            && team.Leader == leader && team.Members.Count == members.Count
            && team.Members.Order().SequenceEqual(members.Order());
    }

    /// <summary>
    /// Form a matched party after all 82637 votes, preserving a full preformed party. Merging partial parties
    /// into one five-member party and publishing 42026/42048 is (inferred); the client only proves that a
    /// five-person match has a normal party/roster when entering the dungeon.
    /// </summary>
    public async Task<long> CreateMatchedPartyAsync(GameSession[] members)
    {
        try
        {
            return await RunTeamAsync("team/matched-party", members.Where(member => member.Role != null)
                .Select(member => member.Role!.Id), () => CreateMatchedPartyCoreAsync(members));
        }
        catch (OperationCanceledException) when (members.Any(member =>
            member.Owner?.State == Ownership.RoleOwnerState.Quiescing))
        {
            // Match ready is still under its inherited Realm. Waiting for this member's handover
            // would deadlock cleanup; tell match admission that the group changed instead.
            return 0;
        }
    }

    private async Task<long> CreateMatchedPartyCoreAsync(GameSession[] members)
    {
        if (members.Length is < 2 or > 5 || members.Any(s => s.Role == null)
            || members.Select(s => s.Role!.Id).Distinct().Count() != members.Length)
            return 0;
        var ids = members.Select(s => s.Role!.Id).ToArray();
        if (ids.Any(id => Session(id) == null)) return 0;
        if (_byMember.TryGetValue(ids[0], out var existing) && existing.Type == TeamType.Five
            && existing.Members.Count == ids.Length && existing.Members.Order().SequenceEqual(ids.Order())) return existing.Id;
        var sourceTeams = ids.Where(_byMember.ContainsKey).Select(id => _byMember[id]).Distinct().ToArray();
        if (sourceTeams.Any(t => t.Type != TeamType.Five || t.Members.Any(id => !ids.Contains(id)))) return 0;
        await _friends.RecordMatchedTeammatesAsync(ids);
        foreach (var old in sourceTeams)
        {
            _teams.Remove(old.Id);
            foreach (long id in old.Members) _byMember.Remove(id);
        }
        var team = new Team
        {
            Id = ++_nextId, Type = TeamType.Five, Leader = ids[0], Target = NoTarget,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Area = _options.Value.ClusterId.ToString(),
            PlayBook = _seasons.PlayBook,
        };
        _teams[team.Id] = team;
        foreach (long id in ids) Join(team, id);
        foreach (var member in members)
        {
            try
            {
                await member.SendGameAsync(TeamNewMember, new BroadCastNewMemberEnter
                { TeamType = team.Type, DetailInfo = Detail(team, member.Role!.Id) });
                await member.SendGameAsync(TeamAll, All(team));
            }
            catch (Exception e) { _log.LogWarning(e, "Matched party publication failed for {Role}", member.Role?.Id); }
        }
        return team.Id;
    }
}
