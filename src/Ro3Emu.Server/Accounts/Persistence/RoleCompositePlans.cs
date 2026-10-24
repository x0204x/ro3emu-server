using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

internal class RolePlanConflictException : InvalidOperationException { }
internal sealed class OwnerRevisionConflictException : RolePlanConflictException { }

internal sealed class RoleDailyResetPlan : DbPlan
{
    private readonly long _id;
    private readonly byte[] _daily, _tasks;
    private readonly string _currency;
    internal RoleDailyResetPlan(long id, byte[] daily, byte[] tasks, string currency)
    {
        _id = id; _daily = (byte[])daily.Clone(); _tasks = (byte[])tasks.Clone(); _currency = currency;
    }
    internal override int Bytes => 128 + _daily.Length + _tasks.Length + _currency.Length * 2;
    internal override string Label => "role/daily-reset";
    internal override IReadOnlyList<long> RolePins => [_id];
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(_id, "daily"), RevisionStore.Role(_id, "tasks"), RevisionStore.Role(_id, RevisionStore.Progress)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "UPDATE role SET daily=$daily,tasks=$tasks,currency=$currency WHERE id=$id");
        cmd.Parameters.AddWithValue("$id", _id); cmd.Parameters.AddWithValue("$daily", _daily);
        cmd.Parameters.AddWithValue("$tasks", _tasks); cmd.Parameters.AddWithValue("$currency", _currency);
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException($"No role {_id} for daily reset");
        return true;
    }
}

