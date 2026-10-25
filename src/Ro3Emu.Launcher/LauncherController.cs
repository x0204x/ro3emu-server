using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Ro3Emu.Patcher;

namespace Ro3Emu.Launcher;

/// <summary>
/// The launcher's logic behind the page: checks the channel, installs/updates/repairs the client with
/// <see cref="Updater"/>, updates the launcher itself, pings the game server and starts ro3.exe.
/// Messages to the page: state, progress, server, settings, news (see ui/app.js).
/// </summary>
public sealed class LauncherController : IDisposable
{
    readonly LauncherConfig _config;
    readonly string _exePath;
    readonly Action<object> _post;
    readonly Func<string, string?> _pickFolder;
    readonly Action _minimize;
    readonly UserSettings _settings;
    readonly RepositoryClient _repo;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    readonly string _version = typeof(LauncherController).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    readonly CancellationTokenSource _life = new();
    readonly Lock _logGate = new();
    readonly string _logPath;
    InstallLayout _layout;
    ChannelInfo? _channel;
    LocalState? _installed;
    string _phase = "checking";
    CancellationTokenSource? _work;
    Process? _game;
    bool _started;
    UpdateStage? _loggedStage;
    DateTimeOffset _lastProgressLog;

    public LauncherController(LauncherConfig config, string exePath, Action<object> post, Func<string, string?> pickFolder, Action minimize)
    {
        _config = config;
        _exePath = exePath;
        _settings = UserSettings.Load(config.ResolveSettingsFile(Path.GetDirectoryName(exePath)!));
        _post = post;
        _pickFolder = pickFolder;
        _minimize = minimize;
        _repo = new RepositoryClient(new Uri(config.Repository), config.RepositoryTrust);
        string executableDirectory = Path.GetDirectoryName(exePath)!;
        _logPath = Path.Combine(executableDirectory, "launcher.log");
        _layout = new InstallLayout(_settings.ClientDirectory
            ?? config.ResolveClientDirectory(executableDirectory)
            ?? executableDirectory);
        Log($"Launcher {_version} started; client={_layout.GameDirectory}");
    }

