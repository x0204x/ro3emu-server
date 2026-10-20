using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace Ro3Emu.Launcher;

/// <summary>
/// Frameless window hosting the HTML page (ui/, embedded) in WebView2:
/// the page talks to <see cref="LauncherController"/> through web messages; window commands are handled here.
/// </summary>
public partial class MainWindow : Window
{
    const string Origin = "https://launcher.ro3emu/";

    readonly LauncherController _controller;
    // Development: RO3EMU_LAUNCHER_UI=<folder> serves the page from disk (and enables dev tools).
    readonly string? _uiOverride = Environment.GetEnvironmentVariable("RO3EMU_LAUNCHER_UI");

    public MainWindow(LauncherConfig config, string exePath)
    {
        InitializeComponent();
        _controller = new LauncherController(config, exePath, Post, PickFolder, () => WindowState = WindowState.Minimized);
        SourceInitialized += (_, _) => RoundCorners();
        Loaded += async (_, _) => await InitializeWebAsync();
        Closing += (_, _) => _controller.Dispose();
    }

    async Task InitializeWebAsync()
    {
        try
        {
            string userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RO3Emu", "LauncherWebView");
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            await Web.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var answer = MessageBox.Show("The launcher needs the Microsoft Edge WebView2 Runtime, which is not installed on this PC.\n\nOpen the download page?",
                "RO3Emu", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes) OpenExternal("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
            Application.Current.Shutdown();
            return;
        }

        var core = Web.CoreWebView2;
        var settings = core.Settings;
        settings.AreDevToolsEnabled = _uiOverride is not null;
        settings.AreDefaultContextMenusEnabled = _uiOverride is not null;
        settings.AreBrowserAcceleratorKeysEnabled = _uiOverride is not null;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsNonClientRegionSupportEnabled = true; // CSS app-region: drag

        core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += async (_, e) => await OnMessageAsync(e.WebMessageAsJson);
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        core.Navigate(Origin + "index.html");
    }

    void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        string path = Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath.TrimStart('/'));
        if (path.Length == 0) path = "index.html";
        Stream? stream = OpenUiFile(path);
        var env = Web.CoreWebView2.Environment;
        e.Response = stream is null
            ? env.CreateWebResourceResponse(null, 404, "Not Found", "")
            : env.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {ContentType(path)}\r\nCache-Control: no-store");
    }

    Stream? OpenUiFile(string path)
    {
        if (path.Contains("..")) return null;
        if (_uiOverride is not null)
        {
            string file = Path.Combine(_uiOverride, path.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(file) ? new MemoryStream(File.ReadAllBytes(file)) : null;
        }
        return Assembly.GetExecutingAssembly().GetManifestResourceStream("ui/" + path);
    }

    static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };

    async Task OnMessageAsync(string json)
    {
        JsonElement message;
        try
        {
            message = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            return;
        }
        string? cmd = message.TryGetProperty("cmd", out var c) ? c.GetString() : null;
        if (cmd == "window")
        {
            switch (message.TryGetProperty("action", out var a) ? a.GetString() : null)
            {
                case "minimize": WindowState = WindowState.Minimized; break;
                case "close": Close(); break;
                case "drag": BeginDrag(); break;
            }
            return;
        }
        if (cmd != null) await _controller.HandleAsync(cmd);
    }

    void Post(object message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => Post(message));
            return;
        }
        Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    string? PickFolder(string title)
    {
        // Testing: RO3EMU_LAUNCHER_PICK=<folder> answers the folder dialog.
        if (Environment.GetEnvironmentVariable("RO3EMU_LAUNCHER_PICK") is { Length: > 0 } scripted) return scripted;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    static void OpenExternal(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    // Native window plumbing: drag from the page's title strip, Windows 11 rounded corners.

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    void BeginDrag()
    {
        const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;
        ReleaseCapture();
        SendMessage(new WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
    }

    void RoundCorners()
    {
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;
        int preference = DWMWCP_ROUND;
        // Cosmetic: Windows before 11 refuses the attribute and keeps square corners.
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }
}
