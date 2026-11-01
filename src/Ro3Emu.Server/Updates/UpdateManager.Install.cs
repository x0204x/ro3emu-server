using Microsoft.Data.Sqlite;
using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Updates;

/// <summary>
/// Download, install and rollback. A download runs in the background and the page follows its state; an install or
/// rollback is a restart planned through PanelOperations (countdown notices in the game) whose last step runs here.
/// </summary>
public sealed partial class UpdateManager
{
    /// <summary>Pre-update database backups kept in updates/backups (the backup rotation never deletes them).</summary>
    private const int UpdateBackupsKept = 3;

    private static PanelActionResult Refused(string message) => new(false, message);
    private static PanelActionResult Ok(string message) => new(true, message);

    /// <summary>Why nothing may start now (a download, a planned or running install), or null.</summary>
    private string? Busy()
    {
        if (_installing) return "An update is being installed.";
        if (_job.Running) return "A download is running.";
        return _ops.PlannedKind is { } kind ? "A " + (kind == "shutdown" ? "shutdown" : "restart") + " is planned; cancel it first." : null;
    }

    // ---------------------------------------------------------------- download

    public async Task<PanelActionResult> DownloadAsync(string admin, string tag)
    {
        if (!Hosting.CanDownload) return Refused("This server runs from a build of the source tree: update it with git and build again.");
        if (_platform == null) return Refused("The releases have no package for this platform (" + _platformName + ").");
        ReleaseInfo? release;
        lock (_gate) release = _cache.Releases.FirstOrDefault(r => string.Equals(r.Tag, tag, StringComparison.Ordinal));
        if (release == null) return Refused("That release is not in the list; check for updates first.");
        if (_installed is { } v && release.Version <= v) return Refused(release.Tag + " is not newer than the installed version " + Installed + ".");
        if (_platform.Pick(release) is not { } asset) return Refused(release.Tag + " has no " + _platform.Asset + ".");
        if (asset.Sha256 == null) return Refused("GitHub lists no SHA-256 digest for " + asset.Name + " of " + release.Tag + ", so it cannot be checked. It is not downloaded.");
        if (!_feed.AllowedDownload(asset.Url)) return Refused("The release's download address is not https.");
        CancellationTokenSource cancel;
        lock (_gate)
        {
            if (Busy() is { } busy) return Refused(busy);
            _download?.Dispose();
            _download = cancel = new CancellationTokenSource();
            _job = new UpdateJob("downloading", release.Tag, 0, asset.Size, null);
        }
        await AuditAsync(admin, "download of " + release.Tag + " (" + asset.Name + ") started");
        _ = Task.Run(() => RunDownloadAsync(admin, release, asset, cancel.Token));
        return Ok("The download of " + release.Tag + " started.");
    }

    /// <summary>Download, unpack, check, then the staged record; a failure leaves the reason on the page and in the audit log.</summary>
    private async Task RunDownloadAsync(string admin, ReleaseInfo release, ReleaseAsset asset, CancellationToken ct)
    {
        string staging = Path.Combine(_paths.Staging, release.Tag);
        try
        {
            string archive = Path.Combine(_paths.Downloads, asset.Name);
            await UpdatePackage.DownloadAsync(_http, asset, archive, UserAgent, new InlineProgress(done => SetJob(j => j with { Done = done })), ct);
            SetJob(j => j with { Phase = "extracting" });
            string root = await Task.Run(() => UpdatePackage.Extract(archive, _platform!, staging), ct);
            SetJob(j => j with { Phase = "checking" });
            string version = await Task.Run(() => UpdatePackage.Check(root, _platform!, release.Version), ct);
            var staged = new StagedRecord(release.Tag, version, root, asset.Size, asset.Sha256!, Now, _platformName, archive);
            UpdateInstaller.WriteJson(_paths.Staged, staged);
            DeleteOtherStaging(release.Tag, asset.Name);
            lock (_gate) (_staged, _job) = (staged, UpdateJob.Idle);
            await AuditAsync(admin, release.Tag + " downloaded and checked (sha256 " + asset.Sha256 + "), ready to install");
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) // cancelled, or the server stops
        {
            DeleteQuietly(staging);
            lock (_gate) _job = UpdateJob.Idle;
        }
        catch (Exception e) when (e is UpdateException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            DeleteQuietly(staging);
            string message = e is UpdateException ? e.Message : "The download failed: " + e.Message;
            lock (_gate) _job = _job with { Phase = "failed", Message = message };
            LogDownloadFailed(_log, e, release.Tag);
            await AuditAsync(admin, "download of " + release.Tag + " failed: " + message);
        }
    }

