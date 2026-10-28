using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Ro3Emu.Patcher;

namespace Ro3Emu.Launcher;

public partial class App : Application
{
    Mutex? _singleInstance;
    EventWaitHandle? _activationSignal;
    RegisteredWaitHandle? _activationRegistration;
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
        string activationName = @"Local\Ro3Emu.Launcher.Activate." + instanceId;
        // Launchers before the explicit PID handoff started the replacement without arguments. The .old file is the
        // only durable signal in that upgrade path, so let the replacement wait briefly for the legacy mutex owner.
        bool legacyHandoff = !explicitHandoff && File.Exists(exePath + ".old");
        _activationSignal = new EventWaitHandle(false, EventResetMode.AutoReset, activationName);
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
            // The primary launcher is commonly minimized while the game runs. Wake it instead of making a second
            // click appear to do nothing when the path-scoped mutex is already owned.
            _activationSignal.Set();
            _activationSignal.Dispose();
            _activationSignal = null;
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }
        SelfUpdate.DeleteLeftovers(exePath);
        var config = LauncherConfig.Load(Path.Combine(Path.GetDirectoryName(exePath)!, "launcher.json"));
        MainWindow = new MainWindow(config, exePath);
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationSignal,
            static (state, timedOut) =>
            {
                if (timedOut || state is not App app || app.Dispatcher.HasShutdownStarted) return;
                _ = app.Dispatcher.InvokeAsync(() =>
                {
                    if (app.MainWindow is Ro3Emu.Launcher.MainWindow window) window.RestoreAndActivate();
                });
            },
            this,
            Timeout.Infinite,
            executeOnlyOnce: false);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationRegistration?.Unregister(null);
        _activationRegistration = null;
        _activationSignal?.Dispose();
        _activationSignal = null;
        if (_ownsSingleInstance)
        {
            try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
