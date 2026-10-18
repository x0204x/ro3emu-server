using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ro3Emu.Setup;

/// <summary>
/// RO3Emu-diagnostics.zip for a bug report: the setup log, the newest server logs, the tail of the game's Player.log,
/// the publisher's installed-release.txt, sizes and SHA-256 of the client's managed DLLs, the export stamp and OS
/// information. No game files and no database.
/// </summary>
internal static class DiagnosticsZip
{
    public static string Write(string? clientDir)
    {
        var path = Path.Combine(SetupHome.Root, "RO3Emu-diagnostics.zip");
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        void Text(string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        void Tail(string name, string file, int maxBytes)
        {
            try
            {
                if (!File.Exists(file)) return;
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > maxBytes) stream.Seek(-maxBytes, SeekOrigin.End);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                Text(name, Redact(reader.ReadToEnd()));
            }
            catch (Exception e) { Text(name + ".error.txt", e.Message); }
        }

        Text("system.txt", SystemInfo(clientDir));
        Tail("setup.log", SetupLog.Path, 2 << 20);
        Tail("setup.log.old", SetupLog.Path + ".old", 512 << 10);
        if (Directory.Exists(SetupHome.LogDir))
            foreach (var log in new DirectoryInfo(SetupHome.LogDir).GetFiles("server-*.log").OrderByDescending(f => f.LastWriteTimeUtc).Take(3))
                Tail("server/" + log.Name, log.FullName, 2 << 20);
        var dataLogs = Path.Combine(SetupHome.DataDir, "logs");
        if (Directory.Exists(dataLogs))
            foreach (var log in new DirectoryInfo(dataLogs).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).Take(2))
                Tail("server-data-logs/" + log.Name, log.FullName, 1 << 20);
        Tail("export.json", Path.Combine(SetupHome.DataDir, "export.json"), 64 << 10);
        var playerLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "gravity", "ro3", "Player.log");
        Tail("game/Player.log", playerLog, 1 << 20);
        Tail("game/Player-prev.log", Path.Combine(Path.GetDirectoryName(playerLog)!, "Player-prev.log"), 256 << 10);
        if (clientDir != null)
        {
            Tail("game/installed-release.txt", Path.Combine(clientDir, ".ro3-update", "installed-release.txt"), 64 << 10);
            Text("game/managed-dlls.txt", ManagedListing(clientDir));
        }
        return path;
    }

    /// <summary>Login secrets (a live SDK ticket, engine sign or gate auth) that an older server or the game may have
    /// logged: a shared zip must not let anyone log in as the player.</summary>
    private static readonly Regex Secrets = new(
        """(?<key>\b(?:Ticket|Token|Sign|Auth|OldAuth|NewAuth|Password|Credential|LoginToken|MixedLoginToken|AccessToken|RefreshToken)\b"?\s*[=:]\s*"?)[^\s",}\]]{6,}""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string Redact(string text) => Secrets.Replace(text, "${key}<redacted>");

    private static string SystemInfo(string? clientDir)
    {
        var s = new StringBuilder();
        s.AppendLine($"RO3Emu {typeof(DiagnosticsZip).Assembly.GetName().Version}");
        s.AppendLine($"time {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        s.AppendLine($"os {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), runtime {RuntimeInformation.FrameworkDescription}");
        s.AppendLine($"cpus {Environment.ProcessorCount}, memory {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes >> 20} MB");
        s.AppendLine($"app {SetupHome.AppDir}");
        s.AppendLine($"home {SetupHome.Root}");
        s.AppendLine($"client {clientDir ?? "(none)"}");
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            s.AppendLine($"drive {drive.Name} {drive.DriveFormat} free {drive.AvailableFreeSpace >> 20} MB of {drive.TotalSize >> 20} MB");
        return s.ToString();
    }

    private static string ManagedListing(string clientDir)
    {
        var s = new StringBuilder();
        foreach (var dir in new[] { Path.Combine(clientDir, "ro3_Data", "Managed"), Path.Combine(clientDir, "ro3emu-backup") })
        {
            if (!Directory.Exists(dir)) continue;
            s.AppendLine("# " + dir);
            foreach (var file in Directory.GetFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var stream = File.OpenRead(file);
                    s.AppendLine($"{Path.GetFileName(file)}\t{stream.Length}\t{Convert.ToHexStringLower(SHA256.HashData(stream))}");
                }
                catch (Exception e) { s.AppendLine($"{Path.GetFileName(file)}\t{e.Message}"); }
            }
        }
        return s.ToString();
    }
}