internal sealed class RoleDailyAwardPlan : DbPlan
{
    private readonly long _id;
    private readonly byte[] _previousDaily, _daily;
    private readonly byte[]? _previousTasks, _tasks;
    private readonly RoleProgressValue _progress;
    internal RoleDailyAwardPlan(long id, byte[] previousDaily, byte[] daily, RoleProgress progress,
        byte[]? previousTasks, byte[]? tasks)
    {
        _id = id; _previousDaily = (byte[])previousDaily.Clone(); _daily = (byte[])daily.Clone();
        _previousTasks = previousTasks is null ? null : (byte[])previousTasks.Clone();
        _tasks = tasks is null ? null : (byte[])tasks.Clone(); _progress = RoleProgressValue.Capture(progress);
    }
    internal override int Bytes => 256 + _previousDaily.Length + _daily.Length + (_previousTasks?.Length ?? 0) +
        (_tasks?.Length ?? 0) + (_progress.Stats.Length + _progress.Currency.Length +
            _progress.Skills.Length + _progress.SkillBar.Length) * 2;
    internal override string Label => "role/daily-award";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => _tasks is null
        ? [RevisionStore.Role(_id, "daily"), RevisionStore.Role(_id, RevisionStore.Progress)]
        : [RevisionStore.Role(_id, "daily"), RevisionStore.Role(_id, "tasks"), RevisionStore.Role(_id, RevisionStore.Progress)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            UPDATE role SET daily=$daily,tasks=COALESCE($tasks,tasks),career=$career,level=$level,job_level=$jobLevel,
                exp=$exp,job_exp=$jobExp,currency=$currency
            WHERE id=$id AND daily=$previous AND ($tasks IS NULL OR tasks IS $previousTasks)
            """);
        cmd.Parameters.AddWithValue("$daily", _daily); cmd.Parameters.AddWithValue("$previous", _previousDaily);
        cmd.Parameters.AddWithValue("$tasks", (object?)_tasks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$previousTasks", (object?)_previousTasks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$career", _progress.Career); cmd.Parameters.AddWithValue("$level", _progress.Level);
        cmd.Parameters.AddWithValue("$jobLevel", _progress.JobLevel); cmd.Parameters.AddWithValue("$exp", _progress.Exp);
        cmd.Parameters.AddWithValue("$jobExp", _progress.JobExp); cmd.Parameters.AddWithValue("$currency", _progress.Currency);
        cmd.Parameters.AddWithValue("$id", _id);
        if (cmd.ExecuteNonQuery() != 1) throw new RolePlanConflictException();
        return true;
    }
}

internal sealed class RoleRingAcceptPlan : DbPlan
{
    private readonly long _id;
    private readonly byte[] _previous, _daily, _tasks;
    internal RoleRingAcceptPlan(long id, byte[] previous, byte[] daily, byte[] tasks)
    {
        _id = id; _previous = (byte[])previous.Clone(); _daily = (byte[])daily.Clone(); _tasks = (byte[])tasks.Clone();
    }
    internal override int Bytes => 128 + _previous.Length + _daily.Length + _tasks.Length;
    internal override string Label => "role/ring-accept";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(_id, "daily"), RevisionStore.Role(_id, "tasks")];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "UPDATE role SET daily=$daily,tasks=$tasks WHERE id=$id AND daily=$previous");
        cmd.Parameters.AddWithValue("$daily", _daily); cmd.Parameters.AddWithValue("$tasks", _tasks);
        cmd.Parameters.AddWithValue("$id", _id); cmd.Parameters.AddWithValue("$previous", _previous);
        return cmd.ExecuteNonQuery() == 1;
    }
}

internal sealed class RoleAwardStatePlan : DbPlan
{
    private readonly long _id;
    private readonly RoleProgressValue _progress;
    private readonly byte[]? _tasks, _runtime, _expectedDaily, _daily, _expectedTasks;
    private readonly bool _checkTasks;
    internal RoleAwardStatePlan(long id, RoleProgress progress, byte[]? tasks, byte[]? runtime,
        byte[]? expectedDaily, byte[]? daily, byte[]? expectedTasks, bool checkTasks)
    {
        _id = id; _progress = RoleProgressValue.Capture(progress);
        _tasks = Copy(tasks); _runtime = Copy(runtime); _expectedDaily = Copy(expectedDaily);
        _daily = Copy(daily); _expectedTasks = Copy(expectedTasks); _checkTasks = checkTasks;
    }
    private static byte[]? Copy(byte[]? value) => value is null ? null : (byte[])value.Clone();
    internal override int Bytes => 256 + (_tasks?.Length ?? 0) + (_runtime?.Length ?? 0) +
        (_expectedDaily?.Length ?? 0) + (_daily?.Length ?? 0) + (_expectedTasks?.Length ?? 0) +
        (_progress.Stats.Length + _progress.Currency.Length + _progress.Skills.Length + _progress.SkillBar.Length) * 2;
    internal override string Label => "role/award-state";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites =>
        new[] { RevisionStore.Role(_id, RevisionStore.Progress) }
            .Concat(_tasks is null ? [] : [RevisionStore.Role(_id, "tasks")])
            .Concat(_runtime is null ? [] : [RevisionStore.Role(_id, "task_runtime")])
            .Concat(_daily is null ? [] : [RevisionStore.Role(_id, "daily")]).ToArray();
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            UPDATE role SET career=$career,level=$level,job_level=$jl,exp=$exp,job_exp=$jexp,stats=$stats,
                bonus_points=$bonus,currency=$cur,starter_kit=$kit,skills=$skills,skill_bar=$bar,
                paragon_level=$plv,paragon_exp=$pexp,
                tasks=COALESCE($tasks,tasks),task_runtime=COALESCE($runtime,task_runtime),daily=COALESCE($daily,daily)
            WHERE id=$id AND ($checkDaily=0 OR daily IS $expectedDaily)
                AND ($checkTasks=0 OR tasks IS $expectedTasks)
            """);
        cmd.Parameters.AddWithValue("$id", _id); _progress.Bind(cmd);
        cmd.Parameters.AddWithValue("$tasks", (object?)_tasks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$runtime", (object?)_runtime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$daily", (object?)_daily ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$expectedDaily", (object?)_expectedDaily ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$checkDaily", _expectedDaily is null ? 0 : 1);
        cmd.Parameters.AddWithValue("$expectedTasks", (object?)_expectedTasks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$checkTasks", _checkTasks ? 1 : 0);
        if (cmd.ExecuteNonQuery() != 1) throw new RolePlanConflictException();
        return true;
    }
}

