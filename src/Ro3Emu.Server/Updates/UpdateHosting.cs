namespace Ro3Emu.Server.Updates;

/// <summary>How the server runs, which decides what the update manager may do (docs/guide/panel.md, Updates).</summary>
public enum UpdateHostingMode
{
    /// <summary>A build from the source tree (not a release's folder): updates come from git.</summary>
    Source,
    /// <summary>A systemd unit (server.sh --service: Restart=always, RestartPreventExitStatus=0).</summary>
    Systemd,
    /// <summary>A Windows service wrapper (NSSM, WinSW, ...) that starts the server again when it exits with 75.</summary>
    Service,
    /// <summary>A container: the image is built and replaced outside the server.</summary>
    Docker,
    /// <summary>RO3Emu.exe or the launcher on Windows: they stop with the server and update themselves from the release.</summary>
    App,
    /// <summary>Started by hand (server.sh in a terminal, the exe in a console): nothing starts it again.</summary>
    Manual,
}

/// <param name="InstallDir">The release folder (with server/ and the RO3Emu tool); null for a source build.</param>
/// <param name="Problem">Why an install cannot run here although the mode allows one, or null.</param>
public sealed record UpdateHosting(UpdateHostingMode Mode, string? InstallDir, string? Problem)
{
    /// <summary>The page installs: a process manager starts the server again into the swapped files.</summary>
    public bool CanInstall => Mode is UpdateHostingMode.Systemd or UpdateHostingMode.Service && InstallDir != null && Problem == null;

    public bool CanDownload => Mode != UpdateHostingMode.Source;

    public string Key => Mode switch
    {
        UpdateHostingMode.Systemd => "systemd",
        UpdateHostingMode.Service => "service",
        UpdateHostingMode.Docker => "docker",
        UpdateHostingMode.App => "app",
        UpdateHostingMode.Manual => "manual",
        _ => "source",
    };

    /// <summary>What the detection looks at, so a test can give another machine's answers.</summary>
    public sealed record Probe(bool Windows, bool Linux, string? ProcessPath, bool Interactive, Func<string, string?> Environment, Func<string, bool> FileExists)
    {
        public static Probe Current() => new(OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), System.Environment.ProcessPath,
            System.Environment.UserInteractive, System.Environment.GetEnvironmentVariable, File.Exists);
    }

    /// <summary>The variable RO3Emu.exe and the launcher set for the server they start (Ro3Emu.Setup ServerProcess).</summary>
    public const string StarterVariable = "RO3EMU_STARTER";

    /// <summary>
    /// Ro3Emu:Updates:Hosting when it names a mode, else found: a container (/.dockerenv, /run/.containerenv,
    /// DOTNET_RUNNING_IN_CONTAINER), RO3Emu.exe or the launcher (<see cref="StarterVariable"/>), systemd (INVOCATION_ID,
    /// which systemd sets for every unit it starts), a Windows service (no interactive session), else by hand. Outside
    /// a container, a server that is not in a release's folder is a source build, whatever the setting.
    /// </summary>
    public static UpdateHosting Detect(UpdateOptions options, Probe probe)
    {
        string? install = InstallFolder(options, probe);
        var mode = Named(options.Hosting) ?? Found(probe);
        if (mode == UpdateHostingMode.Docker || install != null) return new(mode, install, null);
        return new(UpdateHostingMode.Source, null,
            options.InstallDir is { Length: > 0 } dir ? "Ro3Emu:Updates:InstallDir (" + dir + ") is not a release folder." : null);
    }

    private static UpdateHostingMode? Named(string? hosting) => (hosting ?? "").Trim().ToLowerInvariant() switch
    {
        "systemd" => UpdateHostingMode.Systemd,
        "service" => UpdateHostingMode.Service,
        "docker" => UpdateHostingMode.Docker,
        "app" => UpdateHostingMode.App,
        "manual" => UpdateHostingMode.Manual,
        _ => null,
    };

    private static UpdateHostingMode Found(Probe probe)
    {
        if (probe.Linux && (probe.FileExists("/.dockerenv") || probe.FileExists("/run/.containerenv"))
            || string.Equals(probe.Environment("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
            return UpdateHostingMode.Docker;
        if (!string.IsNullOrEmpty(probe.Environment(StarterVariable))) return UpdateHostingMode.App;
        if (probe.Linux && !string.IsNullOrEmpty(probe.Environment("INVOCATION_ID"))) return UpdateHostingMode.Systemd;
        if (probe.Windows && !probe.Interactive) return UpdateHostingMode.Service;
        return UpdateHostingMode.Manual;
    }

    /// <summary>
    /// The release folder: Ro3Emu:Updates:InstallDir, else the folder above the running executable when that sits in
    /// server/ beside the RO3Emu tool (the layout of both release packages). Null when neither is a release's.
    /// </summary>
    private static string? InstallFolder(UpdateOptions options, Probe probe)
    {
        var platform = probe.Windows ? UpdatePlatform.Windows : UpdatePlatform.Linux;
        string? folder = options.InstallDir is { Length: > 0 } dir ? Path.GetFullPath(dir)
            : probe.ProcessPath is { } exe && Path.GetDirectoryName(Path.GetFullPath(exe)) is { } server
                && string.Equals(Path.GetFileName(server), "server", StringComparison.Ordinal) ? Path.GetDirectoryName(server) : null;
        if (folder == null) return null;
        return probe.FileExists(Path.Combine(folder, platform.ServerFile)) && probe.FileExists(Path.Combine(folder, platform.ToolFile)) ? folder : null;
    }
}
