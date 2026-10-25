using System.Diagnostics;

namespace Ro3Emu.Patcher;

/// <summary>
/// Replaces the running launcher exe: Windows lets a running exe be renamed, so the old one moves aside to
/// <c>*.old</c>, the new one takes its name, and <c>*.old</c> is deleted on the next start.
/// </summary>
public static class SelfUpdate
{
    public static void DeleteLeftovers(string exePath)
    {
        try
        {
            string old = exePath + ".old";
            if (File.Exists(old)) File.Delete(old);
            string partial = exePath + ".new";
            if (File.Exists(partial)) File.Delete(partial);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static bool IsNewer(string remote, string current)
        => Version.TryParse(remote, out var r) && Version.TryParse(current, out var c) && r > c;

    /// <summary>Downloads and swaps in the new exe; returns true when the caller should start it and exit.</summary>
    public static async Task<bool> ApplyAsync(RepositoryClient repo, LauncherRelease release, string currentVersion, string exePath, CancellationToken ct)
    {
        if (!IsNewer(release.Version, currentVersion) || string.IsNullOrEmpty(release.Url))
        {
            return false;
        }
        byte[]? data = await repo.GetVerifiedBytesAsync(new ChannelAsset { Url = release.Url, Sha256 = release.Sha256 }, ct);
        if (data is null)
        {
            return false; // a broken launcher release must never replace a working one
        }
        string staged = exePath + ".new";
        await File.WriteAllBytesAsync(staged, data, ct);
        string old = exePath + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exePath, old);
        try
        {
            File.Move(staged, exePath);
        }
        catch
        {
            File.Move(old, exePath);
            throw;
        }
        return true;
    }

    public static void Restart(string exePath, string arguments = "")
    {
        Process.Start(new ProcessStartInfo(exePath, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exePath)! });
    }
}
