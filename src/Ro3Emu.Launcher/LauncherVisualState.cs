namespace Ro3Emu.Launcher;

internal enum MotionTransition { None, Start, Stop }

/// <summary>百分比表示目前步驟的實際進度；是否能啟動仍由完整驗證後的 ready 狀態決定。</summary>
internal sealed record LauncherProgressState(string Stage, double? Percent, string Files)
{
    public bool IsVerification => Stage is "checking" or "inspecting" or "scanning" or "verifying";
    public string Label => Stage switch
    {
        "checking" => "核對正式機版本與檔案清單",
        "inspecting" => "盤點客戶端檔案",
        "scanning" => "核對客戶端 SHA-256",
        "indexing" => "核對可重用的更新資料",
        "downloading" => "下載更新檔案",
        "assembling" => "解壓、組裝與驗證檔案",
        "verifying" => "驗證檔案 SHA-256",
        "committing" => "套用更新",
        _ => "處理中",
    };

    public static LauncherProgressState Create(string stage, long done, long total, long filesDone, long filesTotal)
    {
        double? percent = total > 0 ? Math.Clamp(done * 100d / total, 0, 100)
            : filesTotal > 0 ? Math.Clamp(filesDone * 100d / filesTotal, 0, 100) : null;
        // 下載與組裝仍可能在處理最後一個區塊，保留既有的完成前上限。
        if (percent.HasValue)
        {
            percent = Math.Floor(percent.Value * 10) / 10;
            if (stage is "downloading" or "assembling") percent = Math.Min(99, percent.Value);
        }
        string files = filesTotal > 0
            ? $"檔案 {Math.Clamp(filesDone, 0, filesTotal):N0} / {filesTotal:N0}" : "";
        return new(stage, percent, files);
    }
}

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
