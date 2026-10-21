using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

public sealed partial class TeamService
{
    private ReservationKey[] TeamKeys(IEnumerable<long> roles, long teamId = 0)
    {
        var ids = roles.Where(id => id > 0).ToHashSet();
        var teams = ids.Where(_byMember.ContainsKey).Select(id => _byMember[id]).ToHashSet();
        if (_teams.TryGetValue(teamId, out var requested)) teams.Add(requested);
        foreach (var team in teams) ids.UnionWith(team.Members);
        return ids.Select(id => ReservationKey.Role(id))
            .Concat(teams.Select(team => ReservationKey.Entity("team", team.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))))
            .Concat(teamId > 0 ? new[] { ReservationKey.Entity("team", teamId.ToString(System.Globalization.CultureInfo.InvariantCulture)) }
                : Array.Empty<ReservationKey>())
            .Distinct().ToArray();
    }

    private async Task RunTeamAsync(string origin, IEnumerable<long> roles, long teamId, Func<Task> work)
    {
        var owners = _loop.Owners ?? throw new InvalidOperationException("Team owner registry is unavailable.");
        long[] participants = roles.ToArray();
        while (true)
        {
            var keys = TeamKeys(participants, teamId);
            bool inherited = owners.Current is { RuntimeOnly: false };
            Task[] Handovers() => keys.Where(key => key.Kind == ReservationKind.RolePart)
                .Select(key => long.TryParse(key.Id, out long id) ? owners.Cached(id) : null)
                .Where(owner => owner?.State == RoleOwnerState.Quiescing)
                .Select(owner => owner!.Detaching).OfType<Task>().Distinct().ToArray();
            if (!inherited && Handovers() is { Length: > 0 } pending)
            {
                await Task.WhenAll(pending);
                continue;
            }
            var scope = await owners.EnterAsync(origin, keys);
            OwnerNotices? notices = null;
            bool changed = false;
            Task[] admittedHandovers = [];
            try
            {
                changed = !keys.ToHashSet().SetEquals(TeamKeys(participants, teamId));
                if (!inherited && !changed) admittedHandovers = Handovers();
                if (!changed && admittedHandovers.Length == 0)
                {
                    foreach (var key in keys.Where(key => key.Kind == ReservationKind.RolePart))
                        if (long.TryParse(key.Id, out long id) && Session(id)?.Owner is { } participant)
                            scope.Context.Pin(participant);
                    if (scope.Context.Notices is null) notices = new OwnerNotices(scope.Context);
                    await work();
                }
            }
            finally
            {
                notices?.Dispose();
                scope.Dispose();
            }
            if (changed) continue;
            if (admittedHandovers.Length != 0)
            {
                await Task.WhenAll(admittedHandovers);
                continue;
            }
            if (notices != null) await notices.FlushAsync();
            return;
        }
    }

    private Task RunTeamAsync(GameSession session, string origin, Func<Task> work,
        long extraRole = 0, long teamId = 0) =>
        RunTeamAsync(origin, [session.Role!.Id, extraRole], teamId, work);

    private async Task<T> RunTeamAsync<T>(string origin, IEnumerable<long> roles, Func<Task<T>> work)
    {
        T result = default!;
        await RunTeamAsync(origin, roles, 0, async () => result = await work());
        return result;
    }

    private async Task RecordAndJoinAsync(Team team, long role)
    {
        if (team.Members.Count != 0)
            await _friends.RecordTeammatesAsync(role, team.Members);
        Join(team, role);
    }
}
