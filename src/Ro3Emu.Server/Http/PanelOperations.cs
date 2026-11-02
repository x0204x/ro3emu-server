using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The server's side of the panel's staff roles, account security, report inbox, character actions and server
/// control (IPanelOperations, docs/guide/panel.md). Writes go through the store's writer with their audit row in the
/// same transaction; what touches a live session runs on the game loop through the server's own paths.
/// </summary>
public sealed partial class PanelOperations(IOptions<ServerOptions> options, AccountStore accounts, RoleStore roles, StaffRoles staff,
    MaintenanceMode maintenance, FeedbackService feedback, DatabaseBackup backups, GameData data, WorldData world, GameLoop loop,
    SessionRegistry sessions, ReconnectService reconnect, GmCommands gm, CharacterService characters, Game.Ownership.OwnerRegistry owners,
    IHostApplicationLifetime lifetime, TimeProvider clock, ILogger<PanelOperations> log) : IPanelOperations, IDisposable
{
    /// <summary>Recovery codes per account: ten of ten base32 characters (50 bits each), shown once.</summary>
    private const int RecoveryCount = 10, RecoveryLength = 10;

    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();

    private Task<bool> AuditAsync(string admin, string action, string target, string detail) =>
        accounts.WriteAsync<bool>(new AdminAuditWritePlan(Now, admin, action, target, detail));

    // ---------------------------------------------------------------- staff roles

    public PanelRole RoleOf(string account) => staff.RoleOf(account);

    public PanelRole ListedRoleOf(string account) => staff.ListedRoleOf(account);

    public async Task<IReadOnlyList<PanelStaff>> StaffAsync()
    {
        var withTwoFactor = await accounts.ReadAsync<HashSet<string>>(new TotpEnabledRead());
        return [.. staff.All().Select(s => new PanelStaff(s.Account, s.Role, s.Source, s.Row?.GrantedBy,
            s.Row is { } row ? DateTimeOffset.FromUnixTimeSeconds(row.GrantedUtc) : null, withTwoFactor.Contains(s.Account)))];
    }

    public async Task<PanelRoleChange> SetRoleAsync(string admin, string account, PanelRole role)
    {
        if (staff.Configured(account) != null) return PanelRoleChange.Configured;
        var known = await accounts.ReadAsync<PanelAccountRow?>(new PanelAccountRead(account, Now));
        if (known == null) return PanelRoleChange.NoSuchAccount;
        if (staff.Configured(known.Name) != null) return PanelRoleChange.Configured;
        if (staff.RoleOf(known.Name) == role) return PanelRoleChange.Unchanged;
        return await staff.SetAsync(admin, known.Name, role) == null ? PanelRoleChange.NoSuchAccount : PanelRoleChange.Changed;
    }

    // ---------------------------------------------------------------- login history

    public Task RecordLoginAsync(string account, string source, IPAddress? address) =>
        accounts.WriteAsync<bool>(new AccountLoginWritePlan(account, Now, source,
            options.Value.Panel.LoginAddresses ? address?.ToString() : null));

    public async Task<IReadOnlyList<PanelLogin>> LoginsAsync(string account, int limit) =>
        [.. (await accounts.ReadAsync<List<AccountLoginRow>>(new AccountLoginsRead(account, limit)))
            .Select(r => new PanelLogin(DateTimeOffset.FromUnixTimeSeconds(r.Utc), r.Source, options.Value.Panel.LoginAddresses ? r.Address : null))];

    // ---------------------------------------------------------------- two-factor authentication

    private Task<TotpRow?> TotpAsync(string account) => accounts.ReadAsync<TotpRow?>(new TotpRead(account));

    private Task<bool> TotpWriteAsync(TotpWriteKind kind, string account, byte[]? secret = null, long step = 0, string recovery = "",
        string? admin = null) => accounts.WriteAsync<bool>(new TotpWritePlan(kind, account, Now, secret, step, recovery, admin));

    public async Task<PanelTwoFactor> TwoFactorAsync(string account) =>
        await TotpAsync(account) is { } row ? new PanelTwoFactor(row.Enabled, !row.Enabled, row.Enabled ? row.Recovery.Count : 0)
            : new PanelTwoFactor(false, false, 0);

    public async Task<byte[]?> BeginTwoFactorAsync(string account)
    {
        byte[] secret = Totp.NewSecret();
        return await TotpWriteAsync(TotpWriteKind.Begin, account, secret) ? secret : null;
    }

    public async Task<IReadOnlyList<string>?> EnableTwoFactorAsync(string account, string code)
    {
        if (await TotpAsync(account) is not { Enabled: false } row || Totp.Match(row.Secret, code, clock.GetUtcNow(), 0) is not { } step)
            return null;
        var (codes, hashes) = NewRecovery();
        return await TotpWriteAsync(TotpWriteKind.Enable, account, step: step, recovery: hashes) ? codes : null;
    }

    /// <summary>A current code moves the account's last used step forward (so it cannot be replayed); a recovery code is
    /// removed. Both conditionally, in the writer, so two requests with one code cannot both pass.</summary>
    public async Task<bool> CheckTwoFactorAsync(string account, string code)
    {
        if (await TotpAsync(account) is not { Enabled: true } row) return false;
        if (Totp.Match(row.Secret, code, clock.GetUtcNow(), row.LastStep) is { } step)
            return await TotpWriteAsync(TotpWriteKind.Use, account, step: step);
        string hash = RecoveryHash(code);
        // Constant time against every stored hash, then the conditional removal decides.
        bool known = false;
        foreach (string stored in row.Recovery)
            known |= CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stored), Encoding.ASCII.GetBytes(hash));
        return known && await TotpWriteAsync(TotpWriteKind.UseRecovery, account, recovery: hash);
    }

    public Task<bool> DisableTwoFactorAsync(string account, string? admin) => TotpWriteAsync(TotpWriteKind.Disable, account, admin: admin);

    public async Task<IReadOnlyList<string>?> NewRecoveryCodesAsync(string account)
    {
        var (codes, hashes) = NewRecovery();
        return await TotpWriteAsync(TotpWriteKind.ReplaceRecovery, account, recovery: hashes) ? codes : null;
    }

    public Task RecordPanelLogoutAsync(string admin, string account, int ended) =>
        AuditAsync(admin, AdminAudit.PanelLogout, account, ended == 1 ? "1 panel session ended"
            : ended.ToString(System.Globalization.CultureInfo.InvariantCulture) + " panel sessions ended");

    /// <summary>Fresh recovery codes ("ABCDE-FGHJK") and the comma-joined SHA-256 hashes the database keeps.</summary>
    private static (IReadOnlyList<string> Codes, string Hashes) NewRecovery()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        var codes = Enumerable.Range(0, RecoveryCount)
            .Select(_ => RandomNumberGenerator.GetString(alphabet, RecoveryLength).Insert(RecoveryLength / 2, "-")).ToList();
        return (codes, string.Join(',', codes.Select(RecoveryHash)));
    }

    /// <summary>The stored form of a recovery code: letters and digits only, upper case, SHA-256 in hex.</summary>
    private static string RecoveryHash(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(
            new string([.. code.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit)]))));
}
