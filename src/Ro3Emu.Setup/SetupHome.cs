using System.Text;
using System.Text.Json;

namespace Ro3Emu.Setup;

/// <summary>
/// Where RO3Emu keeps its things: the folder of RO3Emu.exe when it can write there (an extracted zip), else
/// %LOCALAPPDATA%\RO3Emu. Inside: data\ (exported game data, the server's database and keys), logs\, downloads\,
/// RO3Emu.settings.json. Nothing of ours goes into the game folder except the patch.
/// </summary>
internal static class SetupHome
{
    public static string AppDir { get; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string? _root;

    public static string Root
    {
        get => _root ??= ChooseRoot();
        set => _root = Path.GetFullPath(value);
    }

    public static string DataDir => Path.Combine(Root, "data");

    public static string LogDir => Path.Combine(Root, "logs");

    public static string DownloadDir => Path.Combine(Root, "downloads");

    public static string SettingsPath => Path.Combine(Root, "RO3Emu.settings.json");

    private static string ChooseRoot()
    {
        if (Environment.GetEnvironmentVariable("RO3EMU_HOME") is { Length: > 0 } home) return Path.GetFullPath(home);
        if (CanWrite(AppDir)) return AppDir;
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RO3Emu");
        Directory.CreateDirectory(local);
        return local;
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".ro3emu-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>A file shipped next to RO3Emu.exe (release layout), or null.</summary>
    public static string? Shipped(params string[] parts)
    {
        var path = Path.Combine([AppDir, .. parts]);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The repository root when run from a build of the source tree (developers), else null.</summary>
    public static string? RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppDir); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        return null;
    }
}

/// <summary>What RO3Emu remembers between runs.</summary>
internal sealed class Settings
{
    public ClientSyncOptions ClientSync { get; set; } = new();
    public string? ClientDir { get; set; }

    /// <summary>The highest publisher release installed into each folder: an older signed manifest is refused (no downgrade).</summary>
    public Dictionary<string, long> InstalledSequence { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static Settings Load()
    {
        try
        {
            if (File.Exists(SetupHome.SettingsPath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SetupHome.SettingsPath)) ?? new Settings();
        }
        catch (Exception e) { SetupLog.Write("settings: " + e.Message); }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SetupHome.Root);
        File.WriteAllText(SetupHome.SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }
}

/// <summary>logs\setup.log: every step, answer and full error (the console only shows the plain message).</summary>
internal static class SetupLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string Path => _path ??= Init();

    private static string Init()
    {
        Directory.CreateDirectory(SetupHome.LogDir);
        var path = System.IO.Path.Combine(SetupHome.LogDir, "setup.log");
        // Keep it small: the previous runs' tail is enough for a bug report.
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 4 << 20)
            {
                var old = path + ".old";
                File.Move(path, old, overwrite: true);
            }
        }
        catch (IOException) { }
        return path;
    }

    public static void Write(string text)
    {
        try
        {
            lock (Gate) File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
        }
        catch (Exception) { }
    }
}
