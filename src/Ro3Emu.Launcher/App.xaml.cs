using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Ro3Emu.Patcher;

namespace Ro3Emu.Launcher;

public partial class App : Application
{
    Mutex? _singleInstance;
    bool _ownsSingleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        string exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "RO3Launcher.exe");
        bool explicitHandoff = RestartHandoff.HasMarker(e.Args);
        if (!RestartHandoff.WaitForPreviousProcess(e.Args, TimeSpan.FromSeconds(15)))
        {
            Shutdown(12);
            return;
        }
        base.OnStartup(e);
        string instanceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(exePath).ToUpperInvariant())))[..16];
        // Launchers before the explicit PID handoff started the replacement without arguments. The .old file is the
        // only durable signal in that upgrade path, so let the replacement wait briefly for the legacy mutex owner.
        bool legacyHandoff = !explicitHandoff && File.Exists(exePath + ".old");
        _singleInstance = new Mutex(false, @"Local\Ro3Emu.Launcher." + instanceId);
        try
        {
            _ownsSingleInstance = _singleInstance.WaitOne(legacyHandoff ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            _ownsSingleInstance = true;
        }
        if (!_ownsSingleInstance)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
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
        if (_ownsSingleInstance)
        {
            try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
