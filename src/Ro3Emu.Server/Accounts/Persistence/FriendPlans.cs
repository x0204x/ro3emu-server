using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

internal enum FriendReadKind { Friends, Applicants, Blacklist, BlacklistedBy, AreFriends, HasApplied, Blocks, Metadata, Group, GroupsFor, Intimacy, RecentTeammates }

internal sealed class FriendReadPlan(FriendReadKind kind, long a, long b = 0, int max = 100, long cutoff = 0) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "friend/read";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx) => kind switch
    {
        FriendReadKind.Friends => Ids(db, tx, "SELECT other FROM friend WHERE owner=$a ORDER BY since", a, b),
        FriendReadKind.Applicants => Ids(db, tx, "SELECT applicant FROM friend_apply WHERE target=$a ORDER BY time", a, b),
        FriendReadKind.Blacklist => Ids(db, tx, "SELECT other FROM blacklist WHERE owner=$a", a, b),
        FriendReadKind.BlacklistedBy => Ids(db, tx, "SELECT owner FROM blacklist WHERE other=$a", a, b),
        FriendReadKind.AreFriends => Ids(db, tx, "SELECT other FROM friend WHERE owner=$a AND other=$b", a, b).Count != 0,
        FriendReadKind.HasApplied => Ids(db, tx, "SELECT applicant FROM friend_apply WHERE target=$a AND applicant=$b", a, b).Count != 0,
        FriendReadKind.Blocks => Ids(db, tx, "SELECT other FROM blacklist WHERE owner=$a AND other=$b", a, b).Count != 0,
        FriendReadKind.Metadata => Metadata(db, tx, a),
        FriendReadKind.Group => Group(db, tx, a),
        FriendReadKind.GroupsFor => GroupsFor(db, tx, a),
        FriendReadKind.Intimacy => Intimacy(db, tx, a, b),
        FriendReadKind.RecentTeammates => Recent(db, tx, a, max, cutoff),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static List<long> Ids(SqliteConnection db, SqliteTransaction tx, string sql, long a, long b)
    {
        using var cmd = DbSql.Command(db, tx, sql);
        cmd.Parameters.AddWithValue("$a", a); cmd.Parameters.AddWithValue("$b", b);
        var result = new List<long>();
        using var rows = cmd.ExecuteReader();
        while (rows.Read()) result.Add(rows.GetInt64(0));
        return result;
    }

    private static (byte[]? Data, IReadOnlyList<FriendStore.ChatGroup> Groups) Metadata(SqliteConnection db, SqliteTransaction tx, long role)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT data FROM friend_metadata WHERE role=$role");
        cmd.Parameters.AddWithValue("$role", role);
        byte[]? data = cmd.ExecuteScalar() is byte[] bytes ? (byte[])bytes.Clone() : null;
        return (data, GroupsFor(db, tx, role));
    }

    internal static FriendStore.ChatGroup? Group(SqliteConnection db, SqliteTransaction tx, long id)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT owner,name,created FROM friend_chat_group WHERE id=$id");
        cmd.Parameters.AddWithValue("$id", id);
        using var row = cmd.ExecuteReader();
        if (!row.Read()) return null;
        long owner = row.GetInt64(0), created = row.GetInt64(2);
        string name = row.GetString(1);
        row.Close();
        using var members = DbSql.Command(db, tx, "SELECT role,dnd FROM friend_chat_member WHERE group_id=$id ORDER BY role");
        members.Parameters.AddWithValue("$id", id);
        var list = new List<FriendStore.ChatMember>();
        using var rows = members.ExecuteReader();
        while (rows.Read()) list.Add(new(rows.GetInt64(0), rows.GetInt64(1) != 0));
        return new FriendStore.ChatGroup(id.ToString(), owner, name, created, [.. list]);
    }

    private static IReadOnlyList<FriendStore.ChatGroup> GroupsFor(SqliteConnection db, SqliteTransaction tx, long role)
    {
        var ids = Ids(db, tx, "SELECT group_id FROM friend_chat_member WHERE role=$a ORDER BY group_id", role, 0);
        return [.. ids.Select(id => Group(db, tx, id)).Where(x => x != null).Cast<FriendStore.ChatGroup>()];
    }

    private static int Intimacy(SqliteConnection db, SqliteTransaction tx, long owner, long other)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT intimacy FROM friend WHERE owner=$a AND other=$b");
        cmd.Parameters.AddWithValue("$a", owner); cmd.Parameters.AddWithValue("$b", other);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static IReadOnlyList<long> Recent(SqliteConnection db, SqliteTransaction tx, long role, int max, long cutoff)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT other FROM recent_team_peer WHERE owner=$role AND last_seen >= $cutoff ORDER BY last_seen DESC LIMIT $max");
        cmd.Parameters.AddWithValue("$role", role); cmd.Parameters.AddWithValue("$cutoff", cutoff); cmd.Parameters.AddWithValue("$max", max);
        var result = new List<long>();
        using var rows = cmd.ExecuteReader();
        while (rows.Read()) result.Add(rows.GetInt64(0));
        return result;
    }
}

