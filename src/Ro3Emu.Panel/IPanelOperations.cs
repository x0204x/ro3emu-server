namespace Ro3Emu.Panel;

/// <summary>
/// What the admin area's staff roles, account security, report inbox, character actions and server control ask of
/// the server (docs/guide/panel.md). Like <see cref="IPanelBackend"/>, every change goes through the running server and
/// every staff action is written to the audit log in the transaction that makes it.
/// </summary>
public interface IPanelOperations
{
    // ---------------------------------------------------------------- staff roles

    /// <summary>The account's staff role: Ro3Emu:Panel:Roles, Ro3Emu:GmAccounts (Admin) or one an Admin gave on the panel.</summary>
    PanelRole RoleOf(string account);

    /// <summary>
    /// The same from the lists alone: without the rule that an empty GM list on a local server makes every account an
    /// Admin. A request that a proxy forwarded gets this one.
    /// </summary>
    PanelRole ListedRoleOf(string account);

    /// <summary>Every account with a staff role, and where the role comes from.</summary>
    Task<IReadOnlyList<PanelStaff>> StaffAsync();

    /// <summary>Gives <paramref name="account"/> a role (None removes it); refused for unknown accounts and configured roles.</summary>
    Task<PanelRoleChange> SetRoleAsync(string admin, string account, PanelRole role);

    // ---------------------------------------------------------------- account security

    /// <summary>Records a successful login (<paramref name="source"/> "panel" or "game"); the address only when the
    /// operator keeps addresses (PanelOptions.LoginAddresses).</summary>
    Task RecordLoginAsync(string account, string source, System.Net.IPAddress? address);

    /// <summary>The account's latest logins, newest first.</summary>
    Task<IReadOnlyList<PanelLogin>> LoginsAsync(string account, int limit);

    Task<PanelTwoFactor> TwoFactorAsync(string account);

    /// <summary>A new secret waiting for its first code; replaces an earlier unconfirmed one. Null when 2FA is already on.</summary>
    Task<byte[]?> BeginTwoFactorAsync(string account);

    /// <summary>Turns 2FA on once <paramref name="code"/> matches the waiting secret: the recovery codes (shown once), or null.</summary>
    Task<IReadOnlyList<string>?> EnableTwoFactorAsync(string account, string code);

    /// <summary>True when <paramref name="code"/> is a current one-time code or an unused recovery code (used up by this
    /// call). A code is good once.</summary>
    Task<bool> CheckTwoFactorAsync(string account, string code);

    /// <summary>Turns 2FA off (the caller checked a code, or an Admin resets it); <paramref name="admin"/> is recorded.</summary>
    Task<bool> DisableTwoFactorAsync(string account, string? admin);

    /// <summary>New recovery codes replacing the old ones; null when 2FA is off.</summary>
    Task<IReadOnlyList<string>?> NewRecoveryCodesAsync(string account);

    /// <summary>Records that <paramref name="admin"/> signed <paramref name="account"/> out of every panel browser
    /// (the sessions live in the panel; the server only keeps the audit row).</summary>
    Task RecordPanelLogoutAsync(string admin, string account, int ended);

    // ---------------------------------------------------------------- reports

    /// <summary>In-game reports (FeedbackService), newest first, with their triage; <paramref name="status"/> empty = all.</summary>
    Task<PanelReportPage> ReportsAsync(string status, int page, int pageSize);

    Task<PanelReport?> ReportAsync(long id);

    /// <summary>Changes a report's status or assignee and adds a note; false when there is no such report.</summary>
    Task<bool> UpdateReportAsync(string admin, long id, PanelReportUpdate update);

    // ---------------------------------------------------------------- character actions

    /// <summary>Runs one GM action on a character through the server's own paths; the result says what happened.</summary>
    Task<PanelActionResult> CharacterActionAsync(string admin, long character, PanelCharacterAction action);

    /// <summary>The account's deleted characters (the server keeps their rows), newest deletion first.</summary>
    Task<IReadOnlyList<PanelDeletedCharacter>> DeletedCharactersAsync(string account);

    // ---------------------------------------------------------------- server control

    Task<PanelServerState> ServerStateAsync();

    /// <summary>Plans a restart, shutdown or restore with in-game countdown notices; replaces an earlier plan.</summary>
    Task<PanelActionResult> ScheduleAsync(string admin, PanelSchedule schedule);

    Task<bool> CancelScheduleAsync(string admin);

    /// <summary>Maintenance on: game logins of accounts without a staff role are refused.</summary>
    Task SetMaintenanceAsync(string admin, bool enabled, string note);

