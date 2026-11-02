using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Accounts;

/// <summary>
/// Stage G (C/D 15 step 3): a role's selection stages the default blobs its services write at login and commits them
/// as one asynchronous transaction at the end of the selection, still under its reservation. A first login made 15
/// synchronous writes on the game loop (about 15 ms of blocking per new role in a login storm).
/// <para>Ordering: anything else that writes or reads one of the staged columns of the role first writes the stage
/// synchronously, exactly as before - before it captures a revision vector, so its own revision check stays exact.
/// Other paths of the role are held off by the selection's reservation until the stage is committed.</para>
/// </summary>
public sealed partial class AccountStore
{
    internal static readonly IReadOnlySet<string> StagedColumns = new HashSet<string>(StringComparer.Ordinal)
        { "equip_plans", "mounts", "wild_afk", "daily", "wardrobe", "tasks", "task_runtime", "card_plans", "trade_info", "pets",
          "totems", "totem_refine_epoch", "achievements", "achievement_runtime", "job_exp_catchup" };
    private readonly Dictionary<long, Dictionary<string, byte[]>> _staged = [];

    internal void BeginStaging(long role)
    {
        lock (_staged) _staged.TryAdd(role, new Dictionary<string, byte[]>(StringComparer.Ordinal));
    }

    /// <summary>Stages a blob write of a staging role; false when the role or column is not staged (write it now).
    /// A column not staged yet whose value equals what the role already has is not written at all.</summary>
    internal bool TryStage(long role, string column, byte[] value, Func<bool> unchanged)
    {
        if (!StagedColumns.Contains(column)) return false;
        lock (_staged)
        {
            if (!_staged.TryGetValue(role, out var batch)) return false;
            if (!batch.ContainsKey(column) && unchanged()) return true;
            batch[column] = (byte[])value.Clone();
            return true;
        }
    }

    internal bool TryGetStaged(long role, string column, out byte[]? value)
    {
        lock (_staged)
        {
            value = _staged.TryGetValue(role, out var batch) && batch.TryGetValue(column, out var staged) ? (byte[])staged.Clone() : null;
            return value != null;
        }
    }

    /// <summary>Writes the role's stage now (synchronously) when it holds <paramref name="column"/> (or anything, when
    /// null), and stops staging the role.</summary>
    internal void FlushStaged(long role, string? column = null)
    {
        Dictionary<string, byte[]>? batch;
        lock (_staged)
        {
            if (_staged.Count == 0 || !_staged.TryGetValue(role, out batch) || column != null && !batch.ContainsKey(column)) return;
            _staged.Remove(role);
        }
        if (batch.Count == 0) return;
        var (builder, saves) = StagedBuilder(role, batch);
        bool committed = false;
        try { builder.Commit(this, StagedLabel); committed = true; }
        finally { EndStagedSaves(role, saves, committed); }
    }

    /// <summary>Commits the role's stage as one asynchronous transaction and stops staging the role.</summary>
    internal async Task CommitStagedAsync(long role)
    {
        Dictionary<string, byte[]>? batch;
        lock (_staged) { if (!_staged.Remove(role, out batch)) return; }
        if (batch.Count == 0) return;
        var (builder, saves) = StagedBuilder(role, batch);
        bool committed = false;
        try { await builder.CommitAsync(this, StagedLabel); committed = true; }
        finally { EndStagedSaves(role, saves, committed); }
    }

    internal int StagedCount(long role) { lock (_staged) return _staged.TryGetValue(role, out var batch) ? batch.Count : -1; }

    private const string StagedLabel = "role/login-blobs";

    private (RoleItemPlanBuilder Builder, (string Column, object Token, byte[] Value)[] Saves) StagedBuilder(
        long role, Dictionary<string, byte[]> batch)
    {
        var builder = NewTransaction();
        // The legacy save's snapshot write-through, per column: the session snapshot keeps serving the committed bytes.
        var saves = batch.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key, Snapshots.BeginSave(role, kv.Key), kv.Value)).ToArray();
        foreach (var (column, _, value) in saves) builder.RoleBlob(role, column, value);
        return (builder, saves);
    }

    private void EndStagedSaves(long role, (string Column, object Token, byte[] Value)[] saves, bool committed)
    {
        foreach (var (column, token, value) in saves) Snapshots.EndSave(role, column, token, value, committed);
    }

    /// <summary>Before a plan captures its vector: a staged column it writes is written first.</summary>
    private void FlushStagedFor(DbPlan plan)
    {
        lock (_staged) { if (_staged.Count == 0) return; }
        foreach (var key in plan.RoleWrites) FlushStaged(key.RoleId, key.Part);
    }
}
