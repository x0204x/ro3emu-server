using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    /// <summary>Use the ordinary 30400 admission rules for a selected MatchConfig type 2 boss.</summary>
    public int MatchAdmission(GameSession s, DungeonCatalog.Plan plan)
    {
        int error = Admission(s, plan);
        return error != 0 ? error : Preparing(s) ? ErrorCodes.DungeonPreparing : 0;
    }

    /// <summary>
    /// The 82637 vote has already finished. The normal dungeon owner allocates the room and performs
    /// 30107/70010/30005/30401; no second 42033/30406 vote is issued (LC_DAL_Match and LC_DAL_Team).
    /// </summary>
    public async Task<int> BeginMatchedAsync(DungeonCatalog.Plan plan, GameSession[] members, long teamId)
    {
        if (members.Length == 0 || members.Distinct().Count() != members.Length
            || members.Any(s => s.Role == null || !_sessions.Contains(s))) return ErrorCodes.PlayerOffline;
        if (members.Length < plan.MinPlayers && !(members.Length == 1 && members[0].DungeonSoloMatch))
            return ErrorCodes.DungeonTeamSize;
        if (members.Length > plan.MaxPlayers) return ErrorCodes.PartyFull;
        foreach (var member in members)
        {
            int error = MatchAdmission(member, plan);
            if (error != 0) return error;
        }
        if (members.Length > 1 && (teamId == 0 || _teams.TeamOf(members[0].Role!.Id).TeamId != teamId
            || !_teams.MembersOf(members[0].Role!.Id).Order().SequenceEqual(members.Select(s => s.Role!.Id).Order())))
            return ErrorCodes.MemberLeft;
        await BeginAsync(plan, members, members[0].Role!.Id, teamId, matched: true);
        return 0;
    }
}
