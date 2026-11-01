using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Updates;

/// <summary>
/// Ro3Emu.Server --rollback-update [config args]: the panel's rollback with the server stopped, for a new version that
/// does not start. It puts the files kept in &lt;data&gt;/updates/previous back into the release folder and says how to
/// restore the database backup made before the update; it never touches the database itself.
/// </summary>
public static class UpdateCli
{
    public static int Rollback(ServerOptions options, TextWriter output)
    {
        var hosting = UpdateHosting.Detect(options.Updates, UpdateHosting.Probe.Current());
        if (hosting.InstallDir is not { } install)
        {
            output.WriteLine("Rollback refused: this server does not run from a release folder" + (hosting.Problem != null ? " (" + hosting.Problem + ")." : "."));
            return 1;
        }
        var paths = new UpdatePaths(options.DataDir);
        try
        {
            using var held = RealmLock.Acquire(options.DataDir); // a running server holds it
            var previous = UpdateInstaller.ReadPrevious(paths) ?? throw new UpdateException("No previous version is kept in " + paths.Previous + ".");
            string running = Running();
            UpdateInstaller.Restore(install, paths, running);
            output.WriteLine($"The files of {previous.Version} are back in {install}; the ones of {running} are kept in {paths.Previous}.");
            if (previous.Backup is { } backup && File.Exists(Path.Combine(paths.Backups, backup)))
                output.WriteLine("The database is unchanged. To go back to the one from before the update (losing what changed since): "
                    + $"Ro3Emu.Server --restore-backup \"{Path.Combine(paths.Backups, backup)}\"");
            return 0;
        }
        catch (Exception e) when (e is UpdateException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            output.WriteLine("Rollback refused: " + e.Message);
            return 1;
        }
    }

    private static string Running() => ReleaseVersion.TryParse(ReleaseVersion.Running, out var v) ? v.ToString() : ReleaseVersion.Running;
}
