using System.Text.Json;
using Microsoft.Data.Sqlite;
using Romsg;

namespace Ro3Emu.Server.Accounts.Persistence;

internal enum AuctionReadKind { All, Open, OfType, Settled, Run, Ids, Item, Prebuys, PendingRuns, RunSnapshot, Bonuses, SourceExists }

internal sealed record AuctionRunSnapshot(AuctionStore.Run? Run, byte[]? Raw);

internal sealed class AuctionReadPlan(AuctionReadKind kind, long id = 0, bool pendingOnly = true,
    string? sourceKey = null, IReadOnlyCollection<long>? ids = null) : DbReadPlan
{
    internal override int Bytes => 128 + (sourceKey?.Length ?? 0) * 2 + (ids?.Count ?? 0) * 8;
    internal override string Label => "auction/read-" + kind;
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        switch (kind)
        {
            case AuctionReadKind.All:
                return Lots(db, tx, " ORDER BY a.id", null);
            case AuctionReadKind.Open:
                return Lots(db, tx, " WHERE a.settled=0 ORDER BY a.id", null);
            case AuctionReadKind.OfType:
                return Lots(db, tx, " WHERE a.auction_type=$id ORDER BY a.id", id);
            case AuctionReadKind.Settled:
                return Lots(db, tx, " WHERE a.settled=1 AND a.auction_type=$id ORDER BY a.end_time DESC, a.id", id);
            case AuctionReadKind.Run:
                return Lots(db, tx, " WHERE a.run_id=$id ORDER BY a.id", id);
            case AuctionReadKind.Ids:
                return ids is not { Count: > 0 } ? []
                    : Lots(db, tx, $" WHERE a.id IN ({string.Join(',', ids.Distinct().Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)))}) ORDER BY a.id", null);
            case AuctionReadKind.Item:
                return Lots(db, tx, " WHERE a.id=$id", id).FirstOrDefault();
            case AuctionReadKind.Prebuys:
                using (var cmd = DbSql.Command(db, tx,
                    "SELECT id,role_id,auction_type,src_type,item_id,quantity,unit_price,currency,revision,processed,requested_id,paused FROM auction_prebuy"
                    + (pendingOnly ? " WHERE processed=0" : "") + " ORDER BY id"))
                using (var reader = cmd.ExecuteReader())
                {
                    var rows = new List<AuctionStore.Prebuy>();
                    while (reader.Read())
                    {
                        var row = new AuctionStore.Prebuy(reader.GetInt64(0), reader.GetInt64(1),
                            (AuctionType)reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5),
                            reader.GetInt32(6), reader.GetInt32(7), reader.GetInt64(8), reader.GetInt32(9) != 0,
                            reader.GetInt64(10), reader.GetInt32(11) != 0);
                        rows.Add(row with { Original = row });
                    }
                    reader.Close();
                    return rows.Select(row => row with
                    {
                        SourceRevision = ReadRevision(db, tx,
                        RevisionStore.Entity("auction_prebuy", row.Id))
                    }).ToList();
                }
            case AuctionReadKind.PendingRuns:
                using (var cmd = DbSql.Command(db, tx, "SELECT state FROM auction_run"))
                using (var reader = cmd.ExecuteReader())
                {
                    var rows = new List<AuctionStore.Run>();
                    while (reader.Read())
                    {
                        var run = JsonSerializer.Deserialize<AuctionStore.Run>((byte[])reader[0])!;
                        if (!run.Finalized) rows.Add(run);
                    }
                    reader.Close();
                    foreach (var run in rows) run.SourceRevision = ReadRevision(db, tx,
                        RevisionStore.Entity("auction_run", run.Id));
                    return rows;
                }
            case AuctionReadKind.RunSnapshot:
                using (var cmd = DbSql.Command(db, tx, "SELECT state FROM auction_run WHERE id=$id"))
                {
                    cmd.Parameters.AddWithValue("$id", id);
                    var raw = cmd.ExecuteScalar() as byte[];
                    var run = raw is null ? null : JsonSerializer.Deserialize<AuctionStore.Run>(raw);
                    if (run is not null) run.SourceRevision = ReadRevision(db, tx,
                        RevisionStore.Entity("auction_run", run.Id));
                    return new AuctionRunSnapshot(run, raw is null ? null : (byte[])raw.Clone());
                }
            case AuctionReadKind.Bonuses:
                using (var cmd = DbSql.Command(db, tx,
                    "SELECT group_key,amount,claimed,guild_id FROM auction_bonus WHERE role_id=$role"))
                {
                    cmd.Parameters.AddWithValue("$role", id);
                    using var reader = cmd.ExecuteReader();
                    var rows = new List<AuctionStore.Bonus>();
                    while (reader.Read()) rows.Add(new AuctionStore.Bonus(id, reader.GetString(0), reader.GetInt32(1),
                        reader.GetInt32(2) != 0, reader.GetInt64(3)));
                    return rows;
                }
            case AuctionReadKind.SourceExists:
                using (var cmd = DbSql.Command(db, tx, "SELECT 1 FROM auction_source WHERE source_key=$key"))
                {
                    cmd.Parameters.AddWithValue("$key", sourceKey ?? throw new ArgumentNullException(nameof(sourceKey)));
                    return cmd.ExecuteScalar() is not null;
                }
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static AuctionStore.Item DecodeItem(byte[] state)
    {
        var item = JsonSerializer.Deserialize<AuctionStore.Item>(state)!;
        item.OriginalBytes = (byte[])state.Clone();
        return item;
    }
    /// <summary>Lots with their entity revision from the same query (one read, not one revision query per row).</summary>
    private static List<AuctionStore.Item> Lots(SqliteConnection db, SqliteTransaction tx, string where, long? id)
    {
        using var cmd = DbSql.Command(db, tx, AuctionItemColumns.Select + where);
        if (id is { } value) cmd.Parameters.AddWithValue("$id", value);
        using var reader = cmd.ExecuteReader();
        var rows = new List<AuctionStore.Item>();
        while (reader.Read())
        {
            var item = DecodeItem((byte[])reader[0]);
            var key = RevisionStore.Entity("auction_item", item.SourceKey);
            item.SourceRevision = reader.IsDBNull(1) ? new OwnerRevisionValue(key, 0, false)
                : new OwnerRevisionValue(key, reader.GetInt64(1), true);
            rows.Add(item);
        }
        return rows;
    }
    private static OwnerRevisionValue ReadRevision(SqliteConnection db, SqliteTransaction tx, OwnerRevisionKey key) =>
        (OwnerRevisionValue)new OwnerRevisionReadPlan(key).Execute(db, tx);
}