    /// <summary>A database backup now (the server's own DatabaseBackup); its file name.</summary>
    Task<string> BackupNowAsync(string admin);

    /// <summary>The full path of a backup file in data/backups, recording the download; null when there is none.</summary>
    Task<string?> BackupFileAsync(string admin, string name);
}

/// <summary>Staff roles, each with everything of the ones before it.</summary>
public enum PanelRole { None, Moderator, Gm, Admin }

public static class PanelRoles
{
    /// <summary>"moderator", "gm", "admin" (or "" for none): the role's name in the API and in configuration.</summary>
    public static string Key(PanelRole role) => role switch
    {
        PanelRole.Moderator => "moderator",
        PanelRole.Gm => "gm",
        PanelRole.Admin => "admin",
        _ => "",
    };

    /// <summary>A role by its name in any letter case ("GM", "Admin"); "none" and "" are <see cref="PanelRole.None"/>.</summary>
    public static PanelRole? Parse(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "none" or "player" => PanelRole.None,
        "moderator" or "mod" => PanelRole.Moderator,
        "gm" => PanelRole.Gm,
        "admin" => PanelRole.Admin,
        _ => null,
    };
}

/// <param name="Source">config (Ro3Emu:Panel:Roles), gm-accounts (Ro3Emu:GmAccounts) or panel (given by an Admin).</param>
public sealed record PanelStaff(string Account, PanelRole Role, string Source, string? GrantedBy, DateTimeOffset? Granted, bool TwoFactor);

public enum PanelRoleChange { Changed, NoSuchAccount, Configured, Unchanged }

/// <param name="Source">panel or game.</param>
/// <param name="Address">The client's address; null when the operator keeps none.</param>
public sealed record PanelLogin(DateTimeOffset Utc, string Source, string? Address);

/// <param name="Pending">A secret waits for its first code.</param>
public sealed record PanelTwoFactor(bool Enabled, bool Pending, int RecoveryLeft);

public sealed record PanelReportSummary(long Id, DateTimeOffset Time, string Source, string? Account, string? Character, int Level,
    string? Map, string Text, string Status, string? Assignee, int Notes);

public sealed record PanelReportPage(IReadOnlyList<PanelReportSummary> Reports, int Total, int Page, int PageSize,
    IReadOnlyDictionary<string, int> Counts);

public sealed record PanelReportNote(DateTimeOffset Utc, string Author, string Text);

/// <param name="Context">The server's log lines about the player from the minutes before the report.</param>
public sealed record PanelReport(long Id, DateTimeOffset Time, string Source, string? Account, long CharacterId, string? Character,
    int Level, int Career, long Scene, string? Map, long Instance, int Line, double X, double Y, double Z, string Text, bool Truncated,
    IReadOnlyList<string> Context, string Status, string? Assignee, DateTimeOffset? Updated, string? UpdatedBy,
    IReadOnlyList<PanelReportNote> Notes);

/// <param name="Status">new, in-progress, done or wont-fix; null keeps it.</param>
/// <param name="Assignee">An account; "" clears it, null keeps it.</param>
public sealed record PanelReportUpdate(string? Status, string? Assignee, string? Note);

/// <param name="Kind">kick, teleport, rename, item (Count &lt; 0 removes) or restore.</param>
public sealed record PanelCharacterAction(string Kind, string? Reason = null, long Map = 0, float? X = null, float? Z = null,
    string? Name = null, int Item = 0, long Count = 0);

public sealed record PanelActionResult(bool Done, string Message);

public sealed record PanelDeletedCharacter(long Id, string Name, string Class, int Level, DateTimeOffset Deleted, bool NameFree);

/// <param name="Kind">restart, shutdown or restore.</param>
/// <param name="Backup">The backup file a restore puts back.</param>
public sealed record PanelSchedule(string Kind, int Minutes, string Reason, string? Backup = null);

/// <param name="Kind">restart, shutdown, restore, or update and rollback (planned by <see cref="IPanelUpdates"/>).</param>
/// <param name="Version">The version an update or rollback starts.</param>
public sealed record PanelScheduled(string Kind, DateTimeOffset At, string Reason, string By, string? Backup, string? Version = null);

public sealed record PanelMaintenance(bool On, string Note, string? By, DateTimeOffset? Since);

public sealed record PanelBackupFile(string Name, long Bytes, DateTimeOffset Created, bool Compressed);

/// <param name="LastRestore">What the last restore at a start did (the server's own words), or null.</param>
public sealed record PanelServerState(PanelMaintenance Maintenance, PanelScheduled? Scheduled, IReadOnlyList<PanelBackupFile> Backups,
    string? LastRestore);
