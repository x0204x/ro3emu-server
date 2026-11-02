using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

internal sealed class MarketSchemaPlan : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "market/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "CREATE TABLE IF NOT EXISTS market_record (kind TEXT NOT NULL, id TEXT NOT NULL, data TEXT NOT NULL, PRIMARY KEY(kind,id))");
        cmd.ExecuteNonQuery();
        SchemaSql.Execute(db, tx, """
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'market_record:' || kind,id,'state',0 FROM market_record
            """);
        SchemaSql.Execute(db, tx, """
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'market_kind',kind,'state',0 FROM market_record GROUP BY kind
            """);
        return 1;
    }
}

internal sealed record MarketRecordSnapshot(List<(string Id, string Data)> Rows, OwnerRevisionValue KindRevision);

/// <summary>Rows and their collection revision are read in one SQLite snapshot.</summary>
internal sealed class MarketRecordSnapshotReadPlan(string kind, string? key = null) : DbReadPlan
{
    internal override int Bytes => 128 + kind.Length * 2 + (key?.Length ?? 0) * 2;
    internal override string Label => "market/read-versioned-records";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var revision = (OwnerRevisionValue)new OwnerRevisionReadPlan(RevisionStore.Entity("market_kind", kind))
            .Execute(db, tx);
        return new MarketRecordSnapshot(MarketRecordReadPlan.Read(db, tx, kind, key), revision);
    }
}

internal sealed class MarketRecordReadPlan(string kind, string? key = null) : DbReadPlan
{
    internal override int Bytes => 128 + kind.Length * 2 + (key?.Length ?? 0) * 2;
    internal override string Label => "market/read-records";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx) => Read(db, tx, kind, key);
    internal static List<(string Id, string Data)> Read(SqliteConnection db, SqliteTransaction tx, string kind, string? key)
    {
        using var cmd = DbSql.Command(db, tx, key is null
            ? "SELECT id,data FROM market_record WHERE kind=$k ORDER BY id"
            : "SELECT id,data FROM market_record WHERE kind=$k AND id=$i");
        cmd.Parameters.AddWithValue("$k", kind);
        if (key is not null) cmd.Parameters.AddWithValue("$i", key);
        using var row = cmd.ExecuteReader();
        var result = new List<(string, string)>();
        while (row.Read()) result.Add((row.GetString(0), row.GetString(1)));
        return result;
    }
}

internal sealed class MarketRecordGuardPlan : DbPlan
{
    private readonly string _kind;
    private readonly string? _key;
    private readonly (string Id, string Data)[] _expected;
    internal OwnerRevisionValue? CapturedRevision { get; }
    internal MarketRecordGuardPlan(string kind, string? key, IEnumerable<(string Id, string Data)> expected)
    { _kind = kind; _key = key; _expected = [.. expected]; }
    internal MarketRecordGuardPlan(string kind, string? key, MarketRecordSnapshot snapshot)
    {
        _kind = kind; _key = key; _expected = [.. snapshot.Rows];
        CapturedRevision = snapshot.KindRevision;
    }
    internal override int Bytes => 128 + _expected.Sum(x => (x.Id.Length + x.Data.Length) * 2);
    internal override string Label => "market/guard-records";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        if (!MarketRecordReadPlan.Read(db, tx, _kind, _key).SequenceEqual(_expected))
            throw new InvalidOperationException("Market source changed during transaction capture.");
        return true;
    }
}

internal sealed class MarketRecordWritePlan(string kind, string key, string? data) : DbPlan
{
    internal override int Bytes => 128 + (kind.Length + key.Length + (data?.Length ?? 0)) * 2;
    internal override string Label => "market/write-record";
    internal override IReadOnlyList<OwnerRevisionKey> EntityWrites =>
        [RevisionStore.Entity("market_record:" + kind, key), RevisionStore.Entity("market_kind", kind)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, data is null
            ? "DELETE FROM market_record WHERE kind=$k AND id=$i"
            : "INSERT INTO market_record(kind,id,data) VALUES($k,$i,$d) ON CONFLICT(kind,id) DO UPDATE SET data=excluded.data");
        cmd.Parameters.AddWithValue("$k", kind); cmd.Parameters.AddWithValue("$i", key);
        if (data is not null) cmd.Parameters.AddWithValue("$d", data);
        return cmd.ExecuteNonQuery();
    }
}

internal sealed record MarketRoleSnapshot(Role Role, string Currency, List<InvItem> Items, RoleRevisionVector Vector);

/// <summary>Offline identity, currency and inventory come from one committed snapshot.</summary>
internal sealed class MarketRoleReadPlan(long role) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "market/read-participant";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var identity = DbSql.Command(db, tx, "SELECT * FROM role WHERE id=$id AND deleted_time=0");
        identity.Parameters.AddWithValue("$id", role);
        var found = RoleStore.Read(identity).SingleOrDefault() ?? throw new InvalidOperationException($"Missing market role {role}");
        using var balance = DbSql.Command(db, tx, "SELECT currency FROM role WHERE id=$id");
        balance.Parameters.AddWithValue("$id", role);
        var currency = (string)balance.ExecuteScalar()!;
        var items = ItemStore.ReadItems(db, tx, role);
        var vector = (RoleRevisionVector)new RoleRevisionReadPlan(role).Execute(db, tx);
        return new MarketRoleSnapshot(found, currency, items, vector);
    }
}

