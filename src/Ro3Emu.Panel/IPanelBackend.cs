using System.Net;

namespace Ro3Emu.Panel;

/// <summary>
/// What the panel asks of the server. The server implements it over its own stores, writer and game loop, so every
/// change the panel makes goes through the running server: a ban kicks the player at once, and nothing is written
/// into the database behind the server's back. The panel itself holds no game state.
/// </summary>
public partial interface IPanelBackend
{
    /// <summary>The server's name and version, and when it started.</summary>
    PanelServerInfo Info { get; }

    /// <summary>False while the server stops: the panel refuses every change then, as the server's own HTTP does.</summary>
    bool Accepting { get; }

    /// <summary>The folder the /img/ pictures are served from (PanelOptions.ImageDir), or null when there is none.</summary>
    string? ImageDir { get; }

    /// <summary>The branding's logo (PanelBranding.LogoPath) as a full path inside the data folder, or null when none is
    /// configured or the path leaves the data folder.</summary>
    string? BrandLogoFile { get; }

    /// <summary>The server's credential budget per address (the SDK login's): false = answer 429 without hashing.</summary>
    bool TryAcquireCredentialCheck(IPAddress? remote);

    /// <summary>The account's stored name when the password matches and the account is not banned, else null.</summary>
    Task<string?> VerifyAsync(string account, string password);

    /// <summary>Creates the account the game's first login would create for this name and password.</summary>
    Task<PanelRegistration> RegisterAsync(string account, string password);

    /// <summary>Sets a new password once the current one matched; false when it did not.</summary>
    Task<bool> ChangePasswordAsync(string account, string current, string replacement);

    /// <summary>True while the account (its stored name) has an active ban.</summary>
    Task<bool> IsBannedAsync(string account);

    /// <summary>The account may use the admin area (listed in Ro3Emu:GmAccounts).</summary>
    bool IsAdmin(string account);

    /// <summary>Players in the world now; read from the server's census, no work for the game loop.</summary>
    int PlayersOnline();

    /// <summary>How many accounts and characters the realm has (cached; the public home page shows them).</summary>
    Task<PanelCounts> CountsAsync();

    Task<IReadOnlyList<PanelNewsItem>> NewsAsync(int limit);

    /// <summary>Characters in the world, with their account (the panel shows it to admins only).</summary>
    Task<IReadOnlyList<PanelOnlineRole>> OnlineAsync();

    IReadOnlyList<PanelBoardInfo> Boards();

    /// <summary>The root classes a class board can be narrowed to.</summary>
    IReadOnlyList<PanelClass> Classes();

    /// <summary>A board's top rows; <paramref name="rootClass"/> 0 = every class. Null when there is no such board.</summary>
    Task<PanelBoard?> BoardAsync(string board, long rootClass);

    /// <summary>One account and its characters, or null when it does not exist.</summary>
    Task<PanelAccount?> AccountAsync(string account);

    Task<PanelDashboard> DashboardAsync();

    /// <summary>Accounts whose name contains <paramref name="query"/> (any letter case), by name.</summary>
    Task<PanelAccountPage> FindAccountsAsync(string query, int page, int pageSize);

    /// <summary>Bans the account (null <paramref name="until"/> = no end), records it in the audit log in the same
    /// transaction and kicks its game session at once.</summary>
    Task<PanelBanOutcome> BanAsync(string admin, string account, string reason, DateTimeOffset? until);

    /// <summary>Lifts the account's ban and records it; false when it had none.</summary>
    Task<bool> UnbanAsync(string admin, string account, string reason);

    Task<PanelAuditPage> AuditAsync(int page, int pageSize);
}

public sealed record PanelServerInfo(string Name, string Area, string Version, DateTimeOffset StartedUtc);

public enum PanelRegistration { Created, Taken, Banned, Invalid }

public sealed record PanelCounts(int Accounts, int Characters);

public sealed record PanelNewsItem(long Id, string Title, string Body, string Tag, DateTimeOffset Published, string Author,
    bool Pinned = false);

/// <param name="ClassIcon">The class emblem's /img/ address; empty when the server has no pictures.</param>
public sealed record PanelOnlineRole(string Name, string Class, int Level, string Map, string Account, string ClassIcon = "", long MapId = 0);

/// <param name="Id">The board's key in the URL.</param>
/// <param name="Score">What the score column counts.</param>
/// <param name="ByClass">The board can be narrowed to one root class.</param>
public sealed record PanelBoardInfo(string Id, string Title, string Score, bool Guild, bool ByClass);

public sealed record PanelClass(long Id, string Name, string Icon = "");

public sealed record PanelBoard(PanelBoardInfo Info, IReadOnlyList<PanelBoardRow> Rows);

/// <param name="Level">A character's base level, a guild's level.</param>
public sealed record PanelBoardRow(int Rank, string Name, string Class, int Level, long Score, string ClassIcon = "");

public sealed record PanelCharacter(long Id, string Name, string Class, int Level, int JobLevel, string Map, string Guild,
    DateTimeOffset Created, DateTimeOffset? LastLogin, bool Online, string ClassIcon = "");

public sealed record PanelBan(string Reason, DateTimeOffset Banned, DateTimeOffset? Until, string? By);

/// <param name="LastLogin">The latest character login of the account, when it has one.</param>
public sealed record PanelAccount(string Name, DateTimeOffset? Created, DateTimeOffset? LastLogin,
    IReadOnlyList<PanelCharacter> Characters, PanelBan? Ban);

public sealed record PanelAccountSummary(string Name, DateTimeOffset? Created, int Characters, DateTimeOffset? LastLogin, bool Banned);

public sealed record PanelAccountPage(IReadOnlyList<PanelAccountSummary> Accounts, int Total, int Page, int PageSize);

/// <param name="Kicked">Game sessions of the account that were closed.</param>
public sealed record PanelBanOutcome(bool Found, int Kicked);

public sealed record PanelAuditEntry(long Id, DateTimeOffset Utc, string Admin, string Action, string Target, string Detail);

public sealed record PanelAuditPage(IReadOnlyList<PanelAuditEntry> Entries, int Total, int Page, int PageSize);

/// <summary>The server's own metrics (/ro3emu/metrics), the few an operator watches.</summary>
public sealed record PanelDashboard(
    string Phase, int InWorld, int Loading, int Login, int Connections, int Accounts, int Characters,
    double TicksPerSecond, double TickP50Ms, double TickP99Ms, double TickMaxMs, long Overruns, int QueueLength,
    long WorkingSetBytes, double CpuSeconds, int Gen2Collections, long Exceptions, long ErrorLogs, long DbCommits,
    IReadOnlyList<PanelMapCount> Maps);

public sealed record PanelMapCount(string Map, int Players);