internal sealed class AuctionSourceDuplicateException : InvalidOperationException;

internal sealed class AuctionSourceClaimPlan(string key) : DbPlan
{
    internal string SourceKey => key;
    internal override int Bytes => 128 + key.Length * 2;
    internal override string Label => "auction/source-claim";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => [RevisionStore.Entity("auction_source", key)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "INSERT OR IGNORE INTO auction_source(source_key) VALUES ($key)");
        cmd.Parameters.AddWithValue("$key", key);
        if (cmd.ExecuteNonQuery() != 1) throw new AuctionSourceDuplicateException();
        return true;
    }
}

internal sealed class AuctionRunSavePlan : DbPlan
{
    private readonly long _id;
    private readonly byte[]? _before;
    private readonly byte[] _after;
    private readonly long[] _addedRoles;
    internal AuctionRunSavePlan(AuctionStore.Run run, byte[]? before)
    {
        _id = run.Id; _before = before is null ? null : (byte[])before.Clone();
        _after = JsonSerializer.SerializeToUtf8Bytes(run);
        var old = before is null ? null : JsonSerializer.Deserialize<AuctionStore.Run>(before);
        _addedRoles = run.Eligible.Except(old?.Eligible ?? []).Where(role => role > 0).Distinct().ToArray();
    }
    internal override int Bytes => 128 + (_before?.Length ?? 0) + _after.Length;
    internal override string Label => "auction/run-save";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => [RevisionStore.Entity("auction_run", _id)];
    internal override IReadOnlyList<long> RolePins => _addedRoles;
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, _before is null
            ? "INSERT INTO auction_run(id,state) VALUES ($id,$state)"
            : "UPDATE auction_run SET state=$state WHERE id=$id AND state IS $before");
        cmd.Parameters.AddWithValue("$id", _id);
        cmd.Parameters.AddWithValue("$state", _after);
        if (_before is not null) cmd.Parameters.AddWithValue("$before", _before);
        if (cmd.ExecuteNonQuery() != 1) throw new RolePlanConflictException();
        return 1;
    }
}

internal sealed class AuctionBonusClaimPlan(long role, string key) : DbPlan
{
    internal override int Bytes => 128 + key.Length * 2;
    internal override string Label => "auction/bonus-claim";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(role, RevisionStore.AuctionBonus)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "UPDATE auction_bonus SET claimed=1 WHERE role_id=$role AND group_key=$key AND claimed=0");
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$key", key);
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Bonus raced claim.");
        return 1;
    }
}

internal sealed record AuctionItemWriteReceipt(long Id, byte[] State);

