using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

public sealed partial class FriendExtraService
{
    /// <summary>The highest FriendlinessConfig._iFriendNum, the intimacy cap (as a gift's).</summary>
    private const long MaxIntimacy = 180000;

    /// <summary>
    /// ActionConfig 107 GainIntimacy as a Connect quest's 116 gives it (TaskService.Connect.cs): the role and each
    /// online friend among <paramref name="partners"/> gain <paramref name="amount"/>, one value for both directions as
    /// a gift writes it, then both are told (onRelationDataChange). Runs after the quest's delivery, on the role's loop
    /// (inferred: an offline party member is not counted as together).
    /// </summary>
    public void GainIntimacy(GameSession s, IReadOnlyList<long> partners, int amount)
    {
        if (s.Role == null || amount <= 0) return;
        long[] online = [.. partners.Where(id => id != s.Role.Id && Online(id) != null).Distinct()];
        if (online.Length == 0) return;
        // The partners' role keys are declared with the role's own before the work is admitted.
        loop.PostOwned(s, [.. online.Select(id => ReservationKey.Role(id))],
            () => RunSocialAsync(s, "friend/sociality", online, () => GainCoreAsync(s, online, amount)), "friend sociality");
    }

    private async Task GainCoreAsync(GameSession s, long[] partners, int amount)
    {
        if (s.Role is not { } role) return;
        var tx = roles.NewTransaction();
        var changed = new List<(long Friend, int Intimacy)>();
        foreach (long other in partners)
        {
            if (Online(other) == null || !await friends.AreFriendsAsync(role.Id, other)) continue;
            var vector = await roles.ReadRevisionVectorAsync(other);
            if (vector.Deleted) continue;
            int intimacy = (int)Math.Min(MaxIntimacy, (long)await friends.IntimacyAsync(role.Id, other) + amount);
            tx.ExpectRole(vector);
            tx.FriendIntimacy(role.Id, other, intimacy);
            tx.FriendIntimacy(other, role.Id, intimacy);
            changed.Add((other, intimacy));
        }
        if (changed.Count == 0) return;
        await roles.CommitAsync(tx, "friend/sociality");
        foreach (var (friend, intimacy) in changed)
        {
            await micro.IntimacyChangedAsync(role.Id, friend, intimacy);
            await micro.IntimacyChangedAsync(friend, role.Id, intimacy);
        }
    }
}
