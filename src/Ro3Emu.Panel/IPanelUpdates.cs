namespace Ro3Emu.Panel;

/// <summary>
/// The update manager of Server control (Admins, docs/guide/panel.md): the installed version against the releases on
/// GitHub, a checked download staged next to the data, and an install or rollback that runs through the planned
/// restart. Every action is audited by the server.
/// </summary>
public interface IPanelUpdates
{
    Task<PanelUpdateState> StateAsync();

    /// <summary>Asks GitHub now, unless it was asked a moment ago or its rate limit is spent.</summary>
    Task<PanelUpdateState> CheckAsync(string admin);

    /// <summary>Starts the download of a release newer than the installed one; the state shows its progress.</summary>
    Task<PanelActionResult> DownloadAsync(string admin, string tag);

    /// <summary>Stops a download that runs.</summary>
    Task<PanelActionResult> CancelDownloadAsync(string admin);

    /// <summary>Deletes the staged release (or a failed download's leftovers).</summary>
    Task<PanelActionResult> DiscardAsync(string admin);

    /// <summary>Plans the install of the staged release: a restart whose last step backs up the database and swaps the files.</summary>
    Task<PanelActionResult> InstallAsync(string admin, PanelUpdatePlan plan);

    /// <summary>Plans the return to the previous version's files, with the database backup made before the update if asked.</summary>
    Task<PanelActionResult> RollbackAsync(string admin, PanelUpdatePlan plan);
}

/// <param name="RestoreDatabase">A rollback also restores the database backup made just before the update.</param>
public sealed record PanelUpdatePlan(int Minutes, string Reason, bool RestoreDatabase = false);

/// <param name="Commit">The source revision the build names (short), or null.</param>
/// <param name="Platform">linux-x64, win-x64, ...</param>
public sealed record PanelUpdateInstalled(string Version, string? Commit, string Platform);

/// <param name="Mode">systemd, service, docker, app (RO3Emu.exe or the launcher), manual or source.</param>
/// <param name="CanInstall">The server can swap its files and come back by itself.</param>
/// <param name="CanDownload">A release can be downloaded and staged (not for a source build).</param>
/// <param name="Summary">How the server runs, in a sentence.</param>
/// <param name="Steps">What the operator does to install a staged release by hand (empty when the page does it).</param>
public sealed record PanelUpdateHosting(string Mode, bool CanInstall, bool CanDownload, string Summary, string? InstallDir,
    IReadOnlyList<string> Steps);

/// <param name="Enabled">Ro3Emu:Updates:Check.</param>
/// <param name="Error">Why the last check failed (the releases shown are then the last good answer), or null.</param>
public sealed record PanelUpdateCheck(bool Enabled, string Repository, DateTimeOffset? Checked, DateTimeOffset? Next, string? Error);

/// <param name="Notes">The release notes as GitHub keeps them (Markdown), shown as plain text.</param>
/// <param name="Asset">The package for this server's platform, or null when the release has none.</param>
/// <param name="Digest">GitHub lists the package's SHA-256 (a download without one is refused).</param>
public sealed record PanelRelease(string Tag, string Version, string Name, DateTimeOffset Published, string Notes, string Url,
    string? Asset, long Bytes, bool Digest);

/// <param name="Phase">idle, downloading, verifying, extracting, staged, failed, planned or installing.</param>
public sealed record PanelUpdateJob(string Phase, string? Tag, long Done, long Total, string? Message);

public sealed record PanelStagedUpdate(string Tag, string Version, string Folder, long Bytes, string Sha256, DateTimeOffset Staged);

/// <param name="Backup">The database backup made just before the update to the running version, or null.</param>
public sealed record PanelPreviousVersion(string Version, DateTimeOffset Saved, string? Backup, long BackupBytes);

/// <param name="Kind">install, rollback or failed.</param>
public sealed record PanelUpdateHistory(DateTimeOffset Utc, string Kind, string From, string To, string By, string Detail);

public sealed record PanelUpdateState(PanelUpdateInstalled Installed, PanelUpdateHosting Hosting, PanelUpdateCheck Check,
    IReadOnlyList<PanelRelease> Newer, PanelRelease? Latest, PanelUpdateJob Job, PanelStagedUpdate? Staged,
    PanelPreviousVersion? Previous, IReadOnlyList<PanelUpdateHistory> History);
