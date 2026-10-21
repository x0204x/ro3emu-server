using Microsoft.Data.Sqlite;

using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Accounts;

/// <summary>
/// One item instance (ItemBaseInfo): <see cref="Guid"/> is the client's itemGuid, <see cref="Entry"/> the ItemConfig id.
/// In the bag when <see cref="Grid"/> is 0 (pack = BackpackConfig tab, sort = sortedIndex), else equipped in that
/// EquipGridType slot.
/// </summary>
public sealed class InvItem
{
    public long Guid { get; set; }
    public int Entry { get; set; }
    public int Stack { get; set; }
    public int Pack { get; set; }
    public long Sort { get; set; }
    public int Grid { get; set; }

    /// <summary>Decomposition protection: ItemBaseInfo.isLocked, not an itemInfos key.</summary>
    public bool IsLocked { get; set; }

    /// <summary>
    /// Per-instance ItemBaseInfo.itemInfos (ItemAttrTypeEnum keys: rolled base stats 30-49 / 60-79, entries, appraisal
    /// ...), stored as JSON; null for plain stackable items.
    /// </summary>
    public Dictionary<int, long>? Attrs { get; set; }
    /// <summary>String itemInfos values (card cost history 510-529).</summary>
    public Dictionary<int, string>? StringAttrs { get; set; }
    public int BindType { get; set; }
    public long BindExpire { get; set; }
    /// <summary>Legacy award name for ItemBindInfo.expireTimeStamp.</summary>
    public long ExpireTimeStamp { get => BindExpire; set => BindExpire = value; }

    /// <summary>ItemBaseInfo.enchantment, keyed by Enchantment row id.</summary>
    public Dictionary<int, ItemEnchantment> Enchantments { get; set; } = new();

    /// <summary>A detached candidate; publish its changed state only after its database transaction commits.</summary>
    internal InvItem Clone() => new()
    {
        Guid = Guid, Entry = Entry, Stack = Stack, Pack = Pack, Sort = Sort, Grid = Grid,
        Attrs = Attrs == null ? null : new(Attrs),
        StringAttrs = StringAttrs == null ? null : new(StringAttrs), BindType = BindType, BindExpire = BindExpire, IsLocked = IsLocked,
        Enchantments = Enchantments.ToDictionary(p => p.Key, p => new ItemEnchantment { Attr = new(p.Value.Attr), GetTime = p.Value.GetTime }),
    };
}

/// <summary>One persisted EnchantAttr: actual rolled stats and acquisition time, separate from itemInfos.</summary>
public sealed class ItemEnchantment
{
    public Dictionary<int, int> Attr { get; set; } = new();
    public long GetTime { get; set; }
}

/// <summary>Items of all roles, in the shared SQLite file.</summary>
public sealed class ItemStore(AccountStore accounts)
{
    private readonly AccountStore _accounts = accounts;

    public List<InvItem> Load(long roleId)
    {
        if (_accounts.Snapshots.TryItems(roleId, out var snapshot) && snapshot != null)
            return SnapshotVerify.Same(snapshot, () => _accounts.ReadSync<List<InvItem>>(new ItemReadPlan(roleId)), $"role {roleId} items")!;
        return _accounts.ReadSync<List<InvItem>>(new ItemReadPlan(roleId));
    }

