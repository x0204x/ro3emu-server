using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Ro3Emu.Patcher;

namespace Ro3Emu.Launcher;

public partial class App : Application
{
    Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Ro3EmuLauncher.exe");
        string instanceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(exePath).ToUpperInvariant())))[..16];
        _singleInstance = new Mutex(true, @"Local\Ro3Emu.Launcher." + instanceId, out bool first);
        if (!first)
        {
            Shutdown();
            return;
        }
        SelfUpdate.DeleteLeftovers(exePath);
        var config = LauncherConfig.Load(Path.Combine(Path.GetDirectoryName(exePath)!, "launcher.json"));
        MainWindow = new MainWindow(config, exePath);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
