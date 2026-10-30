namespace Ro3Emu.Launcher;

/// <summary>Pure process-slot state shared by the controller and source-linked unit tests.</summary>
internal readonly record struct GameProcessState(int ActiveCount)
{
    public const int Capacity = 4;
    public bool AnyRunning => ActiveCount > 0;
    public bool CanStartAnother => ActiveCount < Capacity;
    public bool CanChangeInstallation => ActiveCount == 0;
}

/// <summary>
/// 追蹤至多四個遊戲程序；比對原物件，避免延遲的結束通知誤刪已重用 PID 的另一個程序。
/// </summary>
internal sealed class GameProcessTracker<T> where T : class
{
    readonly Lock _gate = new();
    readonly Dictionary<int, T> _processes = [];

    public GameProcessState State
    {
        get
        {
            lock (_gate) return new(_processes.Count);
        }
    }

    public bool TryAdd(int processId, T process)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentNullException.ThrowIfNull(process);
        lock (_gate)
        {
            if (_processes.Count >= GameProcessState.Capacity || _processes.ContainsKey(processId)) return false;
            _processes.Add(processId, process);
            return true;
        }
    }

    public bool TryRemove(int processId, T expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        lock (_gate)
        {
            if (!_processes.TryGetValue(processId, out T? current) || !ReferenceEquals(current, expected)) return false;
            return _processes.Remove(processId);
        }
    }
}