internal enum FriendWriteKind { Apply, RemoveApplication, Link, Unlink, Block, Unblock, SaveMetadata, CreateGroup, RenameGroup, DismissGroup, ChangeMembers, SetDnd, SetIntimacy, RecordTeammates }

internal sealed class FriendWritePlan : DbPlan
{
    private readonly FriendWriteKind _kind;
    private readonly long _a, _b, _time;
    private readonly int _value;
    private readonly string? _name;
    private readonly long[] _members;
    private readonly long[] _participants;
    private readonly byte[]? _blob;
    private long[]? _roles;

    internal FriendWritePlan(FriendWriteKind kind, long a, long b = 0, long time = 0, int value = 0,
        string? name = null, IEnumerable<long>? members = null, byte[]? blob = null,
        IEnumerable<long>? participants = null)
    {
        _kind = kind; _a = a; _b = b; _time = time; _value = value; _name = name;
        _members = members?.ToArray() ?? [];
        _participants = participants?.ToArray() ?? [];
        _blob = blob is null ? null : (byte[])blob.Clone();
    }
    internal override int Bytes => 192 + (_name?.Length ?? 0) * 2 + (_members.Length + _participants.Length) * sizeof(long) + (_blob?.Length ?? 0);
    internal override string Label => "friend/" + _kind;
    internal override void ResolveRoleWrites(AccountStore accounts)
    {
        if (_roles is not null) return;
        _roles = _kind switch
        {
            FriendWriteKind.SaveMetadata => [_a],
            FriendWriteKind.CreateGroup or FriendWriteKind.RecordTeammates =>
                [.. new[] { _a }.Concat(_members).Distinct()],
            FriendWriteKind.RenameGroup or FriendWriteKind.DismissGroup or
                FriendWriteKind.ChangeMembers or FriendWriteKind.SetDnd =>
                _participants.Length != 0 ? [.. _participants.Distinct()] :
                    [.. accounts.ReadSync<long[]>(new FriendGroupRolesReadPlan(_a))
                        .Concat(_kind == FriendWriteKind.ChangeMembers ? _members :
                            _kind == FriendWriteKind.SetDnd ? [_b] : []).Distinct()],
            _ => [.. new[] { _a, _b }.Where(role => role > 0).Distinct()]
        };
    }
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [.. (_roles ??
        throw new InvalidOperationException("Friend role participants were not resolved before submission."))
        .Select(role => RevisionStore.Role(role,
            _kind == FriendWriteKind.SaveMetadata ? RevisionStore.FriendMetadata : RevisionStore.FriendRelation))];
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => _kind is
        FriendWriteKind.RenameGroup or FriendWriteKind.DismissGroup or FriendWriteKind.ChangeMembers or FriendWriteKind.SetDnd
        ? [RevisionStore.Entity("friend_group", _a)] : [];

    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        switch (_kind)
        {
            case FriendWriteKind.Apply:
                Exec(db, tx, "INSERT OR REPLACE INTO friend_apply(target,applicant,time) VALUES($a,$b,$time)", _a, _b, _time); break;
            case FriendWriteKind.RemoveApplication:
                RemoveApplication(db, tx, _a, _b); break;
            case FriendWriteKind.Link:
                Exec(db, tx, "INSERT OR IGNORE INTO friend(owner,other,since) VALUES($a,$b,$time)", _a, _b, _time);
                Exec(db, tx, "INSERT OR IGNORE INTO friend(owner,other,since) VALUES($a,$b,$time)", _b, _a, _time);
                RemoveApplication(db, tx, _a, _b); RemoveApplication(db, tx, _b, _a); break;
            case FriendWriteKind.Unlink:
                Unlink(db, tx, _a, _b); break;
            case FriendWriteKind.Block:
                Exec(db, tx, "INSERT OR IGNORE INTO blacklist(owner,other) VALUES($a,$b)", _a, _b);
                Unlink(db, tx, _a, _b); RemoveApplication(db, tx, _a, _b); break;
            case FriendWriteKind.Unblock:
                Exec(db, tx, "DELETE FROM blacklist WHERE owner=$a AND other=$b", _a, _b); break;
            case FriendWriteKind.SaveMetadata:
                using (var cmd = DbSql.Command(db, tx, "INSERT INTO friend_metadata(role,data) VALUES($a,$data) ON CONFLICT(role) DO UPDATE SET data=excluded.data"))
                { cmd.Parameters.AddWithValue("$a", _a); cmd.Parameters.AddWithValue("$data", _blob!); cmd.ExecuteNonQuery(); }
                break;
            case FriendWriteKind.CreateGroup:
                using (var cmd = DbSql.Command(db, tx, "INSERT INTO friend_chat_group(owner,name,created) VALUES($a,$name,$time)"))
                { cmd.Parameters.AddWithValue("$a", _a); cmd.Parameters.AddWithValue("$name", _name!); cmd.Parameters.AddWithValue("$time", _time); cmd.ExecuteNonQuery(); }
                long id;
                using (var identity = DbSql.Command(db, tx, "SELECT last_insert_rowid()")) id = (long)identity.ExecuteScalar()!;
                using (var revision = DbSql.Command(db, tx,
                    "INSERT INTO owner_revision(kind,id,part,revision) VALUES('friend_group',$id,'state',1)"))
                { revision.Parameters.AddWithValue("$id", id); revision.ExecuteNonQuery(); }
                foreach (long role in _members) MemberChange(db, tx, id, role, true);
                return FriendReadPlan.Group(db, tx, id)!;
            case FriendWriteKind.RenameGroup:
                using (var cmd = DbSql.Command(db, tx, "UPDATE friend_chat_group SET name=$name WHERE id=$a"))
                { cmd.Parameters.AddWithValue("$name", _name!); cmd.Parameters.AddWithValue("$a", _a); cmd.ExecuteNonQuery(); }
                break;
            case FriendWriteKind.DismissGroup:
                Exec(db, tx, "DELETE FROM friend_chat_member WHERE group_id=$a; DELETE FROM friend_chat_group WHERE id=$a", _a, 0); break;
            case FriendWriteKind.ChangeMembers:
                foreach (long role in _members) MemberChange(db, tx, _a, role, _value != 0);
                break;
            case FriendWriteKind.SetDnd:
                using (var cmd = DbSql.Command(db, tx, "UPDATE friend_chat_member SET dnd=$value WHERE group_id=$a AND role=$b"))
                { cmd.Parameters.AddWithValue("$value", _value); cmd.Parameters.AddWithValue("$a", _a); cmd.Parameters.AddWithValue("$b", _b); cmd.ExecuteNonQuery(); }
                break;
            case FriendWriteKind.SetIntimacy:
                using (var cmd = DbSql.Command(db, tx, "UPDATE friend SET intimacy=$value WHERE owner=$a AND other=$b"))
                { cmd.Parameters.AddWithValue("$value", _value); cmd.Parameters.AddWithValue("$a", _a); cmd.Parameters.AddWithValue("$b", _b); cmd.ExecuteNonQuery(); }
                break;
            case FriendWriteKind.RecordTeammates:
                foreach (long peer in _members)
                {
                    Exec(db, tx, "INSERT INTO recent_team_peer(owner,other,last_seen) VALUES($a,$b,$time) ON CONFLICT(owner,other) DO UPDATE SET last_seen=excluded.last_seen", _a, peer, _time);
                    Exec(db, tx, "INSERT INTO recent_team_peer(owner,other,last_seen) VALUES($a,$b,$time) ON CONFLICT(owner,other) DO UPDATE SET last_seen=excluded.last_seen", peer, _a, _time);
                }
                break;
            default: throw new ArgumentOutOfRangeException();
        }
        return true;
    }

    private static void Exec(SqliteConnection db, SqliteTransaction tx, string sql, long a, long b, long time = 0)
    {
        using var cmd = DbSql.Command(db, tx, sql);
        cmd.Parameters.AddWithValue("$a", a); cmd.Parameters.AddWithValue("$b", b); cmd.Parameters.AddWithValue("$time", time);
        cmd.ExecuteNonQuery();
    }
    private static void RemoveApplication(SqliteConnection db, SqliteTransaction tx, long target, long applicant)
        => Exec(db, tx, "DELETE FROM friend_apply WHERE target=$a AND applicant=$b", target, applicant);
    private static void Unlink(SqliteConnection db, SqliteTransaction tx, long a, long b)
    {
        Exec(db, tx, "DELETE FROM friend WHERE owner=$a AND other=$b", a, b);
        Exec(db, tx, "DELETE FROM friend WHERE owner=$a AND other=$b", b, a);
    }
    private static void MemberChange(SqliteConnection db, SqliteTransaction tx, long id, long role, bool add)
        => Exec(db, tx, add ? "INSERT OR IGNORE INTO friend_chat_member(group_id,role) VALUES($a,$b)"
            : "DELETE FROM friend_chat_member WHERE group_id=$a AND role=$b", id, role);
}

internal sealed class FriendGroupRolesReadPlan(long group) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "friend/read-group-roles";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT owner FROM friend_chat_group WHERE id=$group");
        cmd.Parameters.AddWithValue("$group", group);
        if (cmd.ExecuteScalar() is not long owner) return Array.Empty<long>();
        using var members = DbSql.Command(db, tx, "SELECT role FROM friend_chat_member WHERE group_id=$group");
        members.Parameters.AddWithValue("$group", group);
        using var rows = members.ExecuteReader();
        var result = new List<long> { owner };
        while (rows.Read()) result.Add(rows.GetInt64(0));
        return result.Distinct().ToArray();
    }
}
