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
using System.Windows.Threading;

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
    readonly LauncherVisualState _visualState = new();
    readonly LauncherImageCache<BitmapImage> _bannerImages = new(3);
    readonly Dictionary<string, BitmapImage> _localImages = new(StringComparer.Ordinal);
    string _phase = "checking";
    string _launcherVersion = "";
    string? _installedVersion;
    string? _remoteVersion;
    int _activeGames;
    bool _busy;
    int _bannerIndex;
    DispatcherTimer? _bannerTimer;
    AnimationClock? _noticesClock;

    public MainWindow(LauncherConfig config, string exePath)
    {
        _config = config;
        InitializeComponent();
        Title = config.ServerName;
        WebsiteText.Text = config.ServerName;
        NoticesCopyA.ItemsSource = _notices;
        NoticesCopyB.ItemsSource = _notices;
        NewsNotices.ItemsSource = _notices;
        AddDefaultNotices();
        ConfigureExternalLink(WebsiteText, config.WebsiteUrl, config.ServerName);
        ConfigureExternalLink(DiscordLink, config.DiscordUrl, "Discord");
        ConfigureExternalLink(FacebookLink, config.FacebookUrl, "Facebook");
        ConfigureExternalLink(YoutubeLink, config.YoutubeUrl, "YouTube");
        _controller = new LauncherController(config, exePath, Post, PickFolder,
            CloseAfterSuccessfulLaunch,
            RestoreAndActivate);
        SourceInitialized += (_, _) => RoundCorners();
        Activated += (_, _) => RefreshMotion();
        Deactivated += (_, _) => RefreshMotion();
        IsVisibleChanged += (_, _) => RefreshMotion();
        Loaded += async (_, _) =>
        {
            ((Storyboard)Resources["EntranceStoryboard"]).Begin(this);
            SetMotionEnabled(MotionCheckBox.IsChecked == true);
            if (string.IsNullOrWhiteSpace(_config.UiFixture)) await _controller.HandleAsync("ready");
            else ApplyUiFixture(_config.UiFixture);
        };
        Closing += (_, _) =>
        {
            SetMotionEnabled(false);
            _controller.Dispose();
        };
    }

    void AddDefaultNotices()
    {
        _notices.Add(new("系統", "歡迎使用 Ragnarok 3 啟動器", "正在取得最新公告與版本資訊。", "#315D94", ""));
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
        if (root.TryGetProperty("installed", out JsonElement installed))
            _installedVersion = installed.ValueKind == JsonValueKind.String ? installed.GetString() : null;
        if (root.TryGetProperty("remote", out JsonElement remote))
            _remoteVersion = remote.ValueKind == JsonValueKind.String ? remote.GetString() : null;
        if (root.TryGetProperty("activeGames", out JsonElement activeGames) && activeGames.TryGetInt32(out int count))
            _activeGames = Math.Clamp(count, 0, GameProcessState.Capacity);
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
        RefreshMotion();
    }

    void ApplyProgress(JsonElement root)
    {
        _busy = true;
        string stage = String(root, "stage");
        long done = Long(root, "done");
        long total = Long(root, "total");
        string file = String(root, "file");
        double rate = Double(root, "rate");
        double eta = Double(root, "eta");
        double rawPercent = total > 0 ? Math.Clamp(done * 100d / total, 0, 100) : 0;
        // A full download or assembly byte count is not the end of the update: the last
        // chunk may still be getting decompressed, hashed and committed.  Reserve 100%
        // for the updater's final committing report so the UI never claims completion early.
        bool completed = stage.Equals("committing", StringComparison.OrdinalIgnoreCase)
            && total > 0 && done >= total;
        double percent = completed ? 100 : Math.Min(99, Math.Floor(rawPercent));
        UpdateProgress.Visibility = Visibility.Visible;
        ProgressDetails.Visibility = Visibility.Visible;
        UpdateProgress.Value = percent;
        ProgressPercentText.Text = percent.ToString("0", CultureInfo.InvariantCulture) + "%";
        PackageText.Text = string.IsNullOrWhiteSpace(file) ? StageName(stage)
            : $"{StageName(stage)} · {System.IO.Path.GetFileName(file)}";
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
        var next = new List<NoticeCard>();
        if (root.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
        {
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
        }
        if (next.Count > 0)
        {
            _notices.Clear();
            foreach (NoticeCard item in next) _notices.Add(item);
        }
        bool trusted = root.TryGetProperty("trusted", out JsonElement trustedElement) && trustedElement.ValueKind == JsonValueKind.True;
        SignedContentText.Text = trusted ? "已簽章內容" : "未簽章內容";

        _banners.Clear();
        if (root.TryGetProperty("banners", out JsonElement banners) && banners.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement banner in banners.EnumerateArray())
            {
                string image = String(banner, "image");
                bool fixtureAsset = !string.IsNullOrWhiteSpace(_config.UiFixture) && image.StartsWith("pack://", StringComparison.OrdinalIgnoreCase);
                if (Uri.TryCreate(image, UriKind.Absolute, out Uri? imageUri) && (imageUri.Scheme is "http" or "https" || fixtureAsset))
                    _banners.Add(new BannerCard(imageUri, NullableString(banner, "url")));
            }
        }
        _bannerIndex = 0;
        RenderBanner();
        Dispatcher.InvokeAsync(ApplyNoticesMotion, DispatcherPriority.Loaded);
    }

    void RenderPhase()
    {
        (string label, bool enabled) = _phase switch
        {
            "checking" => ("檢查中…", false),
            "install" => ("安裝遊戲", true),
            "update" => ("更新遊戲", true),
            "updating" => ("暫停下載", true),
            "queued" => ("排隊中…", false),
            "maintenance" => ("維護中", false),
            "repair" => ("修復遊戲", true),
            "ready" => ("開始遊戲", true),
            "running" when _activeGames < GameProcessState.Capacity && _installedVersion is not null => ($"啟動第 {_activeGames + 1} 個遊戲", true),
            "running" => ($"遊戲執行中（{_activeGames}/{GameProcessState.Capacity}）", false),
            "offline" => ("重新連線", true),
            "error" => ("再試一次", true),
            "launching" => ("啟動中…", false),
            _ => ("重新檢查", true),
        };
        PrimaryButtonText.Text = label;
        bool failed = _phase == "error";
        PrimaryButton.Visibility = failed ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.IsEnabled = failed;
        QueueStatusText.Text = _phase switch
        {
            "queued" => "正在等待伺服器佇列",
            "maintenance" => "伺服器維護中",
            "error" => "操作失敗",
            _ => "復原操作",
        };
        bool primaryHasIcon = _phase is "install" or "update" or "repair" or "ready" or "running";
        PrimaryButtonIcon.Visibility = primaryHasIcon ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButtonIcon.Margin = primaryHasIcon ? new Thickness(0, 0, 18, 0) : new Thickness(0);
        PrimaryButtonIcon.Source = LocalImage(
            _phase is "ready" or "running" or "launching" ? "/Assets/ui/steps/step03.png" : "/Assets/ui/icons/download.png");
        PrimaryButton.IsEnabled = enabled;
        RepairButtonText.Text = "修復遊戲";
        RepairButton.IsEnabled = !_busy && _activeGames == 0 && _installedVersion is not null
            && _phase is not ("launching" or "running");
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
        "queued" => "連線人數較多，正在等待伺服器允許進入。",
        "maintenance" => "伺服器目前正在維護，請稍後再試。",
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
        "assembling" => "解壓、組裝與驗證檔案",
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
    async void OnRetry(object sender, RoutedEventArgs e) => await _controller.HandleAsync("ready");
    async void OnRepair(object sender, RoutedEventArgs e) => await _controller.HandleAsync("repair");
    async void OnChangeDirectory(object sender, RoutedEventArgs e) => await _controller.HandleAsync("changeDir");
    async void OnOpenDirectory(object sender, RoutedEventArgs e) => await _controller.HandleAsync("openDir");

    void OnRegister(object sender, RoutedEventArgs e)
    {
        if (OpenExternal(_config.RegisterUrl)) return;
        MessageBox.Show(this, "本伺服器不需要網頁註冊；第一次登入時會自動建立一般玩家帳號。", "帳號註冊", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void OnWebsiteLink(object sender, MouseButtonEventArgs e) => OpenExternal(_config.WebsiteUrl);
    void OnDiscordLink(object sender, MouseButtonEventArgs e) => OpenExternal(_config.DiscordUrl);
    void OnFacebookLink(object sender, MouseButtonEventArgs e) => OpenExternal(_config.FacebookUrl);
    void OnYoutubeLink(object sender, MouseButtonEventArgs e) => OpenExternal(_config.YoutubeUrl);

    void OnErrorHelpClick(object sender, RoutedEventArgs e) => ErrorHelpOverlay.Visibility = Visibility.Visible;
    void OnErrorHelpClose(object sender, RoutedEventArgs e) => ErrorHelpOverlay.Visibility = Visibility.Collapsed;
    void OnErrorHelpDismiss(object sender, MouseButtonEventArgs e) => ErrorHelpOverlay.Visibility = Visibility.Collapsed;

    void OnBannerPrevious(object sender, RoutedEventArgs e)
    {
        if (_banners.Count == 0) return;
        _bannerIndex = (_bannerIndex + _banners.Count - 1) % _banners.Count;
        RenderBanner();
        RestartBannerAutoplay();
    }

    void OnBannerNext(object sender, RoutedEventArgs e)
    {
        if (_banners.Count == 0) return;
        _bannerIndex = (_bannerIndex + 1) % _banners.Count;
        RenderBanner();
        RestartBannerAutoplay();
    }

    void OnBannerLink(object sender, RoutedEventArgs e)
    {
        if (_banners.Count > 0) OpenExternal(_banners[_bannerIndex].Link);
    }

    void RenderBanner()
    {
        bool visible = _banners.Count > 0;
        BannerHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        BannerImage.Source = _bannerImages.GetOrCreate(_banners[_bannerIndex].Image, uri => new BitmapImage(uri));
        BannerLinkButton.Visibility = string.IsNullOrWhiteSpace(_banners[_bannerIndex].Link) ? Visibility.Collapsed : Visibility.Visible;
        BannerPrev.Visibility = BannerNext.Visibility = _banners.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        BannerBullets.Children.Clear();
        for (int index = 0; index < _banners.Count; index++)
        {
            var bullet = new Ellipse
            {
                Width = 8, Height = 8, Margin = new Thickness(4, 0, 4, 0), Cursor = Cursors.Hand,
                Fill = new SolidColorBrush(index == _bannerIndex ? Colors.White : Color.FromArgb(125, 255, 255, 255)),
            };
            int target = index;
            bullet.MouseLeftButtonDown += (_, _) => SelectBanner(target);
            BannerBullets.Children.Add(bullet);
        }
        ApplyBannerAutoplay();
    }

    void SelectBanner(int index)
    {
        if (_banners.Count == 0) return;
        _bannerIndex = Math.Clamp(index, 0, _banners.Count - 1);
        RenderBanner();
        RestartBannerAutoplay();
    }

    void ApplyBannerAutoplay()
    {
        _bannerTimer ??= CreateBannerTimer();
        if (_banners.Count > 1 && _visualState.IsRunning) _bannerTimer.Start();
        else _bannerTimer.Stop();
    }

    DispatcherTimer CreateBannerTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            if (_banners.Count < 2) return;
            _bannerIndex = (_bannerIndex + 1) % _banners.Count;
            RenderBanner();
        };
        return timer;
    }

    void RestartBannerAutoplay()
    {
        if (_bannerTimer?.IsEnabled != true) return;
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }

    void OnMotionChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) SetMotionEnabled(MotionCheckBox.IsChecked == true);
    }

    void RefreshMotion()
    {
        if (IsLoaded) SetMotionEnabled(MotionCheckBox.IsChecked == true);
    }

    void SetMotionEnabled(bool enabled)
    {
        MotionTransition transition = _visualState.Update(enabled, IsLoaded, IsVisible, IsActive,
            WindowState == WindowState.Minimized, Pages.SelectedIndex == 0, _activeGames);
        if (transition == MotionTransition.None) return;
        enabled = transition == MotionTransition.Start;
        PoringTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        PoringTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        foreach (UIElement particle in _particles)
        {
            // 移除時同時解除每個時鐘，不能只讓粒子離開視覺樹後繼續計算。
            particle.BeginAnimation(OpacityProperty, null);
            if (particle.RenderTransform is TransformGroup transforms)
                foreach (Transform transform in transforms.Children)
                    if (transform is TranslateTransform translate)
                    {
                        translate.BeginAnimation(TranslateTransform.XProperty, null);
                        translate.BeginAnimation(TranslateTransform.YProperty, null);
                    }
                    else if (transform is RotateTransform rotate) rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            ParticleLayer.Children.Remove(particle);
        }
        _particles.Clear();
        PoringTranslate.X = PoringTranslate.Y = 0;
        if (enabled)
        {
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            PoringTranslate.BeginAnimation(TranslateTransform.YProperty, Decorative(new DoubleAnimation(0, -14, TimeSpan.FromSeconds(2.9))
            {
                RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true, EasingFunction = ease,
            }));
            PoringTranslate.BeginAnimation(TranslateTransform.XProperty, Decorative(new DoubleAnimation(-12, 12, TimeSpan.FromSeconds(6.4))
            {
                RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true, EasingFunction = ease,
            }));
            CreateAmbientParticles();
        }
        ApplyNoticesMotion();
        ApplyBannerAutoplay();
    }

    void CreateAmbientParticles()
    {
        string[] sources = ["leaf1.png", "leaf2.png", "petal1.png", "petal2.png", "petal3.png", "sparkle1.png", "sparkle2.png", "sparkle3.png", "sparkle4.png"];
        var random = new Random(7351);
        for (int index = 0; index < 14; index++)
        {
            string source = sources[random.Next(sources.Length)];
            bool rotates = source.StartsWith("leaf", StringComparison.Ordinal) || source.StartsWith("petal", StringComparison.Ordinal);
            var image = new Image
            {
                Source = LocalImage($"/Assets/ui/particles/{source}"),
                Width = 14 + random.NextDouble() * 18,
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            Canvas.SetLeft(image, 40 + random.NextDouble() * 860);
            Canvas.SetTop(image, 260 + random.NextDouble() * 360);
            ParticleLayer.Children.Add(image);
            _particles.Add(image);
            var translate = new TranslateTransform();
            var rotate = new RotateTransform();
            image.RenderTransform = new TransformGroup { Children = { rotate, translate } };
            double seconds = 8 + random.NextDouble() * 8;
            TimeSpan begin = TimeSpan.FromSeconds(random.NextDouble() * seconds);
            translate.BeginAnimation(TranslateTransform.YProperty, Decorative(new DoubleAnimation(0, -(170 + random.NextDouble() * 170), TimeSpan.FromSeconds(seconds))
            {
                RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin,
            }));
            translate.BeginAnimation(TranslateTransform.XProperty, Decorative(new DoubleAnimation(-10 + random.NextDouble() * 5, 5 + random.NextDouble() * 8,
                TimeSpan.FromSeconds(2.8 + random.NextDouble() * 2.6))
            {
                RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            }));
            var opacity = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(seconds), RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin,
            };
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0.55 + random.NextDouble() * 0.3, KeyTime.FromPercent(0.25)));
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            image.BeginAnimation(OpacityProperty, Decorative(opacity));
            if (rotates)
            {
                rotate.BeginAnimation(RotateTransform.AngleProperty,
                    Decorative(new DoubleAnimation(0, random.Next(2) == 0 ? 360 : -360, TimeSpan.FromSeconds(9 + random.NextDouble() * 9))
                    {
                        RepeatBehavior = RepeatBehavior.Forever,
                    }));
            }
        }
    }

    void ApplyNoticesMotion()
    {
        _noticesClock?.Controller?.Remove();
        NoticesScrollTranslate.ApplyAnimationClock(TranslateTransform.YProperty, null);
        _noticesClock = null;
        NoticesScrollTranslate.Y = 0;
        NoticesCopyB.Visibility = Visibility.Collapsed;
        if (!_visualState.IsRunning) return;
        NoticesCopyA.UpdateLayout();
        double contentHeight = NoticesCopyA.ActualHeight;
        double viewportHeight = NoticesViewport.ActualHeight;
        if (contentHeight <= 0 || viewportHeight <= 0 || contentHeight <= viewportHeight + 1) return;
        NoticesCopyB.Visibility = Visibility.Visible;
        var animation = new DoubleAnimation(0, -contentHeight, TimeSpan.FromSeconds(contentHeight / 22d))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        _noticesClock = Decorative(animation).CreateClock();
        NoticesScrollTranslate.ApplyAnimationClock(TranslateTransform.YProperty, _noticesClock);
    }

    void OnNoticesMouseEnter(object sender, MouseEventArgs e) => _noticesClock?.Controller?.Pause();
    void OnNoticesMouseLeave(object sender, MouseEventArgs e) => _noticesClock?.Controller?.Resume();

    static T Decorative<T>(T animation) where T : Timeline
    {
        Timeline.SetDesiredFrameRate(animation, 20);
        return animation;
    }

    BitmapImage LocalImage(string path)
    {
        if (_localImages.TryGetValue(path, out BitmapImage? image)) return image;
        image = new BitmapImage(new Uri(path, UriKind.Relative));
        if (image.CanFreeze) image.Freeze();
        _localImages.Add(path, image);
        return image;
    }

    void ConfigureExternalLink(FrameworkElement element, string? url, string label)
    {
        element.ToolTip = label;
        element.Cursor = IsAllowedExternal(url) ? Cursors.Hand : Cursors.Arrow;
    }

    bool OpenExternal(string? url)
    {
        if (!IsAllowedExternal(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return true;
    }

    bool IsAllowedExternal(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        string[] configured = [_config.RegisterUrl ?? "", _config.WebsiteUrl ?? "", _config.DiscordUrl ?? "", _config.FacebookUrl ?? "", _config.YoutubeUrl ?? ""];
        if (configured.Any(value => Uri.TryCreate(value, UriKind.Absolute, out Uri? known) &&
                                    Uri.Compare(uri, known, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0))
            return true;
        return _config.AllowedExternalHosts.Any(host => string.Equals(host.Trim().TrimEnd('.'), uri.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));
    }

    void ApplyUiFixture(string fixture)
    {
        Post(new { type = "settings", launcher = "2.0.0", serverName = _config.ServerName });
        Post(new
        {
            type = "news",
            trusted = true,
            items = Enumerable.Range(1, 9).Select(index => new
            {
                tag = index % 3 == 0 ? "維護" : index % 2 == 0 ? "活動" : "公告",
                title = $"介面驗收公告 {index}",
                text = "固定測試內容，用來驗證循環捲動、留白與卡片間距。",
                color = index % 3 == 0 ? "orange" : index % 2 == 0 ? "green" : "blue",
                date = $"10/{index + 10:00}",
            }).ToArray(),
            banners = new[]
            {
                new { image = "pack://application:,,,/Assets/ui/background/backgroundclean.png", url = (string?)null },
                new { image = "pack://application:,,,/Assets/ui/background/paperbackdrop.png", url = (string?)null },
            },
        });
        switch (fixture.Trim().ToLowerInvariant())
        {
            case "queue":
                Post(new { type = "state", phase = "queued", installed = "140080", remote = "140080", message = "目前前方還有 18 位玩家。" });
                break;
            case "maintenance":
                Post(new { type = "state", phase = "maintenance", installed = "140080", remote = "140080", message = "預計 20:30 完成維護。" });
                break;
            case "failure":
            case "failure-help-open":
                Post(new { type = "state", phase = "error", installed = "140079", remote = "140080", message = "測試用：無法驗證更新檔案。" });
                if (fixture.Contains("help", StringComparison.OrdinalIgnoreCase)) ErrorHelpOverlay.Visibility = Visibility.Visible;
                break;
            case "progress":
                Post(new { type = "state", phase = "updating", installed = "140079", remote = "140080", message = "正在下載更新內容…" });
                Post(new { type = "progress", stage = "downloading", done = 734003200L, total = 1073741824L, rate = 16777216d, eta = 20d });
                break;
            case "settings":
                Post(new { type = "state", phase = "ready", installed = "140080", remote = "140080", message = (string?)null });
                Pages.SelectedIndex = 2;
                SettingsTab.IsChecked = true;
                break;
            case "offline-unmanaged":
                Post(new
                {
                    type = "state",
                    phase = "offline",
                    installed = (string?)null,
                    remote = (string?)null,
                    message = "找不到由此啟動器完成的安裝紀錄。請連線至更新伺服器並完成安裝或修復。",
                });
                break;
            default:
                Post(new { type = "state", phase = "ready", installed = "140080", remote = "140080", message = (string?)null });
                break;
        }
    }

    void OnNavigationChanged(object sender, RoutedEventArgs e)
    {
        if (Pages is null || sender is not FrameworkElement { Tag: string index } || !int.TryParse(index, out int page)) return;
        Pages.SelectedIndex = page;
        RefreshMotion();
    }
    void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (MaximizeButton is null) return;
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "還原" : "最大化");
        RefreshMotion();
    }
    void OnClose(object sender, RoutedEventArgs e) => Close();
    void OnDragWindow(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }

    internal void RestoreAndActivate()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(RestoreAndActivate);
            return;
        }
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    void CloseAfterSuccessfulLaunch()
    {
        if (Dispatcher.CheckAccess()) Close();
        else _ = Dispatcher.InvokeAsync(Close);
    }

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
