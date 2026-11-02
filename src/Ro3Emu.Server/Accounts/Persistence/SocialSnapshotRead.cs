using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>Detached social state from one reader transaction, including the role's durable revision vector.</summary>
internal sealed record SocialSnapshot(Role Role, RoleRevisionVector Vector, long[] Friends, long[] Applicants,
    long[] Blacklist, long[] BlacklistedBy, IReadOnlyDictionary<long, int> Intimacy);

internal sealed record SocialRelationRows(long[] Friends, long[] Applicants, long[] Blacklist,
    long[] BlacklistedBy, Dictionary<long, int> Intimacy);

internal sealed class SocialSnapshotReadPlan(IEnumerable<long> roles) : DbReadPlan
{
    private readonly long[] _roles = [.. roles.Distinct()];
    internal override int Bytes => 128 + _roles.Length * sizeof(long);
    internal override string Label => "social/snapshot";

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var result = new Dictionary<long, SocialSnapshot?>();
        foreach (long id in _roles)
        {
            var role = ((List<Role>)new RoleProjectionRead(RoleProjectionKind.ById, id: id).Execute(db, tx)!).FirstOrDefault();
            if (role is null) { result[id] = null; continue; }
            var vector = (RoleRevisionVector)new RoleRevisionReadPlan(id).Execute(db, tx);
            if (vector.Deleted) { result[id] = null; continue; }
            var rows = SocialFriendWritePlan.ReadRows(db, tx, id);
            result[id] = new(role, vector, rows.Friends, rows.Applicants,
                rows.Blacklist, rows.BlacklistedBy, rows.Intimacy);
        }
        return result;
    }
}

/// <summary>One friend mutation and its exact post-write relation rows in the same durable transaction.</summary>
internal sealed class SocialFriendWritePlan(DbPlan inner, IEnumerable<long> roles) : DbPlan
{
    private readonly long[] _roles = [.. roles.Distinct()];
    internal override int Bytes => inner.Bytes + 128 + _roles.Length * sizeof(long);
    internal override string Label => "social/friend-write";
    internal override void ResolveRoleWrites(AccountStore accounts) => inner.ResolveRoleWrites(accounts);
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => inner.RoleWrites;
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        inner.Execute(db, tx);
        var rows = _roles.ToDictionary(role => role, role => ReadRows(db, tx, role));
        return JsonSerializer.SerializeToUtf8Bytes(rows);
    }

    internal static SocialRelationRows ReadRows(SqliteConnection db, SqliteTransaction tx, long role)
    {
        long[] Read(FriendReadKind kind) => [.. ((List<long>)new FriendReadPlan(kind, role).Execute(db, tx)!)];
        var friends = Read(FriendReadKind.Friends);
        var intimacy = friends.ToDictionary(friend => friend,
            friend => (int)new FriendReadPlan(FriendReadKind.Intimacy, role, friend).Execute(db, tx)!);
        return new(friends, Read(FriendReadKind.Applicants), Read(FriendReadKind.Blacklist),
            Read(FriendReadKind.BlacklistedBy), intimacy);
    }
}

/// <summary>Compatibility read for a deleted or missing Uid; it never registers an active owner.</summary>
internal sealed class TombstoneSocialReadPlan(long role) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "social/tombstone-read";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx) =>
        SocialFriendWritePlan.ReadRows(db, tx, role);
}

internal readonly record struct GuildStamp(long Id, long Revision);

internal sealed class GuildStampReadPlan(IEnumerable<long> roles) : DbReadPlan
{
    private readonly long[] _roles = [.. roles.Distinct()];
    internal override int Bytes => 128 + _roles.Length * sizeof(long);
    internal override string Label => "social/guild-stamps";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var result = new Dictionary<long, GuildStamp>();
        foreach (long role in _roles)
        {
            using var cmd = DbSql.Command(db, tx, """
                SELECT m.guild_id, COALESCE(v.revision,0)
                FROM guild_member m LEFT JOIN owner_revision v
                    ON v.kind='guild' AND v.id=CAST(m.guild_id AS TEXT) AND v.part='state'
                WHERE m.role_id=$role
                """);
            cmd.Parameters.AddWithValue("$role", role);
            using var row = cmd.ExecuteReader();
            result[role] = row.Read() ? new(row.GetInt64(0), row.GetInt64(1)) : default;
        }
        return result;
    }
}

internal sealed record ProfileSnapshot(Role Role, RoleRevisionVector Vector, byte[]? Wardrobe,
    byte[]? PersonalGuild, GuildStamp GuildStamp, GuildStore.State? Guild, byte[]? Personal,
    byte[]? PersonalUpvotes, byte[]? Popularity, byte[]? SocialOutfit);

internal sealed class ProfileSnapshotReadPlan(IEnumerable<long> roles) : DbReadPlan
{
    private readonly long[] _roles = [.. roles.Distinct()];
    internal override int Bytes => 128 + _roles.Length * sizeof(long);
    internal override string Label => "social/profile-snapshot";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var stamps = (Dictionary<long, GuildStamp>)new GuildStampReadPlan(_roles).Execute(db, tx);
        var result = new Dictionary<long, ProfileSnapshot?>();
        foreach (long id in _roles)
        {
            var role = ((List<Role>)new RoleProjectionRead(RoleProjectionKind.ById, id: id).Execute(db, tx)!).FirstOrDefault();
            if (role is null) { result[id] = null; continue; }
            var vector = (RoleRevisionVector)new RoleRevisionReadPlan(id).Execute(db, tx);
            if (vector.Deleted) { result[id] = null; continue; }
            var stamp = stamps[id];
            var guild = stamp.Id == 0 ? null :
                (GuildStore.State?)new GuildReadPlan(GuildReadKind.Guild, stamp.Id).Execute(db, tx);
            result[id] = new(role, vector,
                (byte[]?)new RoleBlobReadPlan(id, "wardrobe").Execute(db, tx),
                (byte[]?)new RoleBlobReadPlan(id, "guild_info").Execute(db, tx), stamp, guild,
                (byte[]?)new RoleBlobReadPlan(id, "personal").Execute(db, tx),
                (byte[]?)new RoleBlobReadPlan(id, "personal_upvote").Execute(db, tx),
                (byte[]?)new RoleBlobReadPlan(id, "popularity").Execute(db, tx),
                (byte[]?)new RoleBlobReadPlan(id, "social_outfit").Execute(db, tx));
        }
        return result;
    }
}