internal sealed class RoleCurrencyBlobPlan : DbPlan
{
    private readonly long _id;
    private readonly string _currency, _column;
    private readonly byte[] _value;
    private readonly bool _requireRole;
    internal RoleCurrencyBlobPlan(long id, IReadOnlyDictionary<int, long> currency, string column, byte[] value, bool requireRole)
    {
        RoleColumns.Validate(column);
        _id = id; _currency = string.Join(',', currency.Select(c => $"{c.Key}:{c.Value}"));
        _column = column; _value = (byte[])value.Clone(); _requireRole = requireRole;
    }
    internal override int Bytes => 128 + _currency.Length * 2 + _value.Length;
    internal override string Label => "role/currency-blob";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(_id, RevisionStore.Progress), RevisionStore.Role(_id, _column)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx,
            $"UPDATE role SET currency=$currency,{_column}=$value WHERE id=$id" + (_column == "season_talent" ? " AND deleted_time=0" : ""));
        cmd.Parameters.AddWithValue("$currency", _currency); cmd.Parameters.AddWithValue("$value", _value);
        cmd.Parameters.AddWithValue("$id", _id);
        int changed = cmd.ExecuteNonQuery();
        if (_requireRole && changed != 1) throw new InvalidOperationException($"no role {_id}");
        return changed;
    }
}

internal sealed class TowerFirstPassWritePlan : DbPlan
{
    private readonly long _floor;
    private readonly byte[] _record;
    internal TowerFirstPassWritePlan(long floor, byte[] record) { _floor = floor; _record = (byte[])record.Clone(); }
    internal override int Bytes => 128 + _record.Length;
    internal override string Label => "tower/first-pass";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "INSERT OR IGNORE INTO tower_first_pass(floor,record) VALUES($floor,$record)");
        cmd.Parameters.AddWithValue("$floor", _floor); cmd.Parameters.AddWithValue("$record", _record);
        return cmd.ExecuteNonQuery();
    }
}

internal sealed class TowerFirstPassReadPlan(long min, long max) : DbReadPlan
{
    internal override int Bytes => 128;
    internal override string Label => "tower/read-first-pass";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT floor,record FROM tower_first_pass WHERE floor BETWEEN $min AND $max ORDER BY floor");
        cmd.Parameters.AddWithValue("$min", min); cmd.Parameters.AddWithValue("$max", max);
        using var rows = cmd.ExecuteReader();
        var result = new Dictionary<long, byte[]>();
        while (rows.Read()) result[rows.GetInt64(0)] = (byte[])((byte[])rows[1]).Clone();
        return result;
    }
}

internal sealed class RoleGenreCodeReadPlan(string code, long now) : DbReadPlan
{
    internal override int Bytes => 128 + code.Length * 2;
    internal override string Label => "role/genre-code";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT id,genre FROM role WHERE deleted_time=0 AND genre IS NOT NULL");
        using var rows = cmd.ExecuteReader();
        while (rows.Read())
        {
            var state = Romsg.DBGenre.Parser.ParseFrom((byte[])rows[1]);
            if (state.CodeInfo?.CodeInfo == code && state.CodeInfo.ExpireTime > now) return rows.GetInt64(0);
        }
        return null;
    }
}

internal sealed class ItemTalentCostPlan : DbPlan
{
    private readonly long _role;
    private readonly KeyValuePair<long, int>[] _stacks;
    private readonly long[] _removed;
    internal ItemTalentCostPlan(long role, IReadOnlyDictionary<long, int> stacks, IReadOnlyCollection<long> removed)
    {
        _role = role; _stacks = stacks.ToArray(); _removed = removed.ToArray();
    }
    internal override int Bytes => 128 + _stacks.Length * 16 + _removed.Length * 8;
    internal override string Label => "item/talent-cost";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(_role, RevisionStore.Inventory)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        foreach (var (guid, stack) in _stacks)
        {
            using var cmd = DbSql.Command(db, tx, "UPDATE item SET stack=$stack WHERE guid=$id AND role_id=$role AND grid=0");
            cmd.Parameters.AddWithValue("$stack", stack); cmd.Parameters.AddWithValue("$id", guid);
            cmd.Parameters.AddWithValue("$role", _role);
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException($"talent cost item {guid} changed");
        }
        foreach (long guid in _removed)
        {
            using var cmd = DbSql.Command(db, tx, "DELETE FROM item WHERE guid=$id AND role_id=$role AND grid=0");
            cmd.Parameters.AddWithValue("$id", guid); cmd.Parameters.AddWithValue("$role", _role);
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException($"talent cost item {guid} changed");
        }
        return true;
    }
}