internal sealed class MarketRoleGuardPlan : DbPlan
{
    private readonly long _role;
    private readonly string _currency;
    private readonly ItemValue[] _items;
    internal RoleRevisionVector CapturedVector { get; }
    internal MarketRoleGuardPlan(long role, MarketRoleSnapshot original)
    {
        _role = role; _currency = original.Currency; _items = [.. original.Items.Select(ItemValue.Capture)];
        CapturedVector = original.Vector;
    }
    internal override int Bytes => 128 + _currency.Length * 2 + MarketItemsWritePlan.ItemBytes(_items);
    internal override string Label => "market/guard-participant";
    internal override IReadOnlyList<long> RolePins => [_role];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var balance = DbSql.Command(db, tx, "SELECT currency FROM role WHERE id=$id AND deleted_time=0");
        balance.Parameters.AddWithValue("$id", _role);
        if (balance.ExecuteScalar() is not string current || current != _currency ||
            !ItemStore.ReadItems(db, tx, _role).Select(ItemValue.Capture).SequenceEqual(_items))
            throw new InvalidOperationException($"Market participant {_role} changed during transaction capture.");
        return true;
    }
}

/// <summary>Preserve the exchange's owned-item predicates and generated-ID receipt without sharing bag objects.</summary>
internal sealed class MarketItemsWritePlan : DbPlan
{
    private readonly long _role;
    private readonly long[] _before;
    private readonly ItemValue[] _after;
    private readonly int[] _receiptSlots;
    internal int ExpectedReceiptCount => _receiptSlots.Length;
    internal MarketItemsWritePlan(long role, IReadOnlyList<InvItem> before, IReadOnlyList<InvItem> after)
    {
        _role = role; _before = [.. before.Select(x => x.Guid)];
        // The former synchronous loop assigned a new object's GUID before seeing the same reference again.
        // Preserve that identity, while distinct zero-GUID objects still create distinct rows.
        var unique = after.Distinct<InvItem>(ReferenceEqualityComparer.Instance).ToArray();
        var slots = new Dictionary<InvItem, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < unique.Length; i++) slots.Add(unique[i], i);
        _after = [.. unique.Select(ItemValue.Capture)];
        _receiptSlots = [.. after.Select(item => slots[item])];
    }
    internal static int ItemBytes(IEnumerable<ItemValue> items) => items.Sum(i => 160 +
        ((i.Attrs?.Length ?? 0) + (i.Enchantments?.Length ?? 0) + (i.StringAttrs?.Length ?? 0)) * 2);
    internal override int Bytes => 128 + _before.Length * 8 + _receiptSlots.Length * 4 + ItemBytes(_after);
    internal override string Label => "market/write-items";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(_role, RevisionStore.Inventory)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var kept = _after.Where(i => i.Guid != 0).Select(i => i.Guid).ToHashSet();
        foreach (long guid in _before.Where(i => !kept.Contains(i)))
        {
            using var remove = DbSql.Command(db, tx, "DELETE FROM item WHERE role_id=$r AND guid=$id");
            remove.Parameters.AddWithValue("$r", _role); remove.Parameters.AddWithValue("$id", guid);
            remove.ExecuteNonQuery();
        }
        var guids = new long[_after.Length];
        foreach (int i in _receiptSlots)
        {
            var item = _after[i];
            if (item.Guid == 0 && guids[i] != 0) item = item with { Guid = guids[i] };
            using var cmd = DbSql.Command(db, tx, item.Guid == 0
                ? "INSERT INTO item(role_id,entry,stack,pack,sort,grid,attrs,is_locked,enchantments,str_attrs,bind_type,bind_expire) VALUES($r,$e,$s,$p,$o,$g,$a,$l,$n,$sa,$bt,$be) RETURNING guid"
                : "UPDATE item SET entry=$e,stack=$s,pack=$p,sort=$o,grid=$g,attrs=$a,is_locked=$l,enchantments=$n,str_attrs=$sa,bind_type=$bt,bind_expire=$be WHERE role_id=$r AND guid=$id");
            cmd.Parameters.AddWithValue("$r", _role); item.Bind(cmd);
            if (item.Guid == 0) guids[i] = (long)cmd.ExecuteScalar()!;
            else
            {
                cmd.Parameters.AddWithValue("$id", item.Guid);
                if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException($"no item {item.Guid}");
                guids[i] = item.Guid;
            }
        }
        return _receiptSlots.Select(slot => guids[slot]).ToArray();
    }
}

internal sealed class MarketCurrencyWritePlan : DbPlan
{
    private readonly long _role;
    private readonly string _currency;
    internal MarketCurrencyWritePlan(long role, IReadOnlyDictionary<int, long> currency)
    { _role = role; _currency = string.Join(',', currency.Select(c => $"{c.Key}:{c.Value}")); }
    internal override int Bytes => 128 + _currency.Length * 2;
    internal override string Label => "market/write-currency";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(_role, RevisionStore.Progress)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "UPDATE role SET currency=$c WHERE id=$id AND deleted_time=0");
        cmd.Parameters.AddWithValue("$c", _currency); cmd.Parameters.AddWithValue("$id", _role);
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException($"Missing market role {_role}");
        return true;
    }
}

internal sealed class MarketBlobWritePlan : DbPlan
{
    private readonly long _role;
    private readonly string _column;
    private readonly byte[] _value;
    internal MarketBlobWritePlan(long role, string column, byte[] value)
    {
        if (column is not ("trade_info" or "stalls_info")) throw new ArgumentOutOfRangeException(nameof(column));
        _role = role; _column = column; _value = (byte[])value.Clone();
    }
    internal override int Bytes => 128 + _value.Length;
    internal override string Label => "market/write-role-blob";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(_role, _column)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, $"UPDATE role SET {_column}=$v WHERE id=$id");
        cmd.Parameters.AddWithValue("$v", _value); cmd.Parameters.AddWithValue("$id", _role);
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException($"Missing market role {_role}");
        return true;
    }
}
