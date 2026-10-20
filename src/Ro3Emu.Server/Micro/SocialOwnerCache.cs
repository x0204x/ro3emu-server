using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using System.Text.Json;

namespace Ro3Emu.Server.Micro;

/// <summary>Loop-owned detached projections. An offline role has the same owner and revision checks as an attached role.</summary>
public sealed class SocialOwnerCache(AccountStore accounts, OwnerRegistry owners, GameLoop loop)
{
    private readonly Dictionary<long, SocialSnapshot> _social = [];
    private readonly Dictionary<long, ProfileSnapshot> _profiles = [];
    internal long SocialSnapshotLoads { get; private set; }

    internal SocialSnapshot? Social(long role)
    {
        AssertLoop();
        return _social.TryGetValue(role, out var value) && Current(value.Vector, RevisionStore.FriendRelation)
            ? value : null;
    }

    internal async Task<IReadOnlyDictionary<long, SocialSnapshot?>> LoadSocialAsync(IEnumerable<long> roles, bool force = false)
    {
        AssertLoop();
        var ids = roles.Distinct().ToArray();
        var missing = ids.Where(id => force || Social(id) is null).ToArray();
        if (missing.Length != 0)
        {
            // This staging scope ends before command admission. Take the whole requested reader set
            // together so a cold snapshot cannot observe SQL commit ahead of its owner's Apply.
            using var scope = await owners.EnterAsync("social/snapshot",
                ids.Select(id => ReservationKey.Role(id)).ToArray());
            missing = ids.Where(id => force || Social(id) is null).ToArray();
            if (missing.Length == 0) return ids.ToDictionary(id => id, id => Social(id));
            SocialSnapshotLoads++;
            var loaded = await accounts.ReadAsync<Dictionary<long, SocialSnapshot?>>(new SocialSnapshotReadPlan(missing));
            // Publish the complete reader cut only after every owner has passed its lifecycle and revision checks.
            foreach (var snapshot in loaded.Values.OfType<SocialSnapshot>())
            {
                var owner = owners.GetOrRegister(snapshot.Vector);
                if (!Matches(owner, snapshot.Vector, RevisionStore.FriendRelation))
                    throw new OwnerRevisionConflictException();
            }
            foreach (var (id, snapshot) in loaded)
            {
                if (snapshot is null) _social.Remove(id);
                else _social[id] = snapshot;
            }
        }
        return ids.ToDictionary(id => id, id => Social(id));
    }

    internal ProfileSnapshot? Profile(long role)
    {
        AssertLoop();
        return _profiles.TryGetValue(role, out var value) && Current(value.Vector,
            RevisionStore.Identity, RevisionStore.Progress, "wardrobe", "guild_info", "personal", "personal_upvote", "popularity",
            "social_outfit", RevisionStore.GuildMembership)
            ? value : null;
    }

    internal async Task<IReadOnlyDictionary<long, ProfileSnapshot?>> LoadProfilesAsync(IEnumerable<long> roles,
        IReadOnlyDictionary<long, GuildStamp> currentGuilds)
    {
        AssertLoop();
        var ids = roles.Distinct().ToArray();
        var missing = ids.Where(id => Profile(id) is not { } cached || cached.GuildStamp != currentGuilds[id]).ToArray();
        if (missing.Length != 0)
        {
            var loaded = await accounts.ReadAsync<Dictionary<long, ProfileSnapshot?>>(new ProfileSnapshotReadPlan(missing));
            foreach (var snapshot in loaded.Values.OfType<ProfileSnapshot>())
            {
                var owner = owners.GetOrRegister(snapshot.Vector);
                if (!Matches(owner, snapshot.Vector, RevisionStore.Identity, RevisionStore.Progress,
                    "wardrobe", "guild_info", "personal", "personal_upvote", "popularity", "social_outfit", RevisionStore.GuildMembership))
                    throw new OwnerRevisionConflictException();
            }
            foreach (var (id, snapshot) in loaded)
            {
                if (snapshot is null) _profiles.Remove(id);
                else _profiles[id] = snapshot;
            }
        }
        return ids.ToDictionary(id => id, id => Profile(id));
    }

    internal Task<Dictionary<long, GuildStamp>> GuildStampsAsync(IEnumerable<long> roles)
    {
        AssertLoop();
        return accounts.ReadAsync<Dictionary<long, GuildStamp>>(new GuildStampReadPlan(roles));
    }

    internal Task<SocialRelationRows> TombstoneRowsAsync(long role)
    {
        AssertLoop();
        return accounts.ReadAsync<SocialRelationRows>(new TombstoneSocialReadPlan(role));
    }

    /// <summary>Publish exact post-write rows returned by the writer transaction with committed owner revisions.</summary>
    internal void ApplyRelation(IReadOnlyDictionary<long, SocialSnapshot?> participants, byte[] receipt)
    {
        AssertLoop();
        var rows = JsonSerializer.Deserialize<Dictionary<long, SocialRelationRows>>(receipt)
            ?? throw new InvalidOperationException("Friend receipt has no relation rows.");
        var updated = new Dictionary<long, SocialSnapshot>();
        foreach (var (id, value) in rows)
        {
            var before = participants[id] ?? throw new InvalidOperationException("Friend receipt lost a participant.");
            updated[id] = before with
            {
                Vector = before.Vector.WithRevision(RevisionStore.FriendRelation,
                    owners.Get(id).Revisions[RevisionStore.FriendRelation]),
                Friends = value.Friends,
                Applicants = value.Applicants,
                Blacklist = value.Blacklist,
                BlacklistedBy = value.BlacklistedBy,
                Intimacy = value.Intimacy,
            };
        }
        foreach (var (id, value) in updated) _social[id] = value;
    }

    private bool Current(RoleRevisionVector vector, params string[] parts) =>
        owners.Cached(vector.RoleId) is { } owner && Matches(owner, vector, parts);

    private static bool Matches(RoleOwner owner, RoleRevisionVector vector, params string[] parts) =>
        owner.State is not (RoleOwnerState.Deleted or RoleOwnerState.Deleting or RoleOwnerState.RecoveryRequired) &&
        owner.LifecycleEpoch == vector.Epoch && !vector.Deleted &&
        parts.All(part => owner.Revisions.TryGetValue(part, out long revision) && revision == vector.Revision(part));

    private void AssertLoop()
    {
        if (!loop.IsOnLoop) throw new InvalidOperationException("Social cache belongs to the game loop.");
    }
}
