using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>Only current running-task counters, frozen at the death's owned admission, enter the parent kill commit.</summary>
internal sealed record KillTaskProgress(long RoleId, byte[]? BeforeTasks, byte[] AfterTasks,
    byte[]? BeforeFollowup, byte[] AfterFollowup);

internal sealed class KillTaskProgressPlan : DbPlan
{
    private readonly KillTaskProgress[] _progress;
    internal string SourceKey { get; }
    internal IReadOnlyList<long> CreditedRoles => [.. _progress.Select(x => x.RoleId)];
    internal KillTaskProgressPlan(string sourceKey, IReadOnlyList<KillTaskProgress> progress)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || progress.Count == 0 ||
            progress.Any(x => x.RoleId <= 0) ||
            progress.Select(x => x.RoleId).Distinct().Count() != progress.Count)
            throw new ArgumentException("Invalid frozen kill task progress.", nameof(progress));
        SourceKey = sourceKey;
        _progress = [.. progress.OrderBy(x => x.RoleId).Select(x => new KillTaskProgress(x.RoleId,
            x.BeforeTasks is null ? null : (byte[])x.BeforeTasks.Clone(), (byte[])x.AfterTasks.Clone(),
            x.BeforeFollowup is null ? null : (byte[])x.BeforeFollowup.Clone(), (byte[])x.AfterFollowup.Clone()))];
    }
    internal override int Bytes => 128 + _progress.Sum(x =>
        (x.BeforeTasks?.Length ?? 0) + x.AfterTasks.Length +
        (x.BeforeFollowup?.Length ?? 0) + x.AfterFollowup.Length + 128);
    internal override string Label => "task/kill-progress";
    internal override IReadOnlyList<RoleRevisionKey> RoleWrites => [.. _progress
        .SelectMany(x => new[] { RevisionStore.Role(x.RoleId, "tasks"),
            RevisionStore.Role(x.RoleId, "task_followup") })];
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        foreach (var item in _progress)
        {
            using var cmd = DbSql.Command(db, tx, """
                UPDATE role SET tasks=$tasks,task_followup=$followup
                WHERE id=$role AND tasks IS $beforeTasks AND task_followup IS $beforeFollowup
                """);
            cmd.Parameters.AddWithValue("$role", item.RoleId);
            cmd.Parameters.AddWithValue("$tasks", item.AfterTasks);
            cmd.Parameters.AddWithValue("$followup", item.AfterFollowup);
            cmd.Parameters.AddWithValue("$beforeTasks", (object?)item.BeforeTasks ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$beforeFollowup", (object?)item.BeforeFollowup ?? DBNull.Value);
            if (cmd.ExecuteNonQuery() != 1) throw new RolePlanConflictException();
        }
        return _progress.Select(x => x.RoleId).ToArray();
    }
}
