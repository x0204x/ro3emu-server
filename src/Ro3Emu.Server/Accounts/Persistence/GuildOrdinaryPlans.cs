using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Romsg;

namespace Ro3Emu.Server.Accounts.Persistence;

internal sealed class GuildRecoveryExistsPlan(long guild) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "guild/recovery-exists";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT 1 FROM guild_recovery WHERE guild_id=$guild");
        cmd.Parameters.AddWithValue("$guild", guild);
        return cmd.ExecuteScalar() != null;
    }
}

/// <summary>Role projection enlisted with the authoritative guild membership index.</summary>
internal sealed class GuildRoleViewWritePlan(long role, DBRoleGuildInfo view, bool generated) : DbPlan
{
    private readonly byte[] value = view.ToByteArray();
    internal override int Bytes => 128 + value.Length;
    internal override string Label => "guild/role-view";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(role, "guild_info")];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        byte[] next = value;
        if (generated)
        {
            using var membership = DbSql.Command(db, tx, "SELECT guild_id FROM guild_member WHERE role_id=$role");
            membership.Parameters.AddWithValue("$role", role);
            var info = DBRoleGuildInfo.Parser.ParseFrom(value);
            info.GuildId = (long?)membership.ExecuteScalar() ?? throw new InvalidOperationException("Created guild lost membership.");
            next = info.ToByteArray();
        }
        using var cmd = DbSql.Command(db, tx, "UPDATE role SET guild_info=$state WHERE id=$role");
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$state", next);
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Guild member role disappeared.");
        return 1;
    }
}

/// <summary>E05d accepted terminal policy: keep treasury ownership durably recoverable on disband.
/// Pending caravan/challenge obligations remain in their source journals. No implicit asset burn.</summary>
internal sealed class GuildRecoverDeletePlan(long guild, long escrowMaterials) : DbPlan
{
    private readonly GuildDeletePlan deletion = new(guild);
    internal override int Bytes => 256;
    internal override string Label => "guild/recover-delete";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => deletion.EntityWrites;
    internal override void ResolveRoleWrites(AccountStore accounts) => deletion.ResolveRoleWrites(accounts);
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => deletion.RoleWrites;
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        DbWriter.AfterJob(WrittenCache.Guild);
        using var query = DbSql.Command(db, tx, "SELECT state FROM guild WHERE id=$id");
        query.Parameters.AddWithValue("$id", guild);
        if (query.ExecuteScalar() is not byte[] bytes) throw new InvalidOperationException("Guild disappeared before recovery capture.");
        var state = System.Text.Json.JsonSerializer.Deserialize<GuildStore.State>(bytes)!;
        state.Materials = checked(state.Materials + escrowMaterials);
        using var keep = DbSql.Command(db, tx, """
            INSERT INTO guild_recovery(guild_id,state,created_ms) VALUES($id,$state,$now)
            """);
        keep.Parameters.AddWithValue("$id", guild);
        keep.Parameters.AddWithValue("$state", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(state));
        keep.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (keep.ExecuteNonQuery() != 1) throw new InvalidOperationException("Guild disappeared before recovery capture.");
        return deletion.Execute(db, tx);
    }
}
