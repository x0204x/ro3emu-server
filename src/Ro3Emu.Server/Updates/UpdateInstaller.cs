using System.Text.Json;

namespace Ro3Emu.Server.Updates;

/// <summary>The update manager's folders and files under &lt;data&gt;/updates.</summary>
public sealed class UpdatePaths(string dataDir)
{
    public string Root { get; } = Path.Combine(dataDir, "updates");
    public string Downloads => Path.Combine(Root, "downloads");
    public string Staging => Path.Combine(Root, "staging");
    /// <summary>The files the last install or rollback replaced (one version), with <see cref="PreviousRecord"/> beside.</summary>
    public string Previous => Path.Combine(Root, "previous");
    public string PreviousRecord => Path.Combine(Root, "previous.json");
    /// <summary>The database backups made right before an install, out of reach of the backup rotation.</summary>
    public string Backups => Path.Combine(Root, "backups");
    public string Staged => Path.Combine(Root, "staged.json");
    public string Pending => Path.Combine(Root, "pending.json");
    public string History => Path.Combine(Root, "history.jsonl");
    public string Feed => Path.Combine(Root, "releases.json");
}

/// <param name="Files">The release's files (relative, with /) that were copied here from the install.</param>
/// <param name="Added">Files the swap put into the install that it did not have: a rollback takes them out again.</param>
/// <param name="Backup">The database backup (in <see cref="UpdatePaths.Backups"/>) made right before the swap, or null.</param>
public sealed record PreviousRecord(string Version, DateTimeOffset Saved, List<string> Files, List<string> Added, string? Backup);

/// <param name="Kept">Settings files whose edited copy stayed; the release's own is beside them as *.new.</param>
public sealed record SwapResult(PreviousRecord Previous, List<string> Kept);