    private void SetJob(Func<UpdateJob, UpdateJob> change)
    {
        lock (_gate) _job = change(_job);
    }

    private sealed class InlineProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    /// <summary>One staged release and one archive at a time: the others go.</summary>
    private void DeleteOtherStaging(string tag, string archive)
    {
        foreach (string dir in Directory.EnumerateDirectories(_paths.Staging))
            if (!string.Equals(Path.GetFileName(dir), tag, StringComparison.Ordinal)) DeleteQuietly(dir);
        foreach (string file in Directory.EnumerateFiles(_paths.Downloads))
            if (!string.Equals(Path.GetFileName(file), archive, StringComparison.Ordinal)) UpdatePackage.TryDelete(file);
    }

    private static void DeleteQuietly(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public async Task<PanelActionResult> CancelDownloadAsync(string admin)
    {
        string? tag;
        lock (_gate)
        {
            if (!_job.Running || _download == null) return Refused("No download is running.");
            tag = _job.Tag;
            _download.Cancel();
        }
        await AuditAsync(admin, "download of " + tag + " cancelled");
        return Ok("The download is cancelled.");
    }

    public async Task<PanelActionResult> DiscardAsync(string admin)
    {
        string? tag;
        lock (_gate)
        {
            if (Busy() is { } busy) return Refused(busy);
            if (_staged == null && _job.Phase != "failed") return Refused("Nothing is downloaded.");
            tag = _staged?.Tag ?? _job.Tag;
            (_staged, _job) = (null, UpdateJob.Idle);
        }
        UpdatePackage.TryDelete(_paths.Staged);
        DeleteQuietly(_paths.Staging);
        DeleteQuietly(_paths.Downloads);
        await AuditAsync(admin, "downloaded " + tag + " discarded");
        return Ok("The download is deleted.");
    }

    // ---------------------------------------------------------------- install

    public async Task<PanelActionResult> InstallAsync(string admin, PanelUpdatePlan plan)
    {
        if (!Hosting.CanInstall) return Refused(HostingSummary() + " Install it as the steps on this page say.");
        StagedRecord? staged;
        lock (_gate) staged = _staged;
        if (staged == null) return Refused("Download a release first.");
        if (_installed is { } v && ReleaseVersion.TryParse(staged.Version, out var to) && to <= v)
            return Refused(staged.Tag + " is not newer than the installed version " + Installed + ".");
        if (!File.Exists(Path.Combine(staged.Folder, _platform!.ServerFile))) return Refused("The downloaded files are gone; download the release again.");
        PanelActionResult result;
        lock (_gate)
        {
            if (Busy() is { } busy) return Refused(busy);
            result = _ops.ScheduleStep(admin, "update", plan.Minutes, plan.Reason, staged.Tag, () => InstallStepAsync(admin, staged));
        }
        if (result.Done) await AuditAsync(admin, "install of " + staged.Tag + " planned at " + Stamp(Now.AddMinutes(plan.Minutes)) + " (installed " + Installed + ")"
            + (plan.Reason.Length > 0 ? ": " + plan.Reason : ""));
        return result;
    }

    /// <summary>
    /// The install's last step, at the end of the countdown: a database backup (kept in updates/backups too), the swap
    /// with the running version kept in updates/previous, the record the next start confirms. Null: stop and restart.
    /// </summary>
    internal async Task<string?> InstallStepAsync(string admin, StagedRecord staged)
    {
        lock (_gate) _installing = true;
        try
        {
            string backup = await Task.Run(() => BackupForUpdate(staged.Version));
            var swap = await Task.Run(() => UpdateInstaller.Swap(staged.Folder, Hosting.InstallDir!, _paths, Installed, backup));
            UpdateInstaller.WriteJson(_paths.Pending, new PendingRecord("install", Installed, Plain(staged.Version), admin, Now));
            UpdatePackage.TryDelete(_paths.Staged);
            DeleteQuietly(_paths.Staging);
            lock (_gate) _staged = null;
            string detail = "Database backup " + backup + "; the files of " + Installed + " are kept." + Kept(swap);
            AppendHistory(new HistoryRecord(Now, "install", Installed, Plain(staged.Version), admin, detail));
            await AuditAsync(admin, "installing " + staged.Tag + ": " + detail + " Restarting.");
            return null;
        }
        catch (Exception e) when (e is UpdateException or IOException or UnauthorizedAccessException or SqliteException or InvalidDataException)
        {
            lock (_gate) _installing = false;
            string message = "The update was not installed: " + e.Message;
            AppendHistory(new HistoryRecord(Now, "failed", Installed, Plain(staged.Version), admin, message));
            await AuditAsync(admin, "install of " + staged.Tag + " failed: " + e.Message);
            return message;
        }
    }

    /// <summary>"1.0.24" of "1.0.24+&lt;commit&gt;" (a version that does not parse stays as it is).</summary>
    private static string Plain(string version) => ReleaseVersion.TryParse(version, out var v) ? v.ToString() : version;

    private static string Kept(SwapResult swap) => swap.Kept.Count == 0 ? ""
        : " Your edited " + string.Join(", ", swap.Kept) + " stayed; the release's own is beside it as .new.";

    /// <summary>A backup now (DatabaseBackup's checked copy) and a copy of it in updates/backups, which the rotation keeps.</summary>
    private string BackupForUpdate(string version)
    {
        string made = _backups.Backup();
        Directory.CreateDirectory(_paths.Backups);
        string name = "ro3emu-before-" + Plain(version) + "-" + Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".db";
        File.Copy(made, Path.Combine(_paths.Backups, name), overwrite: true);
        foreach (var old in new DirectoryInfo(_paths.Backups).EnumerateFiles("*.db").OrderByDescending(f => f.LastWriteTimeUtc).Skip(UpdateBackupsKept))
            UpdatePackage.TryDelete(old.FullName);
        return name;
    }

    // ---------------------------------------------------------------- rollback

    public async Task<PanelActionResult> RollbackAsync(string admin, PanelUpdatePlan plan)
    {
        if (!Hosting.CanInstall) return Refused(HostingSummary());
        if (UpdateInstaller.ReadPrevious(_paths) is not { } previous) return Refused("No previous version is kept.");
        string? backup = previous.Backup is { } name ? Path.Combine(_paths.Backups, name) : null;
        if (plan.RestoreDatabase && (backup == null || !File.Exists(backup))) return Refused("There is no database backup from before the update.");
        PanelActionResult result;
        lock (_gate)
        {
            if (Busy() is { } busy) return Refused(busy);
            result = _ops.ScheduleStep(admin, "rollback", plan.Minutes, plan.Reason, previous.Version,
                () => RollbackStepAsync(admin, previous.Version, plan.RestoreDatabase ? backup : null));
        }
        if (result.Done) await AuditAsync(admin, "rollback to " + previous.Version + " planned at " + Stamp(Now.AddMinutes(plan.Minutes))
            + (plan.RestoreDatabase ? ", restoring " + previous.Backup : ", keeping the database") + (plan.Reason.Length > 0 ? ": " + plan.Reason : ""));
        return result;
    }

    /// <summary>
    /// The rollback's last step: a database backup of now (the newer version's data stays reachable), the previous
    /// files swapped back, and, when asked, the pre-update backup restored by the next start (DatabaseRestore).
    /// </summary>
    internal async Task<string?> RollbackStepAsync(string admin, string version, string? restore)
    {
        lock (_gate) _installing = true;
        try
        {
            string made = await Task.Run(_backups.Backup);
            var swap = await Task.Run(() => UpdateInstaller.Restore(Hosting.InstallDir!, _paths, Installed));
            if (restore != null) DatabaseRestore.Request(_options.DataDir, restore);
            UpdateInstaller.WriteJson(_paths.Pending, new PendingRecord("rollback", Installed, version, admin, Now));
            string detail = "Database backup " + Path.GetFileName(made) + "; the files of " + Installed + " are kept."
                + (restore != null ? " The next start restores " + Path.GetFileName(restore) + "." : " The database stays as it is.") + Kept(swap);
            AppendHistory(new HistoryRecord(Now, "rollback", Installed, version, admin, detail));
            await AuditAsync(admin, "rolling back to " + version + ": " + detail + " Restarting.");
            return null;
        }
        catch (Exception e) when (e is UpdateException or IOException or UnauthorizedAccessException or SqliteException or InvalidDataException)
        {
            lock (_gate) _installing = false;
            string message = "The rollback did not happen: " + e.Message;
            AppendHistory(new HistoryRecord(Now, "failed", Installed, version, admin, message));
            await AuditAsync(admin, "rollback to " + version + " failed: " + e.Message);
            return message;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Updates: the download of {Tag} failed")]
    private static partial void LogDownloadFailed(ILogger logger, Exception error, string tag);
}
