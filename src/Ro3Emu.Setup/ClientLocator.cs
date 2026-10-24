using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32;
using Ro3Emu.ClientTools;
using Ro3Emu.DataExport;

namespace Ro3Emu.Setup;

/// <summary>Finds the player's Ragnarok 3 PC client folder (the one with ro3.exe) and checks it.</summary>
internal static class ClientLocator
{
    public static bool IsClient([NotNullWhen(true)] string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "ro3.exe")) && Directory.Exists(Path.Combine(dir, "ro3_Data", "Managed"));

    /// <summary>
    /// Folder candidates, best first: the saved choice, RO3_CLIENT_DIR, the folder next to RO3Emu.exe, installed
    /// programs that look like the game, and the usual places on every fixed drive.
    /// </summary>
    public static List<string> Candidates(Settings settings)
    {
        var found = new List<string>();
        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                dir = Path.GetFullPath(dir.Trim().Trim('"'));
                // The game folder or its parent (a "Genesis" folder holding ROO_PC and the launcher).
                foreach (var d in new[] { dir, Path.Combine(dir, "ROO_PC") })
                    if (IsClient(d) && !found.Contains(d, StringComparer.OrdinalIgnoreCase)) found.Add(d);
            }
            catch (Exception) { }
        }

        Add(settings.ClientDir);
        Add(Environment.GetEnvironmentVariable("RO3_CLIENT_DIR"));
        Add(SetupHome.AppDir);
        Add(Path.GetDirectoryName(SetupHome.AppDir));
        if (OperatingSystem.IsWindows())
        {
            foreach (var dir in UninstallLocations()) Add(dir);
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                var root = drive.RootDirectory.FullName;
                foreach (var pattern in new[] { "RO3", "Genesis", "Games", "Program Files", "Program Files (x86)", "" })
                {
                    var baseDir = Path.Combine(root, pattern);
                    foreach (var sub in Children(baseDir))
                    {
                        if (pattern.Length == 0 && !LooksLikeGame(sub)) continue;
                        Add(sub);
                        foreach (var sub2 in Children(sub).Where(LooksLikeGame)) Add(sub2);
                    }
                    if (pattern.Length > 0) Add(baseDir);
                }
            }
        }
        return found;
    }

    private static bool LooksLikeGame(string dir)
    {
        var name = Path.GetFileName(dir);
        return name.Contains("RO3", StringComparison.OrdinalIgnoreCase) || name.Contains("ROO", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Ragnarok", StringComparison.OrdinalIgnoreCase) || name.Contains("Genesis", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Children(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
        catch (Exception) { return []; }
    }

    private static IEnumerable<string> UninstallLocations()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        var roots = new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };
        foreach (var (hive, path) in roots)
        {
            RegistryKey? key = null;
            try { key = hive.OpenSubKey(path); } catch (Exception) { }
            if (key == null) continue;
            using (key)
            {
                foreach (var name in key.GetSubKeyNames())
                {
                    string? location = null;
                    try
                    {
                        using var app = key.OpenSubKey(name);
                        var display = app?.GetValue("DisplayName") as string ?? "";
                        if (display.Contains("Ragnarok", StringComparison.OrdinalIgnoreCase) || display.Contains("RO3", StringComparison.OrdinalIgnoreCase)
                            || display.Contains("Genesis", StringComparison.OrdinalIgnoreCase))
                            location = app?.GetValue("InstallLocation") as string;
                    }
                    catch (Exception) { }
                    if (!string.IsNullOrWhiteSpace(location)) yield return location;
                }
            }
        }
    }

    /// <summary>
    /// The game's release: the newer of the one recorded by the publisher's launcher (.ro3-update/installed-release.txt)
    /// and the one RO3Emu's own install/update recorded last (settings), or null. RO3Emu does not write the launcher's
    /// file, so after <c>RO3Emu.exe update</c> only its own record is current.
    /// </summary>
    public static string? InstalledRelease(string clientDir)
    {
        long? launcher = null;
        var path = Path.Combine(clientDir, ".ro3-update", "installed-release.txt");
        try
        {
            if (File.Exists(path))
            {
                var fields = File.ReadAllLines(path).Select(l => l.Split('\t')).Where(c => c.Length == 2).ToDictionary(c => c[0], c => c[1]);
                if (fields.TryGetValue("release_sequence", out var seq)
                    && long.TryParse(seq, NumberStyles.None, CultureInfo.InvariantCulture, out long n)) launcher = n;
            }
        }
        catch (Exception) { }
        long? own = null;
        try
        {
            string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(clientDir));
            foreach (var (key, value) in Settings.Load().InstalledSequence)
                if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(key)), folder, StringComparison.OrdinalIgnoreCase))
                    own = Math.Max(own ?? 0, value);
        }
        catch (Exception) { }
        long? release = own == null || launcher > own ? launcher : own;
        return release is { } r ? $"release {r.ToString(CultureInfo.InvariantCulture)}" : null;
    }

    /// <summary>What is wrong with the folder for playing (empty = fine). <paramref name="needExport"/>: also the game data.</summary>
    public static List<string> Problems(string clientDir, bool needExport, string? hooksDll, string? locDll)
    {
        var problems = new List<string>();
        if (!File.Exists(Path.Combine(clientDir, "ro3.exe"))) problems.Add("ro3.exe is missing");
        var managed = Path.Combine(clientDir, "ro3_Data", "Managed");
        foreach (var dll in new[] { "Assembly-CSharp.dll", "Happyengine.Network.dll", "Phonest.Common.dll", "Unity.TextMeshPro.dll", "UnityEngine.UI.dll", "HUD.dll" })
            if (!File.Exists(Path.Combine(managed, dll))) problems.Add($"ro3_Data\\Managed\\{dll} is missing");
        if (needExport) problems.AddRange(GameDataExport.Missing(clientDir).Select(m => m + " is missing"));
        if (problems.Count == 0) problems.AddRange(ClientPatcher.Check(clientDir, hooksDll, locDll));
        return problems;
    }
}
