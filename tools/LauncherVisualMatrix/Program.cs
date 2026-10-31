using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Ro3Emu.Launcher;

internal static class Program
{
    static readonly string[] Fixtures = ["home", "progress", "checking", "verifying", "inspecting", "verified-step", "queue", "maintenance", "failure-help-open", "offline-unmanaged", "settings"];
    static readonly (int Dpi, double Scale)[] Dpis = [(96, 1d), (120, 1.25d), (144, 1.5d)];
    static readonly (int Width, int Height, string Name)[] Sizes = [(1200, 800, "standard"), (1080, 720, "minimum")];
    static readonly string[] ControlNames =
    [
        "LeftPanel", "BannerHost", "NoticesViewport", "BottomPanel", "ActionsPanel",
        "StepInstall", "StepVerify", "StepReady", "UpdateProgress", "PackageText",
        "ProgressDetails", "ProgressPercentText", "TransferText", "FileProgressText", "DetailText", "QueueStatusText", "PrimaryButton", "RetryButton",
        "RepairButton", "RepairButtonText", "ErrorHelpOverlay", "MotionCheckBox",
    ];

    [STAThread]
    static int Main(string[] args)
    {
        string outputRoot = Path.GetFullPath(args.Length > 0
            ? args[0]
            : Path.Combine("TestResults", "LauncherVisualMatrix", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(outputRoot);

        // 僅載入視覺資源，避免 WPF pump 觸發產品 App.OnStartup 的正式啟動流程。
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "LauncherApp.xaml"))
            .Root?.Element(xaml + "Application.Resources")?.Elements().Single()
            ?? throw new InvalidOperationException("找不到登入器視覺資源。");
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString());
        var captures = new List<CaptureRecord>();
        var failures = new List<string>();
        var calibrations = Dpis.Select(dpi => Calibrate(dpi.Dpi, dpi.Scale)).ToArray();

        foreach (var size in Sizes)
        {
            foreach (string fixture in Fixtures)
            {
                string directory = Path.Combine(outputRoot, size.Name, fixture);
                Directory.CreateDirectory(directory);
                using var lab = new TemporaryLab();
                var config = new LauncherConfig
                {
                    Repository = "http://127.0.0.1:9/",
                    Channel = "visual-fixture",
                    ServerName = "Ragnarok 3 視覺驗收",
                    UiFixture = fixture,
                };
                var window = new MainWindow(config, lab.ExecutablePath)
                {
                    Width = size.Width,
                    Height = size.Height,
                    Left = SystemParameters.VirtualScreenLeft - size.Width - 200,
                    Top = SystemParameters.VirtualScreenTop,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                };
                window.Show();
                Pump(TimeSpan.FromMilliseconds(800));
                if (window.FindName("MotionCheckBox") is CheckBox motion) motion.IsChecked = false;
                Pump(TimeSpan.FromMilliseconds(100));
                window.UpdateLayout();

                var controls = CaptureLayout(window, ControlNames, failures, size.Name, fixture);
                ValidateFixture(window, fixture, failures, size.Name);
                ValidateNoOverlap(controls, "BottomPanel", "ActionsPanel", failures, size.Name, fixture);

                CaptureRecord? reference = null;
                foreach (var dpi in Dpis)
                {
                    string dpiDirectory = Path.Combine(directory, $"dpi-{dpi.Dpi:000}");
                    Directory.CreateDirectory(dpiDirectory);
                    string pngPath = Path.Combine(dpiDirectory, "actual.png");
                    BitmapSource bitmap = Render(window, size.Width, size.Height, dpi.Dpi, dpi.Scale);
                    SavePng(bitmap, pngPath);
                    string layoutPath = Path.Combine(dpiDirectory, "layout.json");
                    var scaledControls = controls.Select(item => item.WithPixels(dpi.Scale)).ToArray();
                    WriteJson(layoutPath, scaledControls);

                    var record = new CaptureRecord(size.Name, size.Width, size.Height, fixture, dpi.Dpi,
                        bitmap.PixelWidth, bitmap.PixelHeight, Relative(outputRoot, pngPath), Relative(outputRoot, layoutPath),
                        Sha256(pngPath), null);
                    if (dpi.Dpi == 96)
                    {
                        reference = record;
                    }
                    else if (reference is not null)
                    {
                        string normalizedPath = Path.Combine(dpiDirectory, "normalized-96.png");
                        BitmapSource normalized = Normalize(bitmap, size.Width, size.Height);
                        SavePng(normalized, normalizedPath);
                        string referencePath = Path.Combine(outputRoot, reference.Png);
                        record = record with { ComparisonTo96 = Compare(referencePath, normalizedPath) };
                    }
                    captures.Add(record);
                }
                window.Close();
                Pump(TimeSpan.FromMilliseconds(30));
            }
        }