/// <summary>
/// Swaps a release's files into the install folder while the server runs, file by file, keeping what it replaces:
/// - **Previous first:** each file about to be replaced is copied to updates/previous (the record is written last), so
///   the version that runs now can always come back (rollback, or Ro3Emu.Server --rollback-update offline).
/// - **Rename, then move in:** the current file is renamed in its own folder to *.old (Linux and Windows both let a
///   running executable be renamed, not overwritten) and the new one moved in. A failure puts every file back as it
///   was. The *.old files are deleted at the next start (<see cref="DeleteLeftovers"/>).
/// - **Settings:** an appsettings*.json the operator edited stays; the release's own lands beside it as *.new.
/// The process manager then starts the server again from the new files (exit 75).
/// </summary>
public static class UpdateInstaller
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>
    /// Puts the files of <paramref name="source"/> (a release folder's layout) into <paramref name="install"/> and takes
    /// out the files of <paramref name="remove"/>; what they replace goes to <paramref name="previous"/> first.
    /// </summary>
    public static SwapResult Swap(string source, string install, UpdatePaths paths, string runningVersion, string? backup,
        IReadOnlyCollection<string>? remove = null)
    {
        install = Path.GetFullPath(install);
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(source, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
        if (files.Count == 0) throw new UpdateException("There are no files to install.");
        var gone = (remove ?? []).Where(r => !files.Contains(r) && File.Exists(Inside(install, r))).ToList();
        CheckWritable(install, files);
        var record = SavePrevious(install, paths, runningVersion, backup, files, gone);
        var kept = new List<string>();
        var done = new List<Moved>();
        try
        {
            foreach (string rel in files) Place(Path.Combine(source, rel), Inside(install, rel), rel, done, kept);
            foreach (string rel in gone) done.Add(new Moved(Inside(install, rel), MoveAside(Inside(install, rel)), null));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Undo(done);
            throw new UpdateException("A file of the install could not be replaced (" + e.Message + "). Every file is back as it was.");
        }
        return new SwapResult(record, kept);
    }

    /// <summary>A release path inside the install folder; refused when it would leave it.</summary>
    internal static string Inside(string install, string rel)
    {
        string target = Path.GetFullPath(Path.Combine(install, rel));
        if (!target.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UpdateException("The path " + rel + " leaves the install folder.");
        return target;
    }

    /// <summary>Every folder the swap writes to takes a file: else nothing is touched.</summary>
    private static void CheckWritable(string install, List<string> files)
    {
        foreach (string dir in files.Select(f => Path.GetDirectoryName(Inside(install, f))!).Distinct(StringComparer.Ordinal))
        {
            string probe = Path.Combine(dir, ".ro3emu-update-probe");
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new UpdateException("The server cannot write to " + dir + " (" + e.Message + "). Nothing was changed.");
            }
        }
    }

    /// <summary>Copies the files about to be replaced or removed to updates/previous and writes its record.</summary>
    private static PreviousRecord SavePrevious(string install, UpdatePaths paths, string version, string? backup, List<string> files, List<string> gone)
    {
        if (Directory.Exists(paths.Previous)) Directory.Delete(paths.Previous, recursive: true);
        UpdatePackage.TryDelete(paths.PreviousRecord);
        var saved = new List<string>();
        var added = new List<string>();
        foreach (string rel in files.Concat(gone))
        {
            string current = Inside(install, rel);
            if (!File.Exists(current))
            {
                added.Add(rel);
                continue;
            }
            string copy = Path.Combine(paths.Previous, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(current, copy); // keeps the Unix mode
            saved.Add(rel);
        }
        var record = new PreviousRecord(version, DateTimeOffset.UtcNow, saved, added, backup);
        WriteJson(paths.PreviousRecord, record);
        return record;
    }

    /// <summary>One file the swap changed: <paramref name="Old"/> is the install's own, renamed aside; <paramref name="From"/>
    /// is where the new one came from (null while it is not in place yet, or for a file only taken out).</summary>
    private sealed record Moved(string Target, string? Old, string? From);

    private static void Place(string from, string target, string rel, List<Moved> done, List<string> kept)
    {
        if (UpdatePlatform.Settings.Contains(rel) && File.Exists(target))
        {
            if (!SameContent(from, target))
            {
                File.Move(from, target + ".new", overwrite: true);
                kept.Add(rel);
            }
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string? old = File.Exists(target) ? MoveAside(target) : null;
        done.Add(new Moved(target, old, null));
        File.Move(from, target);
        done[^1] = done[^1] with { From = from };
    }

    /// <summary>Renames a file to *.old in its folder (an older leftover is deleted first, or a fresh name taken).</summary>
    private static string MoveAside(string target)
    {
        string old = target + ".old";
        if (File.Exists(old))
        {
            try { File.Delete(old); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { old = target + "." + Guid.NewGuid().ToString("N")[..8] + ".old"; }
        }
        File.Move(target, old);
        return old;
    }

    /// <summary>Puts every changed file back: the new one to where it came from (a rollback's source is the only copy of
    /// the previous version), the install's own back under its name.</summary>
    private static void Undo(List<Moved> done)
    {
        for (int i = done.Count - 1; i >= 0; i--)
        {
            var (target, old, from) = done[i];
            try
            {
                if (from != null && File.Exists(target)) File.Move(target, from, overwrite: true);
                if (old != null && File.Exists(old)) File.Move(old, target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // the previous copy in updates/previous still holds it
        }
    }

    private static bool SameContent(string a, string b) =>
        new FileInfo(a).Length == new FileInfo(b).Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    /// <summary>The previous version, ready to swap back: its record when its files are all still there.</summary>
    public static PreviousRecord? ReadPrevious(UpdatePaths paths)
    {
        var record = ReadJson<PreviousRecord>(paths.PreviousRecord);
        return record != null && record.Files.All(f => File.Exists(Path.Combine(paths.Previous, f))) ? record : null;
    }

    /// <summary>
    /// Puts the previous version back: its saved files in, the files its install added out. What runs now becomes the
    /// previous version, so the step can be undone the same way.
    /// </summary>
    public static SwapResult Restore(string install, UpdatePaths paths, string runningVersion)
    {
        var previous = ReadPrevious(paths) ?? throw new UpdateException("No previous version is kept.");
        string source = paths.Previous + ".restoring";
        if (Directory.Exists(source)) Directory.Delete(source, recursive: true);
        Directory.Move(paths.Previous, source);
        try
        {
            return Swap(source, install, paths, runningVersion, null, previous.Added);
        }
        catch
        {
            // Nothing was swapped: the previous version stays the one kept.
            if (Directory.Exists(paths.Previous)) Directory.Delete(paths.Previous, recursive: true);
            Directory.Move(source, paths.Previous);
            WriteJson(paths.PreviousRecord, previous);
            throw;
        }
        finally
        {
            if (Directory.Exists(source)) Directory.Delete(source, recursive: true);
        }
    }

    /// <summary>Deletes the *.old files a swap left in the install's folders (best effort: a file still in use stays).</summary>
    public static void DeleteLeftovers(string install)
    {
        foreach (string dir in new[] { install, Path.Combine(install, "server"), Path.Combine(install, "client") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string old in Directory.EnumerateFiles(dir, "*.old")) UpdatePackage.TryDelete(old);
        }
    }

    internal static void WriteJson<T>(string file, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file + ".partial", JsonSerializer.Serialize(value, Json));
        File.Move(file + ".partial", file, overwrite: true);
    }

    internal static T? ReadJson<T>(string file) where T : class
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json) : null; }
        catch (Exception e) when (e is JsonException or IOException) { return null; }
    }
}
