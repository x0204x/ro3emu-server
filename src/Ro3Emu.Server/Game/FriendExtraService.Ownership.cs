using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

public sealed partial class FriendExtraService
{
    private async Task RunSocialAsync(GameSession session, string origin, IEnumerable<long> participants,
        Func<Task> work, string? groupId = null)
    {
        var owners = loop.Owners ?? throw new InvalidOperationException("Social owner registry is unavailable.");
        long[] participantIds = [.. participants.Append(session.Role!.Id).Where(id => id > 0).Distinct()];
        var keys = participantIds
            .Select(id => ReservationKey.Role(id)).ToList();
        if (long.TryParse(groupId, out long group) && group > 0)
            keys.Add(ReservationKey.Entity("friend_group", group.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        while (true)
        {
            bool inherited = owners.Current is { RuntimeOnly: false };
            // A peer may still be online while its attachment is draining. Wait before taking
            // any role or group key, then recheck after admission if another intent held a key.
            Task[] Handovers() => [.. keys.Where(key => key.Kind == ReservationKind.RolePart)
                .Select(key => long.TryParse(key.Id, out long id) ? owners.Cached(id) : null)
                .Where(owner => owner?.State == RoleOwnerState.Quiescing)
                .Select(owner => owner!.Detaching).OfType<Task>().Distinct()];
            if (!inherited && Handovers() is { Length: > 0 } pending)
            {
                await Task.WhenAll(pending);
                continue;
            }
            var scope = await owners.EnterAsync(origin, [.. keys]);
            OwnerNotices? notices = null;
            Task[] admittedHandovers = [];
            try
            {
                if (!inherited) admittedHandovers = Handovers();
                if (admittedHandovers.Length == 0)
                {
                    if (session.Owner is { } sender) scope.Context.Pin(sender);
                    foreach (long id in participantIds)
                        if (Online(id)?.Owner is { } participant && participant.RoleId != session.Role!.Id)
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
            if (admittedHandovers.Length != 0)
            {
                await Task.WhenAll(admittedHandovers);
                continue;
            }
            if (notices != null) await notices.FlushAsync();
            return;
        }
    }

    // Read the roster without a reservation, then verify it after admission. A changed roster
    // restarts with its new complete participant set; no provisional keys remain held.
    private async Task RunGroupAsync(GameSession session, string origin, string id, IEnumerable<long> extra,
        Func<FriendStore.ChatGroup?, Task> work)
    {
        while (true)
        {
            var staged = await friends.GroupAsync(id);
            var participants = (staged?.Members.Select(member => member.Role) ?? [])
                .Concat(extra).Distinct().ToArray();
            bool changed = false;
            await RunSocialAsync(session, origin, participants, async () =>
            {
                var current = await friends.GroupAsync(id);
                changed = staged?.Owner != current?.Owner || staged?.Name != current?.Name
                    || !(staged?.Members.Select(member => member.Role) ?? [])
                        .SequenceEqual(current?.Members.Select(member => member.Role) ?? []);
                if (!changed) await work(current);
            }, id);
            if (!changed) return;
        }
    }
}