        string assembly = typeof(MainWindow).Assembly.Location;
        var manifest = new
        {
            generatedUtc = DateTimeOffset.UtcNow,
            verdict = failures.Count == 0 ? "PARTIAL" : "FAIL",
            boundary = failures.Count == 0
                ? "合成 96/120/144 DPI 結構門檻通過；真實 HWND、WM_DPICHANGED 與 Per-Monitor V2 尚未驗收。"
                : "合成 DPI 結構門檻失敗。",
            runtime = new { os = Environment.OSVersion.VersionString, dotnet = Environment.Version.ToString(), framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription },
            launcherAssembly = new { path = assembly, sha256 = File.Exists(assembly) ? Sha256(assembly) : null },
            fixtures = Fixtures,
            sizes = Sizes.Select(size => new { size.Name, logicalWidth = size.Width, logicalHeight = size.Height }),
            dpi = Dpis.Select(dpi => new { dpi.Dpi, dpi.Scale }),
            motionFrozen = true,
            calibration = calibrations,
            captures,
            failures,
        };
        WriteJson(Path.Combine(outputRoot, "manifest.json"), manifest);
        WriteSummary(Path.Combine(outputRoot, "summary.md"), captures, failures, calibrations);
        Console.WriteLine($"{(failures.Count == 0 ? "PARTIAL" : "FAIL")}: {captures.Count} 張快照；{failures.Count} 個結構錯誤");
        Console.WriteLine(outputRoot);
        app.Shutdown();
        return failures.Count == 0 ? 0 : 1;
    }

    static CalibrationRecord Calibrate(int dpi, double scale)
    {
        int pixels = checked((int)Math.Round(96 * scale));
        var target = new RenderTargetBitmap(pixels, pixels, dpi, dpi, PixelFormats.Pbgra32);
        var rectangle = new Border { Width = 96, Height = 96, Background = Brushes.White };
        rectangle.Measure(new Size(96, 96));
        rectangle.Arrange(new Rect(0, 0, 96, 96));
        target.Render(rectangle);
        int stride = pixels * 4;
        byte[] bytes = new byte[stride * pixels];
        target.CopyPixels(bytes, stride, 0);
        int opaque = 0;
        for (int index = 3; index < bytes.Length; index += 4) if (bytes[index] != 0) opaque++;
        return new CalibrationRecord(dpi, pixels, pixels, opaque, opaque == pixels * pixels);
    }

    static BitmapSource Render(Visual visual, int logicalWidth, int logicalHeight, int dpi, double scale)
    {
        int width = checked((int)Math.Round(logicalWidth * scale));
        int height = checked((int)Math.Round(logicalHeight * scale));
        var target = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    static BitmapSource Normalize(BitmapSource source, int width, int height)
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen()) drawing.DrawImage(source, new Rect(0, 0, width, height));
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    static ComparisonRecord Compare(string referencePath, string candidatePath)
    {
        BitmapSource reference = LoadPbgra(referencePath);
        BitmapSource candidate = LoadPbgra(candidatePath);
        int stride = reference.PixelWidth * 4;
        byte[] a = new byte[stride * reference.PixelHeight];
        byte[] b = new byte[stride * candidate.PixelHeight];
        reference.CopyPixels(a, stride, 0);
        candidate.CopyPixels(b, stride, 0);
        long changed = 0;
        double squared = 0;
        long absolute = 0;
        int max = 0;
        for (int index = 0; index < a.Length; index++)
        {
            int delta = Math.Abs(a[index] - b[index]);
            if (delta > 0) changed++;
            absolute += delta;
            squared += delta * delta;
            max = Math.Max(max, delta);
        }
        return new ComparisonRecord((double)changed / a.Length, (double)absolute / a.Length, Math.Sqrt(squared / a.Length), max);
    }

    static BitmapSource LoadPbgra(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Pbgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    static LayoutRecord[] CaptureLayout(Window root, IEnumerable<string> names, List<string> failures, string size, string fixture)
    {
        var result = new List<LayoutRecord>();
        foreach (string name in names)
        {
            if (root.FindName(name) is not FrameworkElement element)
            {
                failures.Add($"{size}/{fixture}: 找不到控制項 {name}");
                continue;
            }
            Rect bounds;
            try { bounds = element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight)); }
            catch (InvalidOperationException) { bounds = Rect.Empty; }
            if (bounds.IsEmpty) bounds = new Rect(0, 0, 0, 0);
            bool visible = element.IsVisible && element.Visibility == Visibility.Visible;
            bool requiresArea = element is not TextBlock text || !string.IsNullOrEmpty(text.Text);
            if (visible && (!Finite(bounds.X) || !Finite(bounds.Y) || !Finite(bounds.Width) || !Finite(bounds.Height) ||
                            (requiresArea && (bounds.Width <= 0 || bounds.Height <= 0))))
                failures.Add($"{size}/{fixture}: {name} 的可見邊界無效");
            if (visible && (bounds.Left < -1 || bounds.Top < -1 || bounds.Right > root.ActualWidth + 1 || bounds.Bottom > root.ActualHeight + 1))
                failures.Add($"{size}/{fixture}: {name} 超出視窗邊界 {bounds}");
            result.Add(new LayoutRecord(name, element.Visibility.ToString(), element.IsArrangeValid,
                Round(bounds.X), Round(bounds.Y), Round(bounds.Width), Round(bounds.Height), null));
        }
        return result.ToArray();
    }

    static void ValidateFixture(Window root, string fixture, List<string> failures, string size)
    {
        bool Visible(string name) => root.FindName(name) is FrameworkElement element && element.Visibility == Visibility.Visible && element.IsVisible;
        bool Enabled(string name) => root.FindName(name) is Control control && control.IsEnabled;
        void Need(bool condition, string message) { if (!condition) failures.Add($"{size}/{fixture}: {message}"); }
        switch (fixture)
        {
            case "progress": Need(Visible("UpdateProgress") && Visible("ProgressDetails"), "進度列或進度細節未顯示"); break;
            case "checking":
                Need(root.FindName("UpdateProgress") is ProgressBar { IsIndeterminate: true }, "未知總量應顯示持續運作的進度列");
                Need(root.FindName("ProgressPercentText") is TextBlock { Text: "處理中…" }, "未知總量不得顯示假的 0%");
                Need(Enabled("PrimaryButton"), "核對期間應可暫停");
                break;
            case "verifying":
            case "inspecting":
                Need(root.FindName("UpdateProgress") is ProgressBar { IsIndeterminate: false, Value: 50 }, "核對進度未顯示實際的 50%");
                Need(root.FindName("ProgressPercentText") is TextBlock { Text: "50%" }, "核對百分比不正確");
                Need(root.FindName("FileProgressText") is TextBlock count && count.Text.Contains(" / ", StringComparison.Ordinal), "缺少已核對／總檔案數");
                Need(root.FindName("PrimaryButtonText") is TextBlock { Text: "暫停作業" }, "核對時未顯示暫停作業");
                break;
            case "verified-step":
                Need(root.FindName("ProgressPercentText") is TextBlock { Text: "100%" }, "核對完成步驟應顯示 100%");
                Need(root.FindName("PrimaryButtonText") is TextBlock { Text: "暫停作業" }, "步驟完成但契約未放行前，不可顯示開始遊戲");
                Need(!Enabled("RepairButton"), "核對契約期間不可插入修復");
                break;
            case "queue":
            case "maintenance": Need(Visible("PrimaryButton") && !Enabled("PrimaryButton"), "主要按鈕應顯示但停用"); break;
            case "failure-help-open": Need(Visible("RetryButton") && !Visible("PrimaryButton") && Visible("ErrorHelpOverlay"), "錯誤復原畫面狀態不一致"); break;
            case "offline-unmanaged":
                Need(Visible("PrimaryButton") && Enabled("PrimaryButton"), "未受管理客戶端離線時應允許重新連線");
                Need(root.FindName("PrimaryButtonText") is TextBlock { Text: "重新連線" }, "未受管理客戶端不得顯示開始遊戲");
                Need(!Enabled("RepairButton"), "未受管理客戶端離線時不得直接執行修復或啟動");
                break;
            case "settings": Need(Visible("MotionCheckBox"), "設定頁未顯示動態效果控制項"); break;
            default: Need(Visible("PrimaryButton"), "首頁主要按鈕未顯示"); break;
        }
    }

    static void ValidateNoOverlap(IEnumerable<LayoutRecord> controls, string first, string second, List<string> failures, string size, string fixture)
    {
        LayoutRecord? a = controls.FirstOrDefault(item => item.Name == first && item.Visibility == Visibility.Visible.ToString());
        LayoutRecord? b = controls.FirstOrDefault(item => item.Name == second && item.Visibility == Visibility.Visible.ToString());
        if (a is null || b is null) return;
        var ar = new Rect(a.X, a.Y, a.Width, a.Height);
        var br = new Rect(b.X, b.Y, b.Width, b.Height);
        ar.Intersect(br);
        if (!ar.IsEmpty && ar.Width > 1 && ar.Height > 1) failures.Add($"{size}/{fixture}: {first} 與 {second} 重疊 {ar}");
    }

    static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    static void WriteJson(string path, object value)
        => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    static void WriteSummary(string path, IReadOnlyCollection<CaptureRecord> captures, IReadOnlyCollection<string> failures, IReadOnlyCollection<CalibrationRecord> calibrations)
    {
        var text = new StringBuilder()
            .AppendLine("# Ragnarok 3 Launcher 合成 DPI 視覺矩陣")
            .AppendLine()
            .AppendLine($"- 結果：`{(failures.Count == 0 ? "PARTIAL" : "FAIL")}`")
            .AppendLine($"- 快照：{captures.Count} 張")
            .AppendLine($"- 結構錯誤：{failures.Count}")
            .AppendLine($"- 96 DIP 校正：{(calibrations.All(item => item.Passed) ? "通過" : "失敗")}")
            .AppendLine()
            .AppendLine("`PARTIAL` 表示 96／120／144 DPI 的離屏 raster 與結構門檻通過；這不是實體螢幕的 `WM_DPICHANGED`、Per-Monitor V2、DWM 或 GPU 驗收。")
            .AppendLine();
        if (failures.Count > 0)
        {
            text.AppendLine("## 失敗項目").AppendLine();
            foreach (string failure in failures) text.AppendLine($"- {failure}");
        }
        File.WriteAllText(path, text.ToString());
    }

    static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    sealed class TemporaryLab : IDisposable
    {
        readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-launcher-visual-" + Guid.NewGuid().ToString("N"));
        public string ExecutablePath { get; }

        public TemporaryLab()
        {
            Directory.CreateDirectory(_directory);
            ExecutablePath = Path.Combine(_directory, "RO3Launcher.exe");
            File.WriteAllBytes(ExecutablePath, [0x4D, 0x5A]);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    sealed record CalibrationRecord(int Dpi, int PixelWidth, int PixelHeight, int OpaquePixels, bool Passed);
    sealed record ComparisonRecord(double ChangedByteRatio, double Mae, double Rmse, int MaxChannelDelta);
    sealed record CaptureRecord(string Size, int LogicalWidth, int LogicalHeight, string Fixture, int Dpi,
        int PixelWidth, int PixelHeight, string Png, string Layout, string PngSha256, ComparisonRecord? ComparisonTo96);
    sealed record PixelBounds(double X, double Y, double Width, double Height);
    sealed record LayoutRecord(string Name, string Visibility, bool IsArrangeValid, double X, double Y, double Width, double Height, PixelBounds? Pixels)
    {
        public LayoutRecord WithPixels(double scale) => this with
        {
            Pixels = new PixelBounds(Round(X * scale), Round(Y * scale), Round(Width * scale), Round(Height * scale)),
        };
    }
}
