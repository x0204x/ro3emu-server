using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>Guild aggregate and exclusive membership index, enlisted with a Character payment.</summary>
internal sealed class GuildSavePlan : DbPlan
{
    private readonly long _id;
    private readonly string _name, _missingMessage;
    private readonly byte[] _state;
    private readonly long[] _members;
    private long[]? _changedMembers;

    internal GuildSavePlan(GuildStore.State state, string missingMessage)
    {
        _id = state.Id;
        _name = state.Name;
        _state = JsonSerializer.SerializeToUtf8Bytes(state);
        _members = state.Members.Keys.ToArray();
        _missingMessage = missingMessage;
    }

    internal override int Bytes => 128 + _state.Length + _members.Length * sizeof(long)
        + (_name.Length + _missingMessage.Length) * sizeof(char);
    internal override string Label => "guild/save";
    internal override void ResolveRoleWrites(AccountStore accounts)
    {
        if (_changedMembers is not null) return;
        long[] prior = _id == 0 ? [] : accounts.ReadSync<long[]>(new GuildMemberIdsReadPlan(_id));
        _changedMembers = _members.Except(prior).Concat(prior.Except(_members)).Distinct().ToArray();
    }
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => (_changedMembers ??
        throw new InvalidOperationException("Guild membership was not resolved before submission."))
        .Select(role => RevisionStore.Role(role, RevisionStore.GuildMembership)).ToArray();
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => _id == 0 ? [] : [RevisionStore.Entity("guild", _id)];

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        DbWriter.AfterJob(GuildStore.Invalidate);
        long id = _id;
        byte[] state = _state;
        if (id == 0)
        {
            using var insert = DbSql.Command(db, tx, "INSERT INTO guild(name,state) VALUES($name,$state) RETURNING id");
            insert.Parameters.AddWithValue("$name", _name);
            insert.Parameters.AddWithValue("$state", Array.Empty<byte>());
            id = (long)insert.ExecuteScalar()!;
            // This object is created privately on the writer; the captured caller object is never retained.
            var generated = JsonSerializer.Deserialize<GuildStore.State>(_state)!;
            generated.Id = id;
            state = JsonSerializer.SerializeToUtf8Bytes(generated);
            using var revision = DbSql.Command(db, tx,
                "INSERT INTO owner_revision(kind,id,part,revision) VALUES('guild',$id,'state',1)");
            revision.Parameters.AddWithValue("$id", id);
            revision.ExecuteNonQuery();
        }
        using (var update = DbSql.Command(db, tx, "UPDATE guild SET name=$name,state=$state WHERE id=$id"))
        {
            update.Parameters.AddWithValue("$name", _name);
            update.Parameters.AddWithValue("$state", state);
            update.Parameters.AddWithValue("$id", id);
            if (update.ExecuteNonQuery() != 1) throw new RowMissingException(GuildStore.Table, id, _missingMessage);
        }
        using (var clear = DbSql.Command(db, tx, "DELETE FROM guild_member WHERE guild_id=$id"))
        {
            clear.Parameters.AddWithValue("$id", id);
            clear.ExecuteNonQuery();
        }
        foreach (long role in _members)
        {
            using var member = DbSql.Command(db, tx, "INSERT INTO guild_member(role_id,guild_id) VALUES($role,$guild)");
            member.Parameters.AddWithValue("$role", role);
            member.Parameters.AddWithValue("$guild", id);
            member.ExecuteNonQuery();
        }
        return id;
    }
}

internal sealed class GuildMemberIdsReadPlan(long guild) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "guild/read-member-ids";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT role_id FROM guild_member WHERE guild_id=$guild");
        cmd.Parameters.AddWithValue("$guild", guild);
        using var rows = cmd.ExecuteReader();
        var result = new List<long>();
        while (rows.Read()) result.Add(rows.GetInt64(0));
        return result.ToArray();
    }
}

/// <summary>Escort pack payment rechecks membership and uses the latest treasury in the same transaction.</summary>
internal sealed class GuildMaterialContributionPlan(long role, long guild, long amount) : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "guild/escort-materials";
    internal override IReadOnlyList<long> RolePins => [role];
    internal override IReadOnlyList<OwnerRevisionKey> EntityDeltaWrites => [RevisionStore.Entity("guild", guild)];

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        DbWriter.AfterJob(GuildStore.Invalidate);
        using var query = DbSql.Command(db, tx,
            "SELECT g.state FROM guild g JOIN guild_member m ON m.guild_id=g.id WHERE g.id=$guild AND m.role_id=$role");
        query.Parameters.AddWithValue("$guild", guild);
        query.Parameters.AddWithValue("$role", role);
        var current = query.ExecuteScalar() is byte[] bytes ? JsonSerializer.Deserialize<GuildStore.State>(bytes) : null;
        if (current == null) throw new InvalidOperationException("Escort submitter left guild");
        current.Materials = checked(current.Materials + amount);
        return new GuildSavePlan(current, "Escort pack guild vanished").Execute(db, tx);
    }
}

/// <summary>Caravan funds and member contributions use the current guild aggregate in the award transaction.</summary>
internal sealed class GuildCaravanRewardPlan(long guild, long role, long funds, long contribution) : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "guild/caravan-reward";
    internal override IReadOnlyList<long> RolePins => role > 0 ? [role] : [];
    internal override IReadOnlyList<OwnerRevisionKey> EntityDeltaWrites => [RevisionStore.Entity("guild", guild)];

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        DbWriter.AfterJob(GuildStore.Invalidate);
        using var query = DbSql.Command(db, tx, "SELECT state FROM guild WHERE id=$id");
        query.Parameters.AddWithValue("$id", guild);
        if (query.ExecuteScalar() is not byte[] bytes)
        {
            using var recovery = DbSql.Command(db, tx, "SELECT state FROM guild_recovery WHERE guild_id=$id");
            recovery.Parameters.AddWithValue("$id", guild);
            if (recovery.ExecuteScalar() is not byte[] held)
                throw new InvalidOperationException("Missing guild recovery owner; caravan entitlement remains pending.");
            var saved = JsonSerializer.Deserialize<GuildStore.State>(held)!;
            saved.Money = checked(saved.Money + funds);
            using var credit = DbSql.Command(db, tx, "UPDATE guild_recovery SET state=$state WHERE guild_id=$id");
            credit.Parameters.AddWithValue("$state", JsonSerializer.SerializeToUtf8Bytes(saved));
            credit.Parameters.AddWithValue("$id", guild);
            credit.ExecuteNonQuery();
            return true;
        }
        var current = JsonSerializer.Deserialize<GuildStore.State>(bytes)!;
        current.Money = checked(current.Money + funds);
        if (current.Members.TryGetValue(role, out var member))
        {
            member.CurContribute = checked(member.CurContribute + contribution);
            member.HistoryContribute = checked(member.HistoryContribute + contribution);
            // 32291 Fund Rankings: the funds a member brought in (UITips 17023: earned by the Royal Caravan).
            member.ContributeMoney = checked(member.ContributeMoney + funds);
        }
        new GuildSavePlan(current, "Guild caravan guild update failed").Execute(db, tx);
        return true;
    }
}
