using System.Security.AccessControl;
using System.Security.Principal;

namespace Ro3Emu.Server.Accounts;

/// <summary>
/// Files only the server's own user may read: the gate's private key, the operator token, the realm database (with its
/// WAL) and its backups. On Linux and macOS the group and other bits are taken away (0600 for a file, 0700 for the data
/// dir and the backup dir), so another account on a shared machine cannot copy every password hash or impersonate the
/// gate. On Windows the same paths get their own access list: the user, SYSTEM and the Administrators, nothing inherited.
/// A folder off the system drive otherwise inherits access for Authenticated Users. Files created later in such a folder
/// inherit the list. A drive without access lists, or a file another user owns, keeps what it has, with a warning.
/// </summary>
public static class PrivateFiles
{
    private const UnixFileMode Others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>Creates <paramref name="path"/> when missing and keeps it to its owner.</summary>
    public static void Directory(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        Restrict(path);
    }

    /// <summary>Keeps each existing file to its owner; a path that does not exist is skipped.</summary>
    public static void Files(params string[] paths)
    {
        foreach (string path in paths)
            if (File.Exists(path)) Restrict(path);
    }

    private static void Restrict(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RestrictOnWindows(path);
            return;
        }
        try
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & Others) != 0) File.SetUnixFileMode(path, mode & ~Others);
        }
        // A file another user owns (a data dir prepared by root) keeps the mode its owner gave it.
        catch (UnauthorizedAccessException) { }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictOnWindows(string path)
    {
        try
        {
            using var user = WindowsIdentity.GetCurrent();
            var allowed = AllowedOnWindows(user.User!);
            if (System.IO.Directory.Exists(path))
            {
                var folder = new DirectoryInfo(path);
                if (Private(folder.GetAccessControl(), allowed)) return;
                var list = new DirectorySecurity();
                list.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                foreach (var who in allowed)
                    list.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                folder.SetAccessControl(list);
            }
            else
            {
                var file = new FileInfo(path);
                if (Private(file.GetAccessControl(), allowed)) return;
                var list = new FileSecurity();
                list.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                foreach (var who in allowed) list.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.FullControl, AccessControlType.Allow));
                file.SetAccessControl(list);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException or PlatformNotSupportedException or SystemException)
        {
            // Too early for the logger (the data dir comes first), and this must reach the operator whatever logs.
            Console.Error.WriteLine($"warning: could not keep {path} private to this user: {e.Message}");
        }
    }

    /// <summary>
    /// The user, SYSTEM and the Administrators, each once: a server running as SYSTEM would otherwise count one rule too
    /// many and write the list again at every start.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static SecurityIdentifier[] AllowedOnWindows(SecurityIdentifier user) =>
    [
        .. new[]
        {
            user,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        }.Distinct(),
    ];

    /// <summary>Already protected, with only the allowed accounts: nothing to write, so a start does not touch the tree again.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool Private(FileSystemSecurity current, SecurityIdentifier[] allowed)
    {
        if (!current.AreAccessRulesProtected) return false;
        var rules = current.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        return rules.Count == allowed.Length
            && rules.Cast<FileSystemAccessRule>().All(r => allowed.Contains((SecurityIdentifier)r.IdentityReference));
    }
}
