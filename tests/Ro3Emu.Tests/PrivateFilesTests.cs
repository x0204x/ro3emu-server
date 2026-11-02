using Microsoft.Extensions.Logging.Abstractions;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Tests;

/// <summary>The data dir and the gate's private key are the server user's alone: 0700 and 0600 on Linux, an own access list on Windows.</summary>
public sealed class PrivateFilesTests : IDisposable
{
    private const UnixFileMode Others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-private-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void TheDataDirAndTheGateKeyLoseTheirGroupAndOtherBits()
    {
        if (!OperatingSystem.IsWindows())
        {
            // An older install: a world-readable data dir and key, as a default umask leaves them.
            Directory.CreateDirectory(_dir, (UnixFileMode)0b111_101_101);
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            File.WriteAllText(Path.Combine(_dir, "gate_rsa_private.pem"), rsa.ExportRSAPrivateKeyPem());
            File.SetUnixFileMode(Path.Combine(_dir, "gate_rsa_private.pem"), (UnixFileMode)0b110_100_100);
        }
        GateCrypto.LoadOrCreate(_dir, NullLogger.Instance);
        PrivateFiles.Files(Path.Combine(_dir, "missing.db"));

        Assert.True(File.Exists(Path.Combine(_dir, "gate_rsa_private.pem")));
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(_dir) & Others);
        Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(Path.Combine(_dir, "gate_rsa_private.pem")) & Others);
        Assert.True(File.GetUnixFileMode(Path.Combine(_dir, "gate_rsa_private.pem")).HasFlag(UnixFileMode.UserRead));
        // The public key is meant to be shared and keeps its mode.
        Assert.True(File.Exists(Path.Combine(_dir, "gate_rsa_public.pem")));
    }

    /// <summary>A server running as SYSTEM names it once, so the list it compares with at start matches what was written.</summary>
    [Fact]
    public void OnWindowsSystemIsAllowedOnceWhenItIsTheUser()
    {
        if (!OperatingSystem.IsWindows()) return;
        var system = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
        Assert.Equal(2, PrivateFiles.AllowedOnWindows(system).Length);
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        Assert.Equal(me.User == system ? 2 : 3, PrivateFiles.AllowedOnWindows(me.User!).Length);
    }

    /// <summary>Nothing inherited from the folder above, only this user, SYSTEM and the Administrators, also for a file made later.</summary>
    [Fact]
    public void OnWindowsTheDataDirHasItsOwnAccessList()
    {
        if (!OperatingSystem.IsWindows()) return;
        PrivateFiles.Directory(_dir);
        string key = Path.Combine(_dir, "secret.txt");
        File.WriteAllText(key, "x");
        PrivateFiles.Files(key);
        var later = Path.Combine(_dir, "later.txt");
        File.WriteAllText(later, "x");

        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        System.Security.Principal.SecurityIdentifier[] allowed = [me.User!,
            new(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
            new(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null)];
        var folder = new DirectoryInfo(_dir).GetAccessControl();
        Assert.True(folder.AreAccessRulesProtected);
        foreach (var security in new System.Security.AccessControl.FileSystemSecurity[] { folder, new FileInfo(key).GetAccessControl(), new FileInfo(later).GetAccessControl() })
        {
            var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
            Assert.NotEmpty(rules);
            foreach (var rule in rules) Assert.Contains((System.Security.Principal.SecurityIdentifier)rule.IdentityReference, allowed);
        }

        // A second start changes nothing and does not fail.
        PrivateFiles.Directory(_dir);
        Assert.Equal("x", File.ReadAllText(later));
    }
}