/// <summary>Pet exchange keeps the old before/after delta, including its permissive deletes and checked updates.</summary>
internal sealed class ItemExchangeWritePlan : DbPlan
{
    private readonly long _role;
    private readonly long[] _before;
    private readonly ItemValue[] _after;
    internal ItemExchangeWritePlan(long role, IReadOnlyList<InvItem> before, IReadOnlyList<InvItem> after)
    {
        _role = role; _before = before.Select(i => i.Guid).ToArray(); _after = after.Select(ItemValue.Capture).ToArray();
    }
    internal override int Bytes => 128 + _before.Length * 8 + _after.Sum(i => 160 +
        (i.Attrs?.Length ?? 0) * 2 + (i.Enchantments?.Length ?? 0) * 2 + (i.StringAttrs?.Length ?? 0) * 2);
    internal override string Label => "item/pet-exchange";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(_role, RevisionStore.Inventory)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var kept = _after.Where(i => i.Guid != 0).Select(i => i.Guid).ToHashSet();
        foreach (long guid in _before.Where(guid => !kept.Contains(guid)))
        {
            using var delete = DbSql.Command(db, tx, "DELETE FROM item WHERE role_id=$r AND guid=$id");
            delete.Parameters.AddWithValue("$r", _role); delete.Parameters.AddWithValue("$id", guid);
            delete.ExecuteNonQuery();
        }
        var guids = new long[_after.Length];
        for (int i = 0; i < _after.Length; i++)
        {
            var item = _after[i];
            using var cmd = DbSql.Command(db, tx, item.Guid == 0
                ? "INSERT INTO item(role_id,entry,stack,pack,sort,grid,attrs,is_locked,enchantments,str_attrs,bind_type,bind_expire) VALUES($r,$e,$s,$p,$o,$g,$a,$l,$n,$sa,$bt,$be) RETURNING guid"
                : "UPDATE item SET entry=$e,stack=$s,pack=$p,sort=$o,grid=$g,attrs=$a,is_locked=$l,enchantments=$n,str_attrs=$sa,bind_type=$bt,bind_expire=$be WHERE role_id=$r AND guid=$id");
            cmd.Parameters.AddWithValue("$r", _role);
            if (item.Guid != 0) cmd.Parameters.AddWithValue("$id", item.Guid);
            item.Bind(cmd);
            if (item.Guid == 0) guids[i] = (long)cmd.ExecuteScalar()!;
            else
            {
                if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException($"no item {item.Guid}");
                guids[i] = item.Guid;
            }
        }
        return guids;
    }
}

internal sealed class ItemAwardUpsertPlan : DbPlan
{
    private readonly long _role;
    private readonly ItemValue[] _items;
    internal ItemAwardUpsertPlan(long role, IReadOnlyList<InvItem> items)
    {
        _role = role; _items = items.Select(ItemValue.Capture).ToArray();
    }
    internal override int Bytes => 128 + _items.Sum(i => 160 + (i.Attrs?.Length ?? 0) * 2 +
        (i.Enchantments?.Length ?? 0) * 2 + (i.StringAttrs?.Length ?? 0) * 2);
    internal override string Label => "item/award-upsert";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [RevisionStore.Role(_role, RevisionStore.Inventory)];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var guids = new long[_items.Length];
        for (int i = 0; i < _items.Length; i++)
        {
            var item = _items[i];
            using var cmd = DbSql.Command(db, tx, item.Guid == 0
                ? "INSERT INTO item(role_id,entry,stack,pack,sort,grid,attrs,is_locked,enchantments,str_attrs,bind_type,bind_expire) VALUES($r,$e,$s,$p,$o,$g,$a,$l,$n,$sa,$bt,$be) RETURNING guid"
                : "UPDATE item SET entry=$e,stack=$s,pack=$p,sort=$o,grid=$g,attrs=$a,is_locked=$l,enchantments=$n,str_attrs=$sa,bind_type=$bt,bind_expire=$be WHERE guid=$id AND role_id=$r");
            cmd.Parameters.AddWithValue("$r", _role);
            if (item.Guid != 0) cmd.Parameters.AddWithValue("$id", item.Guid);
            item.Bind(cmd);
            if (item.Guid == 0) guids[i] = (long)cmd.ExecuteScalar()!;
            else
            {
                if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException($"Missing item {item.Guid}");
                guids[i] = item.Guid;
            }
        }
        return guids;
    }
}
