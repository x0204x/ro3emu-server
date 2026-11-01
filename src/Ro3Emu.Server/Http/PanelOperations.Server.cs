using System.Globalization;
using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Http;

/// <summary>
/// Server control: a restart, shutdown or restore at a set time with countdown notices on the GM marquee
/// (BROADCAST_NOTIFY 83270, as the panel's broadcasts), then the server's own clean stop (the same as Ctrl+C and POST
/// /ro3emu/shutdown: the loop drains, the database closes). The process exits with <see cref="RestartExitCode"/> for a
/// restart or restore and 0 for a shutdown, so a process manager that restarts on failure (systemd
/// RestartPreventExitStatus=0, docs/guide/panel.md) brings back only the first. A restore writes the backup's path to
/// <see cref="DatabaseRestore.RequestName"/>; the next start restores it before it opens the realm, exactly as
/// --restore-backup does, so the running database is never swapped. A plan lives in memory: a restart forgets it.
/// </summary>
public sealed partial class PanelOperations
{
    /// <summary>EX_TEMPFAIL (sysexits.h): the server stopped to be started again.</summary>
    public const int RestartExitCode = 75;

    /// <summary>(inferred) When the countdown notices go out before the stop, besides one when it is planned.</summary>
    private static readonly TimeSpan[] Notices = [TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)];

    private readonly object _scheduleGate = new();
    private PanelScheduled? _scheduled;
    private CancellationTokenSource? _scheduleCancel;
    /// <summary>The last step of an update's or rollback's restart (Updates.UpdateManager), and whether it runs now:
    /// from then on the plan can no longer be cancelled or replaced.</summary>
    private Func<Task<string?>>? _step;
    private bool _stepping;

    /// <summary>The kind of the plan waiting for its countdown (restart, update...), or null.</summary>
    internal string? PlannedKind
    {
        get { lock (_scheduleGate) return _scheduled?.Kind; }
    }

    public async Task<PanelServerState> ServerStateAsync()
    {
        var m = maintenance.Current;
        PanelScheduled? scheduled;
        lock (_scheduleGate) scheduled = _scheduled;
        string result = Path.Combine(options.Value.DataDir, DatabaseRestore.ResultName);
        string? last = File.Exists(result) ? (await File.ReadAllTextAsync(result)).Trim() : null;
        return new PanelServerState(new PanelMaintenance(m.On, m.Note, m.By, m.Since > 0 ? DateTimeOffset.FromUnixTimeSeconds(m.Since) : null),
            scheduled, Backups(), last);
    }

    public Task SetMaintenanceAsync(string admin, bool enabled, string note) => maintenance.SetAsync(admin, enabled, note);

    // ---------------------------------------------------------------- backups

    private List<PanelBackupFile> Backups()
    {
        var dir = new DirectoryInfo(backups.Directory);
        if (!dir.Exists) return [];
        return [.. dir.EnumerateFiles(DatabaseBackup.Prefix + "*")
            .Where(f => f.Name.EndsWith(DatabaseBackup.Extension, StringComparison.Ordinal) || f.Name.EndsWith(DatabaseBackup.CompressedExtension, StringComparison.Ordinal))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new PanelBackupFile(f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero),
                f.Name.EndsWith(DatabaseBackup.CompressedExtension, StringComparison.Ordinal)))];
    }

    /// <summary>The full path of a listed backup, or null: only the backup folder's own files.</summary>
    private string? BackupPath(string name) =>
        Backups().Any(b => string.Equals(b.Name, name, StringComparison.Ordinal)) ? Path.Combine(backups.Directory, name) : null;

    public async Task<string> BackupNowAsync(string admin)
    {
        string path;
        try { path = await Task.Run(backups.Backup); }
        catch (Exception e) when (e is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            LogBackupFailed(log, e);
            throw new PanelRefusalException("The backup failed; the server log says why.");
        }
        string name = Path.GetFileName(path);
        await AuditAsync(admin, AdminAudit.Backup, "server", "backup made: " + name);
        return name;
    }

    public async Task<string?> BackupFileAsync(string admin, string name)
    {
        if (BackupPath(name) is not { } path) return null;
        await AuditAsync(admin, AdminAudit.Backup, "server", "backup downloaded: " + name);
        return path;
    }

    // ---------------------------------------------------------------- restart, shutdown, restore

    public async Task<PanelActionResult> ScheduleAsync(string admin, PanelSchedule schedule)
    {
        string? backup = null;
        if (schedule.Kind == "restore" && (backup = schedule.Backup is { } name ? BackupPath(name) : null) == null)
            return Refused("There is no such backup.");
        var plan = new PanelScheduled(schedule.Kind, clock.GetUtcNow().AddMinutes(schedule.Minutes), schedule.Reason, admin, schedule.Backup);
        CancellationTokenSource cancel;
        lock (_scheduleGate)
        {
            if (_stepping) return Refused("The server is installing an update and restarts now.");
            _scheduleCancel?.Cancel();
            _scheduled = plan;
            _step = null;
            cancel = _scheduleCancel = new CancellationTokenSource();
        }
        await AuditAsync(admin, AdminAudit.Schedule, "server", plan.Kind + (plan.Backup != null ? " from " + plan.Backup : "") + " at "
            + plan.At.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture) + (plan.Reason.Length > 0 ? ": " + plan.Reason : ""));
        _ = RunScheduleAsync(plan, backup, cancel.Token);
        return new PanelActionResult(true, "Planned for " + plan.At.ToString("HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) + ".");
    }

    /// <summary>
    /// A restart the update manager plans (<paramref name="kind"/> update or rollback): the same countdown, then
    /// <paramref name="step"/>, its install or rollback, right before the stop. A step that refuses (its message)
    /// cancels the restart and the players are told. Refused while another plan waits, so an update never replaces one
    /// silently; the update manager audits it.
    /// </summary>
    internal PanelActionResult ScheduleStep(string admin, string kind, int minutes, string reason, string version, Func<Task<string?>> step)
    {
        var plan = new PanelScheduled(kind, clock.GetUtcNow().AddMinutes(minutes), reason, admin, null, version);
        CancellationTokenSource cancel;
        lock (_scheduleGate)
        {
            if (_scheduled is { } other) return Refused("A " + Word(other.Kind) + " is already planned; cancel it first.");
            _scheduled = plan;
            _step = step;
            cancel = _scheduleCancel = new CancellationTokenSource();
        }
        _ = RunScheduleAsync(plan, null, cancel.Token);
        return new PanelActionResult(true, "Planned for " + plan.At.ToString("HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) + ".");
    }

    public void Dispose()
    {
        lock (_scheduleGate) _scheduleCancel?.Cancel();
        _scheduleCancel?.Dispose();
    }

    public async Task<bool> CancelScheduleAsync(string admin)
    {
        PanelScheduled? plan;
        lock (_scheduleGate)
        {
            plan = _scheduled;
            if (plan == null || _stepping) return false;
            _scheduleCancel?.Cancel();
            (_scheduled, _scheduleCancel, _step) = (null, null, null);
        }
        await MarqueeAsync("The planned " + Word(plan.Kind) + " is cancelled.");
        await AuditAsync(admin, AdminAudit.Schedule, "server", "cancelled the " + plan.Kind + " planned for "
            + plan.At.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>The countdown: a notice now and at each of <see cref="Notices"/> still ahead, then the stop.</summary>
    private async Task RunScheduleAsync(PanelScheduled plan, string? backup, CancellationToken cancel)
    {
        try
        {
            await MarqueeAsync(Notice(plan, plan.At - clock.GetUtcNow()));
            foreach (var before in Notices.Where(n => plan.At - n > clock.GetUtcNow() + TimeSpan.FromSeconds(1)))
            {
                await Task.Delay(plan.At - before - clock.GetUtcNow(), clock, cancel);
                await MarqueeAsync(Notice(plan, before));
            }
            var left = plan.At - clock.GetUtcNow();
            if (left > TimeSpan.Zero) await Task.Delay(left, clock, cancel);
            Func<Task<string?>>? step;
            lock (_scheduleGate)
            {
                if (cancel.IsCancellationRequested || !ReferenceEquals(_scheduled, plan)) return;
                step = _step;
                _stepping = step != null;
            }
            if (step != null && await RunStepAsync(plan, step) is { } refused)
            {
                LogStepRefused(log, plan.Kind, refused);
                await MarqueeAsync("The planned restart is cancelled.");
                return;
            }
            StopServer(plan, backup);
        }
        catch (OperationCanceledException) { } // cancelled or replaced
        catch (Exception e) { LogScheduleFailed(log, e, plan.Kind); }
    }

    /// <summary>The plan's last step; a refusal (or a failure) ends the plan so the server keeps running.</summary>
    private async Task<string?> RunStepAsync(PanelScheduled plan, Func<Task<string?>> step)
    {
        string? refused;
        try { refused = await step(); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogScheduleFailed(log, e, plan.Kind);
            refused = e.Message;
        }
        if (refused == null) return null;
        lock (_scheduleGate)
        {
            _stepping = false;
            if (ReferenceEquals(_scheduled, plan)) (_scheduled, _scheduleCancel, _step) = (null, null, null);
        }
        return refused;
    }

    private void StopServer(PanelScheduled plan, string? backup)
    {
        if (backup != null) DatabaseRestore.Request(options.Value.DataDir, backup);
        Environment.ExitCode = plan.Kind == "shutdown" ? 0 : RestartExitCode;
        LogStopping(log, plan.Kind, plan.By);
        lifetime.StopApplication();
    }

    private static string Word(string kind) => kind == "shutdown" ? "shutdown" : "restart";

    /// <summary>(inferred) The countdown line; the client has no scheduled-shutdown message we know.</summary>
    private static string Notice(PanelScheduled plan, TimeSpan left)
    {
        string when = left.TotalSeconds < 60 ? Math.Max(1, (int)Math.Round(left.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + " seconds"
            : (int)Math.Round(left.TotalMinutes) is var m && m == 1 ? "1 minute" : m.ToString(CultureInfo.InvariantCulture) + " minutes";
        string what = plan.Kind switch
        {
            "shutdown" => "The server shuts down in ",
            "update" => "The server restarts for an update in ",
            _ => "The server restarts in ",
        };
        return what + when + "." + (plan.Reason.Length > 0 ? " " + plan.Reason : "");
    }

    /// <summary>The GM marquee to every character in the game; how many saw it.</summary>
    private async Task<int> MarqueeAsync(string text)
    {
        try
        {
            return await loop.InvokeAsync(async () =>
            {
                var targets = sessions.All().Where(s => s.Role != null).ToList();
                foreach (var session in targets)
                {
                    try { await GmCommands.ReplyAsync(session, text); }
                    catch (Exception e) when (e is not OperationCanceledException) { LogMarqueeFailed(log, e, session.Account ?? ""); }
                }
                return targets.Count;
            });
        }
        catch (OperationCanceledException) { return 0; }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Panel backup failed")]
    private static partial void LogBackupFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Panel: the planned {Kind} failed")]
    private static partial void LogScheduleFailed(ILogger logger, Exception error, string kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Panel: the planned {Kind} is cancelled: {Reason}")]
    private static partial void LogStepRefused(ILogger logger, string kind, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Panel: planned {Kind} by {Admin}, the server stops now")]
    private static partial void LogStopping(ILogger logger, string kind, string admin);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Panel countdown notice to {Account} failed")]
    private static partial void LogMarqueeFailed(ILogger logger, Exception error, string account);
}
