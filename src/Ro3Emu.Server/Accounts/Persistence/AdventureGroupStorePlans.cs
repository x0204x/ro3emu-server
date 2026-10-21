using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>Adventure Group aggregate and its exclusive membership index (AdventureGroupStore).</summary>
internal sealed class AdventureGroupSchemaPlan : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "advguild/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS adventure_group (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                state BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS adventure_group_member (
                role_id INTEGER PRIMARY KEY,
                group_id INTEGER NOT NULL REFERENCES adventure_group(id)
            );
            CREATE INDEX IF NOT EXISTS adventure_group_member_group ON adventure_group_member(group_id);
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'adventure_group',id,'state',0 FROM adventure_group;
            """);
        return 1;
    }
}

internal enum AdventureGroupReadKind { Group, ForRole, List, NameExists }

internal sealed class AdventureGroupReadPlan(AdventureGroupReadKind kind, long id = 0, string name = "") : DbReadPlan
{
    internal override int Bytes => 128 + name.Length * 2;
    internal override string Label => "advguild/read-" + kind;
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        switch (kind)
        {
            case AdventureGroupReadKind.Group:
                using (var cmd = DbSql.Command(db, tx, "SELECT state FROM adventure_group WHERE id=$id"))
                {
                    cmd.Parameters.AddWithValue("$id", id);
                    return cmd.ExecuteScalar() is byte[] row ? WithRevision(db, tx, Parse(row)) : null;
                }
            case AdventureGroupReadKind.ForRole:
                using (var cmd = DbSql.Command(db, tx,
                    "SELECT g.state FROM adventure_group_member m JOIN adventure_group g ON g.id=m.group_id WHERE m.role_id=$role"))
                {
                    cmd.Parameters.AddWithValue("$role", id);
                    return cmd.ExecuteScalar() is byte[] row ? WithRevision(db, tx, Parse(row)) : null;
                }
            case AdventureGroupReadKind.List:
                using (var cmd = DbSql.Command(db, tx, "SELECT state FROM adventure_group ORDER BY id"))
                using (var reader = cmd.ExecuteReader())
                {
                    var rows = new List<AdventureGroupStore.State>();
                    while (reader.Read()) rows.Add(Parse((byte[])reader[0]));
                    reader.Close();
                    foreach (var group in rows) WithRevision(db, tx, group);
                    return rows;
                }
            case AdventureGroupReadKind.NameExists:
                using (var cmd = DbSql.Command(db, tx, "SELECT 1 FROM adventure_group WHERE name=$name COLLATE NOCASE AND id<>$id"))
                {
                    cmd.Parameters.AddWithValue("$name", name);
                    cmd.Parameters.AddWithValue("$id", id);
                    return cmd.ExecuteScalar() != null;
                }
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static AdventureGroupStore.State Parse(byte[] row) =>
        JsonSerializer.Deserialize<AdventureGroupStore.State>(row) ?? throw new InvalidDataException("Null adventure group state.");

    private static AdventureGroupStore.State WithRevision(SqliteConnection db, SqliteTransaction tx, AdventureGroupStore.State state)
    {
        state.CapturedRevision = (OwnerRevisionValue)new OwnerRevisionReadPlan(RevisionStore.Entity("adventure_group", state.Id))
            .Execute(db, tx);
        return state;
    }
}

/// <summary>
/// The whole aggregate and its membership rows in one write. A role in two groups violates adventure_group_member's
/// primary key and a duplicate name the unique index (SQLite 19): the caller reports the conflict, nothing is written.
/// </summary>
internal sealed class AdventureGroupSavePlan : DbPlan
{
    private readonly long _id;
    private readonly string _name;
    private readonly byte[] _state;
    private readonly long[] _members;

    internal AdventureGroupSavePlan(AdventureGroupStore.State state)
    {
        _id = state.Id;
        _name = state.Name;
        _state = JsonSerializer.SerializeToUtf8Bytes(state);
        _members = state.Members.Keys.ToArray();
    }

    internal override int Bytes => 128 + _state.Length + _members.Length * sizeof(long) + _name.Length * sizeof(char);
    internal override string Label => "advguild/save";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => _id == 0 ? [] : [RevisionStore.Entity("adventure_group", _id)];

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        DbWriter.AfterJob(AdventureGroupStore.Invalidate);
        long id = _id;
        byte[] state = _state;
        if (id == 0)
        {
            using var insert = DbSql.Command(db, tx, "INSERT INTO adventure_group(name,state) VALUES($name,$state) RETURNING id");
            insert.Parameters.AddWithValue("$name", _name);
            insert.Parameters.AddWithValue("$state", Array.Empty<byte>());
            id = (long)insert.ExecuteScalar()!;
            var generated = JsonSerializer.Deserialize<AdventureGroupStore.State>(_state)!;
            generated.Id = id;
            state = JsonSerializer.SerializeToUtf8Bytes(generated);
            using var revision = DbSql.Command(db, tx,
                "INSERT INTO owner_revision(kind,id,part,revision) VALUES('adventure_group',$id,'state',1)");
            revision.Parameters.AddWithValue("$id", id);
            revision.ExecuteNonQuery();
        }
        using (var update = DbSql.Command(db, tx, "UPDATE adventure_group SET name=$name,state=$state WHERE id=$id"))
        {
            update.Parameters.AddWithValue("$name", _name);
            update.Parameters.AddWithValue("$state", state);
            update.Parameters.AddWithValue("$id", id);
            if (update.ExecuteNonQuery() != 1)
                throw new RowMissingException(AdventureGroupStore.Table, id,
                    "Adventure group disappeared during save.");
        }
        using (var clear = DbSql.Command(db, tx, "DELETE FROM adventure_group_member WHERE group_id=$id"))
        {
            clear.Parameters.AddWithValue("$id", id);
            clear.ExecuteNonQuery();
        }
        foreach (long role in _members)
        {
            using var member = DbSql.Command(db, tx, "INSERT INTO adventure_group_member(role_id,group_id) VALUES($role,$group)");
            member.Parameters.AddWithValue("$role", role);
            member.Parameters.AddWithValue("$group", id);
            member.ExecuteNonQuery();
        }
        return id;
    }
}

internal sealed class AdventureGroupDeletePlan(long group) : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "advguild/delete";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => [RevisionStore.Entity("adventure_group", group)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        DbWriter.AfterJob(AdventureGroupStore.Invalidate);
        using var cmd = DbSql.Command(db, tx,
            "DELETE FROM adventure_group_member WHERE group_id=$id; DELETE FROM adventure_group WHERE id=$id");
        cmd.Parameters.AddWithValue("$id", group);
        return cmd.ExecuteNonQuery();
    }
}
