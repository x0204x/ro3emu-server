using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Ro3Emu.Launcher;

/// <summary>
/// Pure-WPF shell reconstructed from observed launcher behavior. Update, repair and launch logic stays in
/// <see cref="LauncherController"/>; this class only projects typed JSON-shaped events onto the visual state.
/// </summary>
public partial class MainWindow : Window
{
    readonly LauncherConfig _config;
    readonly LauncherController _controller;
    readonly ObservableCollection<NoticeCard> _notices = new();
    readonly List<BannerCard> _banners = new();
    readonly List<UIElement> _particles = new();
    string _phase = "checking";
    string _launcherVersion = "";
    string? _installedVersion;
    string? _remoteVersion;
    bool _busy;
    int _bannerIndex;
    Storyboard? _motion;

    public MainWindow(LauncherConfig config, string exePath)
    {
        _config = config;
        InitializeComponent();
        Title = config.ServerName;
        WebsiteText.Text = config.ServerName;
        HomeNotices.ItemsSource = _notices;
        NewsNotices.ItemsSource = _notices;
        AddDefaultNotices();
        _controller = new LauncherController(config, exePath, Post, PickFolder, () => WindowState = WindowState.Minimized);
        SourceInitialized += (_, _) => RoundCorners();
        Loaded += async (_, _) =>
        {
            ((Storyboard)Resources["EntranceStoryboard"]).Begin(this);
            SetMotionEnabled(MotionCheckBox.IsChecked == true);
            await _controller.HandleAsync("ready");
        };
        Closing += (_, _) => _controller.Dispose();
    }

    void AddDefaultNotices()
    {
        _notices.Add(new("系統", "歡迎使用 RO3Emu 啟動器", "正在取得最新公告與版本資訊。", "#315D94", ""));
        _notices.Add(new("安全", "完整性驗證", "更新、修復與啟動器檔案都會在套用前驗證。", "#6F8651", ""));
    }