internal sealed class AuctionItemWritePlan : DbPlan
{
    private readonly long _id;
    private readonly string _source;
    private readonly byte[]? _before;
    private readonly byte[] _state;
    private readonly long[] _actors;
    internal string SourceKey => _source;
    internal bool CreatesNew => _id == 0;
    internal AuctionItemWritePlan(AuctionStore.Item item)
    {
        _id = item.Id;
        _source = item.SourceKey;
        _before = item.OriginalBytes is null ? null : (byte[])item.OriginalBytes.Clone();
        if (_id != 0 && _before is null)
            throw new InvalidOperationException("Auction item update needs its captured source row.");
        _state = JsonSerializer.SerializeToUtf8Bytes(item);
        var old = _before is null ? null : JsonSerializer.Deserialize<AuctionStore.Item>(_before);
        _actors = new[] { item.Owner != old?.Owner ? item.Owner : 0,
                item.Winner != old?.Winner ? item.Winner : 0 }
            .Concat(item.Eligible.Except(old?.Eligible ?? []))
            .Concat(item.GiveUp.Except(old?.GiveUp ?? []))
            .Concat(item.Joined.Except(old?.Joined ?? []))
            .Where(role => role > 0).Distinct().ToArray();
    }
    internal override int Bytes => 128 + _source.Length * 2 + (_before?.Length ?? 0) + _state.Length;
    internal override string Label => "auction/item";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => [RevisionStore.Entity("auction_item", _source)];
    internal override IReadOnlyList<long> RolePins => _actors;
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        long id = _id;
        if (id == 0)
        {
            using var insert = DbSql.Command(db, tx, "INSERT INTO auction_item(source_key,state) VALUES ($source,$state) RETURNING id");
            insert.Parameters.AddWithValue("$source", _source);
            insert.Parameters.AddWithValue("$state", Array.Empty<byte>());
            id = (long)insert.ExecuteScalar()!;
        }
        byte[] state = _state;
        if (_id == 0)
        {
            var item = JsonSerializer.Deserialize<AuctionStore.Item>(_state)!;
            item.Id = id;
            state = JsonSerializer.SerializeToUtf8Bytes(item);
        }
        using var cmd = DbSql.Command(db, tx, _id == 0
            ? "UPDATE auction_item SET state=$state WHERE id=$id"
            : "UPDATE auction_item SET state=$state WHERE id=$id AND state IS $before");
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$id", id);
        if (_before is not null) cmd.Parameters.AddWithValue("$before", _before);
        if (cmd.ExecuteNonQuery() != 1) throw new RolePlanConflictException();
        AuctionItemColumns.Write(db, tx, id, JsonSerializer.Deserialize<AuctionStore.Item>(state)
            ?? throw new InvalidDataException("Auction lot state must contain an item."));
        return new AuctionItemWriteReceipt(id, state);
    }
}

internal sealed class AuctionPrebuyWritePlan : DbPlan
{
    private readonly AuctionStore.Prebuy _order;
    private readonly AuctionStore.Prebuy? _before;
    internal AuctionPrebuyWritePlan(AuctionStore.Prebuy order)
    {
        _order = order with { Original = null };
        _before = order.Original;
        if (_order.Id != 0 && _before is null)
            throw new InvalidOperationException("Auction prebuy update needs its captured source row.");
    }
    internal override int Bytes => 128;
    internal override string Label => "auction/prebuy";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(_order.Role, RevisionStore.AuctionOrder)];
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => _order.Id == 0 ? [] : [RevisionStore.Entity("auction_prebuy", _order.Id)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var order = _order;
        using var cmd = DbSql.Command(db, tx, order.Id == 0
            ? "INSERT INTO auction_prebuy(role_id,auction_type,src_type,item_id,quantity,unit_price,currency,revision,processed,requested_id,paused) VALUES ($role,$type,$src,$entry,$qty,$price,$cur,$revision,$done,$requested,$paused)"
            : "UPDATE auction_prebuy SET quantity=$qty,unit_price=$price,revision=$revision,processed=$done,paused=$paused "
                + "WHERE id=$id AND role_id=$oldRole AND auction_type=$oldType AND src_type=$oldSrc "
                + "AND item_id=$oldEntry AND quantity=$oldQty AND unit_price=$oldPrice AND currency=$oldCur "
                + "AND revision=$oldRevision AND processed=$oldDone AND requested_id=$oldRequested AND paused=$oldPaused");
        cmd.Parameters.AddWithValue("$id", order.Id);
        cmd.Parameters.AddWithValue("$role", order.Role);
        cmd.Parameters.AddWithValue("$type", (int)order.Type);
        cmd.Parameters.AddWithValue("$src", order.SrcType);
        cmd.Parameters.AddWithValue("$entry", order.Entry);
        cmd.Parameters.AddWithValue("$qty", order.Quantity);
        cmd.Parameters.AddWithValue("$price", order.UnitPrice);
        cmd.Parameters.AddWithValue("$cur", order.Currency);
        cmd.Parameters.AddWithValue("$revision", order.Revision);
        cmd.Parameters.AddWithValue("$done", order.Processed ? 1 : 0);
        cmd.Parameters.AddWithValue("$requested", order.RequestedId);
        cmd.Parameters.AddWithValue("$paused", order.Paused ? 1 : 0);
        if (_before is { } old)
        {
            cmd.Parameters.AddWithValue("$oldRole", old.Role);
            cmd.Parameters.AddWithValue("$oldType", (int)old.Type);
            cmd.Parameters.AddWithValue("$oldSrc", old.SrcType);
            cmd.Parameters.AddWithValue("$oldEntry", old.Entry);
            cmd.Parameters.AddWithValue("$oldQty", old.Quantity);
            cmd.Parameters.AddWithValue("$oldPrice", old.UnitPrice);
            cmd.Parameters.AddWithValue("$oldCur", old.Currency);
            cmd.Parameters.AddWithValue("$oldRevision", old.Revision);
            cmd.Parameters.AddWithValue("$oldDone", old.Processed ? 1 : 0);
            cmd.Parameters.AddWithValue("$oldRequested", old.RequestedId);
            cmd.Parameters.AddWithValue("$oldPaused", old.Paused ? 1 : 0);
        }
        if (cmd.ExecuteNonQuery() != 1) throw new RolePlanConflictException();
        return 1;
    }
}