    public async Task HandleAsync(string cmd)
    {
        switch (cmd)
        {
            case "ready":
                PostSettings();
                if (!_started)
                {
                    _started = true;
                    _ = PingLoopAsync();
                }
                await CheckAsync();
                break;
            case "primary":
                if (_work != null) return;
                if (_phase == "running") return;
                if (_phase is "ready" or "offline-ready") await PlayAsync();
                else if (_phase is "install" or "update" or "repair") await UpdateAsync(verifyAll: _phase == "repair");
                else await CheckAsync();
                break;
            case "repair":
                if (_work == null) await UpdateAsync(verifyAll: true);
                break;
            case "pause":
                _work?.Cancel();
                break;
            case "changeDir":
                await ChangeDirectoryAsync();
                break;
            case "openDir":
                if (Directory.Exists(_layout.GameDirectory))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_layout.GameDirectory}\"") { UseShellExecute = true });
                break;
        }
    }

    void PostSettings() => _post(new
    {
        type = "settings",
        dir = _layout.GameDirectory,
        launcher = _version,
        serverName = _config.ServerName,
    });

    void PostState(string phase, string? message = null)
    {
        _phase = phase;
        Log($"State={phase}" + (string.IsNullOrWhiteSpace(message) ? "" : $"; {message}"));
        _post(new { type = "state", phase, installed = _installed?.Version, remote = _channel?.Version, message });
    }

    async Task CheckAsync()
    {
        PostState("checking");
        try
        {
            var updater = new Updater(_layout, _repo);
            var check = await updater.CheckAsync(_config.Channel, _life.Token);
            _channel = check.Channel;
            if (_config.RequireSignedChannel) _settings.AcceptChannelSequence(_channel.Channel, _channel.Sequence);
            _installed = check.Installed;
            await LoadNewsAsync();
            if (_channel.Launcher is { } release && SelfUpdate.IsNewer(release.Version, _version))
            {
                PostState("checking", $"正在將啟動器更新至 {release.Version}…");
                if (await SelfUpdate.ApplyAsync(_repo, release, _version, _exePath, _life.Token))
                {
                    using (SelfUpdate.Restart(_exePath, Environment.ProcessId)) { }
                    System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
                    return;
                }
            }
            if (check.Installed is null)
            {
                bool existing = File.Exists(Path.Combine(_layout.GameDirectory, "ro3.exe"));
                PostState("install", existing ? "已在此資料夾找到 RO3 客戶端，只會下載缺少或已變更的檔案。" : null);
            }
            else if (check.UpdateRequired) PostState("update", $"已有遊戲版本 {_channel.Version} 可供更新。");
            else if (updater.QuickVerify(check.Installed) is { Count: > 0 } changed) PostState("repair", $"有 {changed.Count} 個遊戲檔案與目前版本不一致。");
            else PostState("ready");
        }
        catch (Exception e) when (e is PatcherException or HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            Log("Update check failed", e);
            _installed = new Updater(_layout, _repo).LoadState();
            // Offline but installed: the game can still start (the update server and the game server are separate).
            if (_installed is not null)
                PostState("ready", "目前無法連線至更新伺服器，仍可啟動已安裝的版本。");
            else if (HasLocalClient())
                PostState("offline-ready", "目前無法連線至更新伺服器；可啟動本機客戶端進行測試，但尚未核對更新版本。");
            else
                PostState("offline");
        }
    }

    async Task UpdateAsync(bool verifyAll)
    {
        if (_channel is null)
        {
            await CheckAsync();
            if (_channel is null) return;
        }
        Directory.CreateDirectory(_layout.GameDirectory);
        _work = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        PostState("updating");
        var meter = new SpeedMeter();
        var progress = new ThrottledProgress(p =>
        {
            var (rate, eta) = p.Stage == UpdateStage.Downloading ? meter.Sample(p.BytesDone, p.BytesTotal) : (0, 0);
            _post(new { type = "progress", stage = p.Stage.ToString().ToLowerInvariant(), done = p.BytesDone,
                total = p.BytesTotal, file = p.File, rate, eta });
            var now = DateTimeOffset.UtcNow;
            if (_loggedStage != p.Stage || now - _lastProgressLog >= TimeSpan.FromSeconds(5)
                || p.BytesTotal > 0 && p.BytesDone >= p.BytesTotal)
            {
                _loggedStage = p.Stage;
                _lastProgressLog = now;
                Log($"Update {p.Stage}: {p.BytesDone}/{p.BytesTotal}" +
                    (string.IsNullOrWhiteSpace(p.File) ? "" : $"; file={p.File}"));
            }
        });
        try
        {
            var updater = new Updater(_layout, _repo);
            var channel = _channel;
            var token = _work.Token;
            var summary = await Task.Run(() => updater.UpdateAsync(channel, verifyAll, progress, token));
            Log($"Update completed: version={summary.Version}; changed={summary.ChangedFiles}; deleted={summary.DeletedFiles}; downloaded={summary.DownloadedBytes}; reused={summary.ReusedBytes}");
            _work.Dispose();
            _work = null;
            await CheckAsync();
        }
        catch (OperationCanceledException)
        {
            Log("Update paused by cancellation");
            _work?.Dispose();
            _work = null;
            PostState(_installed is null ? "install" : "update", "下載已暫停；已下載的內容會保留，之後可繼續。");
        }
        catch (Exception e)
        {
            Log("Update failed", e);
            _work?.Dispose();
            _work = null;
            PostState("error", $"更新失敗：{e.Message}");
        }
    }

    async Task PlayAsync()
    {
        LaunchInfo? launch = _installed?.Launch;
        if (launch is null && HasLocalClient()) launch = new LaunchInfo { Exe = "ro3.exe" };
        PostState("launching");
        try
        {
            if (launch is null) throw new PatcherException("No packaged client is installed.");
            if (_game is { HasExited: false }) return;
            _game?.Dispose();
            _game = null;
            var started = await GameStarter.StartAsync(_layout.GameDirectory, launch);
            if (started.Process is { } game)
            {
                _game = game;
                Log($"Game running: pid={game.Id}");
                PostState("running");
                _minimize();
                _ = ObserveGameAsync(game);
            }
            else PostState("repair", $"遊戲未能啟動：{started.Failure}。");
        }
        catch (Exception e)
        {
            Log("Game launch failed", e);
            PostState("error", $"啟動失敗：{e.Message}");
        }
    }

    async Task ObserveGameAsync(Process game)
    {
        try
        {
            await game.WaitForExitAsync(_life.Token);
            int exitCode = game.ExitCode;
            Log($"Game exited: pid={game.Id}; code={exitCode}");
            if (!ReferenceEquals(_game, game) || _life.IsCancellationRequested)
            {
                game.Dispose();
                return;
            }
            _game = null;
            game.Dispose();
            if (exitCode == 0)
                PostState(_installed is null ? "offline-ready" : "ready", "遊戲已關閉，可以再次啟動。");
            else
                PostState("repair", $"遊戲意外結束（代碼 {exitCode}），建議先執行修復遊戲。");
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
            if (ReferenceEquals(_game, game)) _game = null;
            game.Dispose();
        }
        catch (Exception e)
        {
            Log("Game process monitoring failed", e);
            if (ReferenceEquals(_game, game))
            {
                _game = null;
                game.Dispose();
                if (!_life.IsCancellationRequested) PostState("repair", "無法追蹤遊戲程序，請重新檢查或修復遊戲。");
            }
        }
    }

    bool HasLocalClient() => File.Exists(Path.Combine(_layout.GameDirectory, "ro3.exe"));

    async Task ChangeDirectoryAsync()
    {
        if (_work != null) return;
        string? folder = _pickFolder("遊戲資料夾（既有的 ROO_PC 資料夾，或空白資料夾）");
        if (folder is null) return;
        // An existing client (ro3.exe) or an empty folder is used as is; anything else gets its own subfolder.
        bool usable = File.Exists(Path.Combine(folder, "ro3.exe")) || !Directory.EnumerateFileSystemEntries(folder).Any();
        if (!usable) folder = Path.Combine(folder, "Ragnarok3");
        _settings.ClientDirectory = folder;
        _settings.Save();
        _layout = new InstallLayout(folder);
        _installed = null;
        PostSettings();
        await CheckAsync();
    }

    async Task PingLoopAsync()
    {
        if (string.IsNullOrEmpty(_config.Server)) return;
        string server = _config.Server.TrimEnd('/');
        while (!_life.IsCancellationRequested)
        {
            bool online = false;
            int? players = null;
            try
            {
                using var status = await _http.GetAsync(server + "/ro3emu/status", _life.Token);
                if (status.IsSuccessStatusCode)
                {
                    online = true;
                    using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync(_life.Token));
                    if (json.RootElement.TryGetProperty("players", out var p)) players = p.GetInt32();
                }
                else
                {
                    // Servers without /ro3emu/status: any answer from the gate-key endpoint means up.
                    using var key = await _http.GetAsync(server + "/ro3emu/gate-key.pem", _life.Token);
                    online = key.IsSuccessStatusCode;
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { }
            if (_life.IsCancellationRequested) return;
            _post(new { type = "server", online, players });
            try { await Task.Delay(TimeSpan.FromSeconds(30), _life.Token); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>Loads only the content-addressed news document authenticated by the current channel.</summary>
    async Task LoadNewsAsync()
    {
        if (_channel?.News is not { } release) return;
        try
        {
            byte[]? bytes = await _repo.GetVerifiedBytesAsync(release, _life.Token);
            if (bytes is null) return;
            using var json = JsonDocument.Parse(bytes);
            bool hasItems = json.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array;
            bool hasBanners = json.RootElement.TryGetProperty("banners", out var bannerItems) && bannerItems.ValueKind == JsonValueKind.Array;
            if (hasItems || hasBanners)
            {
                JsonElement? notices = hasItems ? items.Clone() : null;
                JsonElement? banners = hasBanners ? bannerItems.Clone() : null;
                _post(new { type = "news", items = notices, banners, trusted = _config.RequireSignedChannel });
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or PatcherException) { }
    }

    public void Dispose()
    {
        _life.Cancel();
        _work?.Cancel();
        Log("Launcher closed");
    }

    void Log(string message, Exception? error = null)
    {
        try
        {
            lock (_logGate)
            {
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 2 * 1024 * 1024)
                    File.Move(_logPath, _logPath + ".old", overwrite: true);
                File.AppendAllText(_logPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}" +
                    (error is null ? "" : error + Environment.NewLine));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Download speed over the last ~5 seconds and the remaining seconds at that speed.</summary>
    sealed class SpeedMeter
    {
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly Queue<(double At, long Bytes)> _samples = new();

        public (double Rate, double Eta) Sample(long done, long total)
        {
            double now = _clock.Elapsed.TotalSeconds;
            _samples.Enqueue((now, done));
            while (_samples.Count > 2 && now - _samples.Peek().At > 5) _samples.Dequeue();
            var first = _samples.Peek();
            double seconds = now - first.At;
            if (seconds < 1) return (0, 0);
            double rate = (done - first.Bytes) / seconds;
            return rate <= 0 ? (0, 0) : (rate, (total - done) / rate);
        }
    }
}
