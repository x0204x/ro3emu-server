using System.Diagnostics;
using Ro3Emu.Patcher;

namespace Ro3Emu.Launcher;

/// <summary>
/// The updater reports progress for every block it reads or downloads; forwarding each one to the page floods it.
/// This forwards at most one report every 150 ms, and immediately when the stage changes.
/// </summary>
public sealed class ThrottledProgress(Action<UpdateProgress> show) : IProgress<UpdateProgress>
{
    readonly Lock _gate = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _lastShown = long.MinValue;
    UpdateStage? _stage;

    public void Report(UpdateProgress value)
    {
        lock (_gate)
        {
            long now = _clock.ElapsedMilliseconds;
            if (_stage == value.Stage && now - _lastShown < 150) return;
            _stage = value.Stage;
            _lastShown = now;
            // Updater workers report in parallel.  Keep the callback inside the gate so
            // stateful consumers (speed sampling, logging and UI posting) see one ordered
            // stream and cannot race each other.
            show(value);
        }
    }
}
