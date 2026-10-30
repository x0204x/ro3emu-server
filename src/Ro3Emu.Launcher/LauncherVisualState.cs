namespace Ro3Emu.Launcher;

internal enum MotionTransition { None, Start, Stop }

/// <summary>僅前景首頁播放裝飾；保留玩家的原勾選設定，不因切換視窗而改寫。</summary>
internal sealed class LauncherVisualState
{
    public bool IsRunning { get; private set; }

    public MotionTransition Update(bool requested, bool loaded, bool visible, bool active, bool minimized, bool home, int games)
    {
        bool next = requested && loaded && visible && active && !minimized && home && games == 0;
        if (next == IsRunning) return MotionTransition.None;
        IsRunning = next;
        return next ? MotionTransition.Start : MotionTransition.Stop;
    }
}

/// <summary>輪播圖片快取有固定上限，避免反覆建立同一張圖片，也不無限保留公告資源。</summary>
internal sealed class LauncherImageCache<T>(int capacity) where T : class
{
    readonly Dictionary<Uri, T> _items = [];
    readonly Queue<Uri> _order = new();

    public T GetOrCreate(Uri uri, Func<Uri, T> create)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        if (_items.TryGetValue(uri, out T? item)) return item;
        item = create(uri);
        if (_items.Count == capacity) _items.Remove(_order.Dequeue());
        _items.Add(uri, item);
        _order.Enqueue(uri);
        return item;
    }
}