    void Post(object message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => Post(message));
            return;
        }
        JsonElement root = JsonSerializer.SerializeToElement(message);
        if (!root.TryGetProperty("type", out JsonElement typeElement)) return;
        switch (typeElement.GetString())
        {
            case "settings": ApplySettings(root); break;
            case "state": ApplyState(root); break;
            case "progress": ApplyProgress(root); break;
            case "server": ApplyServer(root); break;
            case "news": ApplyNews(root); break;
        }
    }

    void ApplySettings(JsonElement root)
    {
        _launcherVersion = String(root, "launcher");
        string serverName = String(root, "serverName");
        if (!string.IsNullOrWhiteSpace(serverName))
        {
            Title = serverName;
            WebsiteText.Text = serverName;
        }
        RenderVersions();
    }

    void ApplyState(JsonElement root)
    {
        _phase = String(root, "phase");
        _busy = _phase == "updating";
        _installedVersion = NullableString(root, "installed") ?? _installedVersion;
        _remoteVersion = NullableString(root, "remote") ?? _remoteVersion;
        string? detail = NullableString(root, "message");
        PackageText.Text = detail ?? DefaultMessage(_phase);
        DetailText.Text = _phase == "error" ? detail ?? "發生未預期的錯誤。" : "";
        bool failed = _phase == "error";
        ErrorGlyph.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        ErrorHelpButton.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        if (failed)
        {
            ErrorHelpCause.Text = detail ?? "啟動器遇到無法繼續的問題。";
            ErrorHelpCode.Text = $"狀態：{_phase}";
        }
        else ErrorHelpOverlay.Visibility = Visibility.Collapsed;
        UpdateProgress.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressDetails.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        RenderPhase();
        RenderVersions();
    }

    void ApplyProgress(JsonElement root)
    {
        _busy = true;
        string stage = String(root, "stage");
        long done = Long(root, "done");
        long total = Long(root, "total");
        double rate = Double(root, "rate");
        double eta = Double(root, "eta");
        double percent = total > 0 ? Math.Clamp(done * 100d / total, 0, 100) : 0;
        UpdateProgress.Visibility = Visibility.Visible;
        ProgressDetails.Visibility = Visibility.Visible;
        UpdateProgress.Value = percent;
        ProgressPercentText.Text = percent.ToString("0", CultureInfo.InvariantCulture) + "%";
        PackageText.Text = StageName(stage);
        TransferText.Text = total > 0
            ? $"{Size(done)} / {Size(total)}" + (rate > 0 ? $"  ·  {Size((long)rate)}/s  ·  剩餘 {Eta(eta)}" : "")
            : "";
        RenderPhase();
    }

    void ApplyServer(JsonElement root)
    {
        bool online = root.TryGetProperty("online", out JsonElement value) && value.ValueKind == JsonValueKind.True;
        int? players = root.TryGetProperty("players", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;
        WebsiteText.ToolTip = online ? $"伺服器上線{(players is null ? "" : $" · {players} 位玩家")}" : "伺服器離線";
    }

    void ApplyNews(JsonElement root)
    {
        if (!root.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array) return;
        var next = new List<NoticeCard>();
        foreach (JsonElement item in items.EnumerateArray())
        {
            string title = String(item, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;
            next.Add(new(
                String(item, "tag", "公告"),
                title,
                String(item, "text"),
                Accent(String(item, "color")),
                String(item, "date")));
        }
        if (next.Count == 0) return;
        _notices.Clear();
        foreach (NoticeCard item in next) _notices.Add(item);

        _banners.Clear();
        if (root.TryGetProperty("banners", out JsonElement banners) && banners.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement banner in banners.EnumerateArray())
            {
                string image = String(banner, "image");
                if (Uri.TryCreate(image, UriKind.Absolute, out Uri? imageUri) && imageUri.Scheme is "http" or "https")
                    _banners.Add(new BannerCard(imageUri, NullableString(banner, "url")));
            }
        }
        _bannerIndex = 0;
        RenderBanner();
    }

    void RenderPhase()
    {
        (string label, bool enabled) = _phase switch
        {
            "checking" => ("檢查中…", false),
            "install" => ("安裝遊戲", true),
            "update" => ("更新遊戲", true),
            "updating" => ("暫停下載", true),
            "repair" => ("修復遊戲", true),
            "ready" or "running" => ("開始遊戲", true),
            "offline" => ("重新連線", true),
            "error" => ("再試一次", true),
            "launching" => ("啟動中…", false),
            _ => ("重新檢查", true),
        };
        PrimaryButtonText.Text = label;
        PrimaryButtonIcon.Source = new BitmapImage(new Uri(
            _phase is "ready" or "running" or "launching" ? "/Assets/ui/steps/step03.png" : "/Assets/ui/icons/download.png",
            UriKind.Relative));
        PrimaryButton.IsEnabled = enabled;
        RepairButton.IsEnabled = !_busy && _installedVersion is not null && _phase is not "launching";
        int step = _phase switch
        {
            "ready" or "running" or "launching" => 3,
            "repair" => 2,
            _ => 1,
        };
        StepInstall.Opacity = step >= 1 ? 1 : 0.45;
        StepVerify.Opacity = step >= 2 ? 1 : 0.45;
        StepReady.Opacity = step >= 3 ? 1 : 0.45;
    }

    void RenderVersions()
    {
        string game = _installedVersion is null && _remoteVersion is null ? "" :
            _installedVersion is not null && _remoteVersion is not null && _installedVersion != _remoteVersion
                ? $" · 遊戲版本 {_installedVersion} → {_remoteVersion}"
                : $" · 遊戲版本 {_installedVersion ?? _remoteVersion}";
        VersionText.Text = $"啟動器 {_launcherVersion}{game}";
    }

    static string DefaultMessage(string phase) => phase switch
    {
        "checking" => "正在檢查更新…",
        "install" => "尚未安裝遊戲，需要下載完整遊戲檔案。",
        "update" => "有新的遊戲版本可以更新。",
        "updating" => "正在更新遊戲檔案…",
        "repair" => "部分遊戲檔案需要重新驗證。",
        "ready" => "已更新至最新版本，祝您遊戲愉快！",
        "offline" => "無法連線至更新伺服器。",
        "launching" => "正在啟動遊戲…",
        "running" => "遊戲目前正在執行。",
        _ => "發生未預期的錯誤。",
    };

    static string StageName(string stage) => stage switch
    {
        "scanning" => "檢查檔案",
        "downloading" => "下載中",
        "assembling" => "組裝檔案",
        "verifying" => "驗證中",
        "committing" => "套用更新",
        _ => "處理中",
    };

    static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    static string Eta(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return "—";
        TimeSpan time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    static string Accent(string color) => color.ToLowerInvariant() switch
    {
        "green" => "#6F8651",
        "orange" => "#D99B24",
        "red" => "#A6421F",
        _ => "#315D94",
    };

    static string String(JsonElement root, string name, string fallback = "")
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    static string? NullableString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    static long Long(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result) ? result : 0;
    static double Double(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double result) ? result : 0;

    async void OnPrimaryAction(object sender, RoutedEventArgs e) => await _controller.HandleAsync(_busy ? "pause" : "primary");
    async void OnRepair(object sender, RoutedEventArgs e) => await _controller.HandleAsync("repair");
    async void OnChangeDirectory(object sender, RoutedEventArgs e) => await _controller.HandleAsync("changeDir");
    async void OnOpenDirectory(object sender, RoutedEventArgs e) => await _controller.HandleAsync("openDir");

    void OnRegister(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(_config.RegisterUrl, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https")
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return;
        }
        MessageBox.Show(this, "本伺服器不需要網頁註冊；第一次登入時會自動建立一般玩家帳號。", "帳號註冊", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void OnErrorHelpClick(object sender, RoutedEventArgs e) => ErrorHelpOverlay.Visibility = Visibility.Visible;
    void OnErrorHelpClose(object sender, RoutedEventArgs e) => ErrorHelpOverlay.Visibility = Visibility.Collapsed;
    void OnErrorHelpDismiss(object sender, MouseButtonEventArgs e) => ErrorHelpOverlay.Visibility = Visibility.Collapsed;

    void OnBannerPrevious(object sender, RoutedEventArgs e)
    {
        if (_banners.Count == 0) return;
        _bannerIndex = (_bannerIndex + _banners.Count - 1) % _banners.Count;
        RenderBanner();
    }

    void OnBannerNext(object sender, RoutedEventArgs e)
    {
        if (_banners.Count == 0) return;
        _bannerIndex = (_bannerIndex + 1) % _banners.Count;
        RenderBanner();
    }

    void OnBannerLink(object sender, RoutedEventArgs e)
    {
        if (_banners.Count == 0 || !Uri.TryCreate(_banners[_bannerIndex].Link, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    void RenderBanner()
    {
        bool visible = _banners.Count > 0;
        BannerHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        BannerImage.Source = new BitmapImage(_banners[_bannerIndex].Image);
        BannerLinkButton.Visibility = string.IsNullOrWhiteSpace(_banners[_bannerIndex].Link) ? Visibility.Collapsed : Visibility.Visible;
        BannerPrev.Visibility = BannerNext.Visibility = _banners.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        BannerBullets.Children.Clear();
        for (int index = 0; index < _banners.Count; index++)
            BannerBullets.Children.Add(new Ellipse
            {
                Width = 7, Height = 7, Margin = new Thickness(3, 0, 3, 0),
                Fill = new SolidColorBrush(index == _bannerIndex ? Colors.White : Color.FromArgb(125, 255, 255, 255)),
            });
    }

    void OnMotionChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) SetMotionEnabled(MotionCheckBox.IsChecked == true);
    }

    void SetMotionEnabled(bool enabled)
    {
        _motion?.Remove(this);
        _motion = null;
        foreach (UIElement particle in _particles) ParticleLayer.Children.Remove(particle);
        _particles.Clear();
        PoringTranslate.X = PoringTranslate.Y = 0;
        if (!enabled) return;

        _motion = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        var driftX = new DoubleAnimation(-7, 11, TimeSpan.FromSeconds(4.8));
        var driftY = new DoubleAnimation(3, -9, TimeSpan.FromSeconds(3.9));
        Storyboard.SetTargetName(driftX, nameof(PoringTranslate));
        Storyboard.SetTargetProperty(driftX, new PropertyPath(TranslateTransform.XProperty));
        Storyboard.SetTargetName(driftY, nameof(PoringTranslate));
        Storyboard.SetTargetProperty(driftY, new PropertyPath(TranslateTransform.YProperty));
        _motion.Children.Add(driftX);
        _motion.Children.Add(driftY);
        _motion.Begin(this, true);
        CreateAmbientParticles();
    }

    void CreateAmbientParticles()
    {
        string[] sources = ["leaf1.png", "leaf2.png", "petal1.png", "petal2.png", "petal3.png", "sparkle1.png", "sparkle2.png"];
        for (int index = 0; index < 12; index++)
        {
            var image = new Image
            {
                Source = new BitmapImage(new Uri($"/Assets/ui/particles/{sources[index % sources.Length]}", UriKind.Relative)),
                Width = 13 + index % 4 * 3,
                Opacity = 0.2 + index % 3 * 0.1,
            };
            Canvas.SetLeft(image, 70 + index * 66);
            Canvas.SetTop(image, 25 + index % 5 * 96);
            ParticleLayer.Children.Add(image);
            _particles.Add(image);
            var translate = new TranslateTransform();
            image.RenderTransform = translate;
            var fall = new DoubleAnimation(-18, 42, TimeSpan.FromSeconds(5.4 + index % 5))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                AutoReverse = true,
                BeginTime = TimeSpan.FromMilliseconds(index * 170),
            };
            translate.BeginAnimation(TranslateTransform.YProperty, fall);
        }
    }

    void OnNavigationChanged(object sender, RoutedEventArgs e)
    {
        if (Pages is null || sender is not FrameworkElement { Tag: string index } || !int.TryParse(index, out int page)) return;
        Pages.SelectedIndex = page;
    }
    void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void OnClose(object sender, RoutedEventArgs e) => Close();
    void OnDragWindow(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }

    string? PickFolder(string title)
    {
        if (Environment.GetEnvironmentVariable("RO3EMU_LAUNCHER_PICK") is { Length: > 0 } scripted) return scripted;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    void RoundCorners()
    {
        const int DwmwaWindowCornerPreference = 33, DwmcpRound = 2;
        int preference = DwmcpRound;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    sealed record NoticeCard(string Tag, string Title, string Text, string Accent, string Date);
    sealed record BannerCard(Uri Image, string? Link);
}
