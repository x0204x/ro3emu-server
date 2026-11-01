using Ro3Emu.Panel;

namespace Ro3Emu.Server.Updates;

/// <summary>What the page says about the hosting mode, and the steps of an install by hand where the page cannot do it.</summary>
public sealed partial class UpdateManager
{
    private string HostingSummary() => Hosting.Mode switch
    {
        UpdateHostingMode.Systemd => "The server runs under systemd: an install swaps the files and the unit (Restart=always, as ./server.sh --service writes it) starts the new version.",
        UpdateHostingMode.Service => "The server runs as a Windows service: an install renames the running files aside and the service wrapper starts the new version (it must start the server again when it exits with code 75).",
        UpdateHostingMode.Docker => "The server runs in a container, whose image is built and replaced outside the server. The page downloads and checks a release; the image is updated by hand.",
        UpdateHostingMode.App => "The server runs under RO3Emu.exe or the launcher, which stop with it. The page downloads and checks a release; the install is done by hand.",
        UpdateHostingMode.Manual => "The server was started by hand, so nothing would start it again after an install. The page downloads and checks a release; the install is done by hand.",
        _ => Hosting.Problem ?? "The server runs from a build of the source tree, not from a release.",
    };

    private PanelUpdateHosting HostingState(StagedRecord? staged) =>
        new(Hosting.Key, Hosting.CanInstall, Hosting.CanDownload, HostingSummary(), Hosting.InstallDir, Steps(staged));

    /// <summary>The install by hand, with this server's folders in it.</summary>
    private List<string> Steps(StagedRecord? staged)
    {
        string install = Hosting.InstallDir ?? "the server's folder";
        string files = staged?.Folder ?? "the release package (download it here first, it is then checked)";
        bool windows = _platform == UpdatePlatform.Windows;
        return Hosting.Mode switch
        {
            UpdateHostingMode.Manual when !windows =>
            [
                "Stop the server (Ctrl+C in its terminal).",
                staged != null ? "Copy the new files over the old ones: cp -a '" + staged.Folder + "/.' '" + install + "/'" : "Extract " + files + " over " + install + ".",
                "Start it again with ./server.sh and the options you use.",
                "Run it as a service (./server.sh --service) and this page installs updates by itself.",
            ],
            UpdateHostingMode.Manual =>
            [
                "Stop the server.",
                "Copy everything in " + files + " over " + install + ". The data folder is not in the package and stays.",
                "Start the server again.",
                "Run it under a Windows service wrapper that starts it again when it exits (NSSM, WinSW) and this page installs updates by itself.",
            ],
            UpdateHostingMode.App =>
            [
                "Close RO3Emu (Q in RO3Emu.exe's window, or Exit in the launcher's tray menu).",
                "Start Ro3EmuLauncher.exe: it offers the update itself and keeps your data. Or copy everything in " + files + " over " + install + ".",
                "RO3Emu.exe patches the game again at its next start.",
            ],
            UpdateHostingMode.Docker =>
            [
                "Build or pull an image with the new release and recreate the container with the same data volume; the database stays in it.",
                staged != null ? "The checked package is " + staged.Archive + " (unpacked in " + staged.Folder + ")." : "Download the release here to get a checked copy of the package.",
            ],
            UpdateHostingMode.Source => ["git pull, then build and start the server again."],
            _ => [],
        };
    }
}