internal sealed class AuctionBonusWritePlan(long role, string key, int amount, long guild) : DbPlan
{
    internal override int Bytes => 128 + key.Length * 2;
    internal override string Label => "auction/bonus";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(role, RevisionStore.AuctionBonus)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            "INSERT OR IGNORE INTO auction_bonus(role_id,group_key,amount,guild_id,claimed) VALUES ($role,$key,$amount,$guild,1)");
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$amount", amount);
        cmd.Parameters.AddWithValue("$guild", guild);
        return cmd.ExecuteNonQuery();
    }
}

internal sealed class AuctionRunClearPlan : DbPlan
{
    private readonly long _runId, _roleId, _guildId;
    private readonly int _sourceType, _playBook;
    private readonly KeyValuePair<int, long>[] _tiers;
    internal AuctionRunClearPlan(long runId, long roleId, long guildId, int sourceType, int playBook,
        IReadOnlyDictionary<int, long> tiers)
    {
        _runId = runId; _roleId = roleId; _guildId = guildId; _sourceType = sourceType; _playBook = playBook;
        _tiers = tiers.ToArray();
    }
    internal override int Bytes => 128 + _tiers.Length * 16;
    internal override string Label => "auction/run-clear";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(_roleId, RevisionStore.AuctionOrder)];
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites => [RevisionStore.Entity("auction_run", _runId)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var read = DbSql.Command(db, tx, "SELECT state FROM auction_run WHERE id=$id");
        read.Parameters.AddWithValue("$id", _runId);
        var run = read.ExecuteScalar() is byte[] bytes ? JsonSerializer.Deserialize<AuctionStore.Run>(bytes)!
            : new AuctionStore.Run
            {
                Id = _runId, GuildId = _guildId, SrcType = _sourceType,
                PlayBook = _playBook, TierGroups = new(_tiers)
            };
        if (run.Finalized) return 0;
        if (run.GuildId != _guildId) run.GuildId = 0;
        if (!run.Eligible.Contains(_roleId)) run.Eligible.Add(_roleId);
        using var write = DbSql.Command(db, tx,
            "INSERT INTO auction_run(id,state) VALUES ($id,$state) ON CONFLICT(id) DO UPDATE SET state=excluded.state");
        write.Parameters.AddWithValue("$id", run.Id);
        write.Parameters.AddWithValue("$state", JsonSerializer.SerializeToUtf8Bytes(run));
        return write.ExecuteNonQuery();
    }
}

internal sealed class AuctionPersonalCasPlan : DbPlan
{
    private readonly long _role;
    private readonly byte[]? _before;
    private readonly byte[] _after;
    internal AuctionPersonalCasPlan(long role, byte[]? before, byte[] after)
    { _role = role; _before = before is null ? null : (byte[])before.Clone(); _after = (byte[])after.Clone(); }
    internal override int Bytes => 128 + (_before?.Length ?? 0) + _after.Length;
    internal override string Label => "auction/personal-cas";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(_role, "auction")];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "UPDATE role SET auction=$after WHERE id=$role AND auction IS $before");
        cmd.Parameters.AddWithValue("$role", _role);
        cmd.Parameters.AddWithValue("$before", (object?)_before ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$after", _after);
        if (cmd.ExecuteNonQuery() != 1) throw new RolePlanConflictException();
        return 1;
    }
}