    internal static List<InvItem> ReadItems(SqliteConnection db, SqliteTransaction tx, long roleId)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT guid, entry, stack, pack, sort, grid, attrs, is_locked, enchantments, str_attrs, bind_type, bind_expire FROM item WHERE role_id = $r ORDER BY guid";
        cmd.Parameters.AddWithValue("$r", roleId);
        using var r = cmd.ExecuteReader();
        var items = new List<InvItem>();
        while (r.Read()) items.Add(Row(r));
        return items;
    }

    /// <summary>
    /// Instances with per-instance itemInfos (equipment, Soul Echoes, cards) of every live role, for leaderboards that
    /// rank offline roles too. Plain stackables (attrs NULL) are skipped.
    /// </summary>
    internal static Dictionary<long, List<InvItem>> ReadAttributedItems(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT i.guid, i.entry, i.stack, i.pack, i.sort, i.grid, i.attrs, i.is_locked, i.enchantments, i.str_attrs, i.bind_type, i.bind_expire, i.role_id "
            + "FROM item i JOIN role r ON r.id = i.role_id WHERE r.deleted_time = 0 AND i.attrs IS NOT NULL ORDER BY i.guid";
        using var r = cmd.ExecuteReader();
        var items = new Dictionary<long, List<InvItem>>();
        while (r.Read())
        {
            long role = r.GetInt64(12);
            if (!items.TryGetValue(role, out var list)) items[role] = list = new();
            list.Add(Row(r));
        }
        return items;
    }

    private static InvItem Row(SqliteDataReader r) => new()
    {
        Guid = r.GetInt64(0), Entry = r.GetInt32(1), Stack = r.GetInt32(2), Pack = r.GetInt32(3), Sort = r.GetInt64(4), Grid = r.GetInt32(5),
        Attrs = r.IsDBNull(6) ? null : System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, long>>(r.GetString(6)),
        IsLocked = r.GetBoolean(7),
        Enchantments = r.IsDBNull(8) ? new() : System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, ItemEnchantment>>(r.GetString(8)) ?? new(),
        StringAttrs = r.IsDBNull(9) ? null : System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, string>>(r.GetString(9)),
        BindType = r.GetInt32(10), BindExpire = r.GetInt64(11),
    };

    /// <summary>Stores a new instance and assigns its guid.</summary>
    public void Insert(long roleId, InvItem item, RoleItemPlanBuilder? transaction = null)
    {
        var plan = new ItemWritePlan(ItemWriteKind.Insert, role: roleId, items: [item]);
        if (transaction == null) item.Guid = _accounts.WriteLegacySync<long[]>(plan)[0];
        else transaction.Add(plan, receipt => item.Guid = ((long[])receipt!)[0]);
    }

    public void Update(InvItem item, RoleItemPlanBuilder? transaction = null)
    {
        var plan = new ItemWritePlan(ItemWriteKind.Update, items: [item]);
        if (transaction == null) _accounts.WriteLegacySync<long[]>(plan);
        else transaction.Add(plan);
    }

    public void Delete(long guid, RoleItemPlanBuilder? transaction = null)
    {
        var plan = new ItemWritePlan(ItemWriteKind.Delete, guid: guid);
        if (transaction == null) _accounts.WriteLegacySync<int>(plan);
        else transaction.Add(plan);
    }

    /// <summary>Commit grid moves and their selected/saved plans together (inferred crash-consistency invariant).</summary>
    public void SaveEquipment(long roleId, IEnumerable<InvItem> moved, byte[] plans)
    {
        _accounts.WriteLegacySync<int>(new ItemWritePlan(ItemWriteKind.Equipment, role: roleId,
            items: moved.DistinctBy(i => i.Guid).ToArray(), plans: plans));
    }

    /// <summary>Appraisal batches are all-or-nothing in SQLite as well as under the session lock.</summary>
    internal void UpdateBatch(IEnumerable<InvItem> items)
    {
        _accounts.WriteLegacySync<long[]>(new ItemWritePlan(ItemWriteKind.UpdateBatch, items: items.ToArray()));
    }

    /// <summary>
    /// Commits an item-only exchange as one durable change (emblem payment plus upgraded attrs).
    /// Zero stacks delete their instance; callers publish these staged values to the session only after commit.
    /// </summary>
    internal void ApplyChanges(long roleId, IReadOnlyList<InvItem> changes)
    {
        _accounts.WriteLegacySync<long[]>(new ItemWritePlan(ItemWriteKind.ApplyChanges, role: roleId, items: changes));
    }

}
